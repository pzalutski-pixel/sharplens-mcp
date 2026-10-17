using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// find_similar_code: structural similarity search — the anti-duplication tool
// for agents about to reimplement an existing helper. A body's CONTENT is
// fingerprinted as a multiset of 4-gram shingles over its normalized token
// kinds (every identifier → ID, every literal → LIT, keywords/punctuation
// keep their kind), so renaming variables or changing constants does not
// change the fingerprint. The content is the same thing for a query method, a
// snippet and a candidate: a block body's statements (without its braces), an
// expression body's expression (without `=>` and `;`), a snippet's statements
// (without enclosing braces, if it has them). Score = multiset Jaccard similarity.
// Each candidate is reported once whatever the number of target frameworks that
// compile it, and the query method never matches itself. No persistent index:
// every call sweeps the solution's source methods once (audit-time tool,
// documented as such in the schema).
public partial class RoslynService
{
    private const int ShingleSize = 4;
    private const int MinCandidateStatements = 3;
    private const int MaxSimilarCodeResults = 1000;

    public async Task<object> FindSimilarCodeAsync(
        string? typeName = null,
        string? methodName = null,
        int? overloadIndex = null,
        string? snippet = null,
        double threshold = 0.75,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var nameMode = typeName != null || methodName != null;
        if (nameMode == (snippet != null))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "Supply either typeName+methodName (compare an existing method) or snippet (compare raw code), not both and not neither.",
                context: new { typeName, methodName, snippetSupplied = snippet != null });
        }
        if (nameMode && (typeName == null || methodName == null))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "Name-based queries require both typeName and methodName.",
                context: new { typeName, methodName });
        }
        if (threshold is <= 0 or > 1)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"threshold must be greater than 0 and at most 1 (got {threshold})",
                context: new { threshold });
        }
        if (ParameterValidation.Limit("maxResults", maxResults, MaxSimilarCodeResults) is { } invalid)
            return CreateErrorResponse(invalid);

        Dictionary<string, int> queryFingerprint;
        SourceSpanKey? querySpan = null;
        string queryDescription;
        // Set when typeName resolved to a source type that shadows same-named framework types.
        string? notice = null;

        if (nameMode)
        {
            var resolution = await ResolveMemberByNameAsync(typeName!, methodName, overloadIndex, cancellationToken);
            if (resolution.Symbol is not { } symbol) return resolution.Error!;
            notice = resolution.Type?.ShadowNotice;
            if (symbol is not IMethodSymbol queryMethod || queryMethod.DeclaringSyntaxReferences.Length == 0)
            {
                return CreateErrorResponse(
                    ErrorCodes.NotAMethod,
                    $"'{symbol.ToDisplayString()}' is not a method declared in source",
                    context: new { typeName, methodName });
            }

            // A partial method splits across declarations; only the implementing
            // part has a body, and it may even be a separate symbol
            // (PartialImplementationPart). Search every declaration of both parts
            // for the one that actually carries a body, rather than blindly
            // fingerprinting reference [0].
            var queryDeclRefs = queryMethod.DeclaringSyntaxReferences
                .Concat(queryMethod.PartialImplementationPart?.DeclaringSyntaxReferences ?? Enumerable.Empty<SyntaxReference>())
                .Concat(queryMethod.PartialDefinitionPart?.DeclaringSyntaxReferences ?? Enumerable.Empty<SyntaxReference>());
            MethodDeclarationSyntax? queryDecl = null;
            IEnumerable<SyntaxToken>? queryContent = null;
            foreach (var declRef in queryDeclRefs)
            {
                if (await declRef.GetSyntaxAsync(cancellationToken) is MethodDeclarationSyntax decl
                    && BodyContentTokens(decl) is { } content)
                {
                    queryDecl = decl;
                    queryContent = content;
                    break;
                }
            }
            if (queryDecl == null || queryContent == null)
            {
                return CreateErrorResponse(
                    ErrorCodes.AnalysisFailed,
                    $"'{symbol.ToDisplayString()}' has no body to fingerprint (abstract, extern, or partial declaration)",
                    context: new { typeName, methodName });
            }

            queryFingerprint = FingerprintTokens(queryContent);
            querySpan = new SourceSpanKey(queryDecl.SyntaxTree.FilePath, queryDecl.Span);
            queryDescription = queryMethod.ToDisplayString();
        }
        else
        {
            queryFingerprint = FingerprintTokens(SnippetContentTokens(snippet!));
            queryDescription = "snippet";
        }

        if (queryFingerprint.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"The query is too short to fingerprint: it produces no {ShingleSize}-token shingles",
                hint: "Query a method body (or snippet) with at least a few statements.",
                context: new { query = queryDescription });
        }

        var matches = new List<(string Method, ResultLocation Location, SourceSpanKey Span, double Score, int StatementCount, string? TargetFramework)>();

        foreach (var project in _solution!.Projects)
        {
            var targetFramework = LogicalProject.TargetFrameworkOf(project);
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null) continue;

                foreach (var candidate in root.DescendantNodes().Where(IsSimilarityCandidate))
                {
                    // Skip trivial BLOCK bodies that match everything; expression bodies
                    // carry no statements but self-filter — too few tokens to form a
                    // shingle scores 0. statementCount reports the block's count, or 1 for
                    // an expression body.
                    if (BodyContentTokens(candidate) is not { } content) continue;
                    var blockBody = BlockBodyOf(candidate);
                    var statementCount = blockBody?.Statements.Count ?? 1;
                    if (blockBody != null && statementCount < MinCandidateStatements) continue;
                    var span = new SourceSpanKey(candidate.SyntaxTree.FilePath, candidate.Span);
                    if (querySpan is { } self && span.Equals(self)) continue;

                    var score = JaccardSimilarity(queryFingerprint, FingerprintTokens(content));
                    if (score < threshold) continue;

                    var memberName = CandidateMemberName(candidate);
                    var containingType = candidate.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                        .FirstOrDefault()?.Identifier.Text;
                    var start = candidate.GetLocation().GetLineSpan().StartLinePosition;
                    matches.Add((
                        containingType != null ? $"{containingType}.{memberName}" : memberName,
                        new ResultLocation(FormatPath(document.FilePath), start.Line, start.Character),
                        span,
                        score,
                        statementCount,
                        targetFramework));
                }
            }
        }

        var ordered = TargetFrameworkDeduplication.ByLocation(matches, m => m.Span, m => m.TargetFramework)
            .OrderByDescending(m => m.Item.Score)
            .ThenBy(m => m.Item.Method, StringComparer.Ordinal)
            .ThenBy(m => m.Item.Location, ResultLocationComparer.Instance)
            .ToList();
        var returned = ordered.Take(maxResults).ToList();

        return CreateSuccessResponse(
            data: new
            {
                query = queryDescription,
                notice,
                threshold,
                results = returned.Select(m => new
                {
                    method = m.Item.Method,
                    location = new { filePath = m.Item.Location.FilePath, line = m.Item.Location.Line },
                    score = Math.Round(m.Item.Score, 3),
                    statementCount = m.Item.StatementCount,
                    targetFrameworks = FrameworksOrNull(m.TargetFrameworks)
                }).ToList()
            },
            ResultCounts.Capped(ordered.Count, returned.Count),
            suggestedNextTools: returned.Count > 0
                ? new[]
                {
                    "get_method_source on a match to compare implementations before writing a new one",
                    "Reuse or extract the existing helper instead of duplicating it"
                }
                : new[] { "No structural twins at this threshold; lower threshold to widen the net" });
    }

    // The fingerprinted content of a method-like member's body: a block's statements
    // (between its braces), or an expression body's expression (without `=>` and `;`).
    // Null for a member without a body.
    private static IEnumerable<SyntaxToken>? BodyContentTokens(SyntaxNode node)
    {
        var (block, arrow) = node switch
        {
            BaseMethodDeclarationSyntax m => (m.Body, m.ExpressionBody),
            LocalFunctionStatementSyntax lf => (lf.Body, lf.ExpressionBody),
            AccessorDeclarationSyntax a => (a.Body, a.ExpressionBody),
            PropertyDeclarationSyntax p => ((BlockSyntax?)null, p.ExpressionBody),
            IndexerDeclarationSyntax i => ((BlockSyntax?)null, i.ExpressionBody),
            _ => ((BlockSyntax?)null, (ArrowExpressionClauseSyntax?)null)
        };
        if (block != null) return block.Statements.SelectMany(s => s.DescendantTokens());
        return arrow?.Expression.DescendantTokens();
    }

    // A snippet's content: its statements, without the braces of a block that encloses the
    // whole snippet — the shape BodyContentTokens gives a method body.
    private static IEnumerable<SyntaxToken> SnippetContentTokens(string snippet)
    {
        var block = (BlockSyntax)SyntaxFactory.ParseStatement("{" + snippet + "\n}");
        while (block.Statements.Count == 1 && block.Statements[0] is BlockSyntax inner)
        {
            block = inner;
        }
        return block.Statements.SelectMany(s => s.DescendantTokens());
    }

    // The block body (if any) of a method-like member, for the statement-count floor.
    private static BlockSyntax? BlockBodyOf(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax m => m.Body,
        LocalFunctionStatementSyntax lf => lf.Body,
        AccessorDeclarationSyntax a => a.Body,
        _ => null
    };

    // Whether a node is a member find_similar_code fingerprints: methods, constructors,
    // destructors, operators, conversions, local functions, accessors, and expression-bodied
    // properties and indexers.
    private static bool IsSimilarityCandidate(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
            or PropertyDeclarationSyntax { ExpressionBody: not null }
            or IndexerDeclarationSyntax { ExpressionBody: not null };

    private static string CandidateMemberName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
        OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax co => "operator " + co.Type,
        LocalFunctionStatementSyntax lf => lf.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        AccessorDeclarationSyntax a => $"{AccessorOwnerName(a)}.{a.Keyword.Text}",
        _ => "<member>"
    };

    // Multiset of ShingleSize-grams over the normalized token-kind sequence. Tokens the
    // parser inserted for missing syntax (zero-width) are not part of the code.
    internal static Dictionary<string, int> FingerprintTokens(IEnumerable<SyntaxToken> tokens)
    {
        var kinds = new List<string>();
        foreach (var token in tokens)
        {
            var kind = token.Kind();
            if (kind == SyntaxKind.EndOfFileToken || token.IsMissing) continue;
            kinds.Add(kind switch
            {
                SyntaxKind.IdentifierToken => "ID",
                SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken
                    or SyntaxKind.CharacterLiteralToken or SyntaxKind.InterpolatedStringTextToken
                    or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                    or SyntaxKind.Utf8StringLiteralToken => "LIT",
                _ => kind.ToString()
            });
        }

        var shingles = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + ShingleSize <= kinds.Count; i++)
        {
            var shingle = string.Join("|", kinds.GetRange(i, ShingleSize));
            shingles[shingle] = shingles.TryGetValue(shingle, out var count) ? count + 1 : 1;
        }
        return shingles;
    }

    // Multiset Jaccard: sum of per-shingle minimum counts over sum of maximums.
    internal static double JaccardSimilarity(Dictionary<string, int> left, Dictionary<string, int> right)
    {
        if (left.Count == 0 && right.Count == 0) return 0;

        var intersection = 0;
        var union = 0;
        foreach (var (shingle, leftCount) in left)
        {
            var rightCount = right.GetValueOrDefault(shingle);
            intersection += Math.Min(leftCount, rightCount);
            union += Math.Max(leftCount, rightCount);
        }
        foreach (var (shingle, rightCount) in right)
        {
            if (!left.ContainsKey(shingle)) union += rightCount;
        }
        return union == 0 ? 0 : (double)intersection / union;
    }
}
