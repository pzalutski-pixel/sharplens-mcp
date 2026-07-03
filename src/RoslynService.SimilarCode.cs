using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// find_similar_code: structural similarity search — the anti-duplication tool
// for agents about to reimplement an existing helper. Method bodies are
// fingerprinted as a multiset of 4-gram shingles over their normalized token
// kinds (every identifier → ID, every literal → LIT, keywords/punctuation
// keep their kind), so renaming variables or changing constants does not
// change the fingerprint. Score = multiset Jaccard similarity. No persistent
// index: every call sweeps the solution's source methods once (audit-time
// tool, documented as such in the schema).
public partial class RoslynService
{
    private const int ShingleSize = 4;
    private const int MinCandidateStatements = 3;

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
        if (maxResults < 1)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxResults must be at least 1 (got {maxResults})",
                context: new { maxResults });
        }

        Dictionary<string, int> queryFingerprint;
        SyntaxTree? queryTree = null;
        Microsoft.CodeAnalysis.Text.TextSpan querySpan = default;
        string queryDescription;

        if (nameMode)
        {
            var (symbol, symbolError) = await ResolveSymbolByNameAsync(
                typeName!, methodName, overloadIndex, cancellationToken);
            if (symbol == null) return symbolError!;
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
            SyntaxNode? queryBody = null;
            foreach (var declRef in queryDeclRefs)
            {
                if (await declRef.GetSyntaxAsync(cancellationToken) is MethodDeclarationSyntax decl
                    && BodyNodeOf(decl) is { } body)
                {
                    queryDecl = decl;
                    queryBody = body;
                    break;
                }
            }
            if (queryDecl == null || queryBody == null)
            {
                return CreateErrorResponse(
                    ErrorCodes.AnalysisFailed,
                    $"'{symbol.ToDisplayString()}' has no body to fingerprint (abstract, extern, or partial declaration)",
                    context: new { typeName, methodName });
            }

            queryFingerprint = FingerprintTokens(queryBody.DescendantTokens());
            queryTree = queryDecl.SyntaxTree;
            querySpan = queryDecl.Span;
            queryDescription = queryMethod.ToDisplayString();
        }
        else
        {
            queryFingerprint = FingerprintTokens(SyntaxFactory.ParseTokens(snippet!));
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

        var matches = new List<(string method, string? filePath, int line, double score, int statementCount)>();

        foreach (var project in _solution!.Projects)
        {
            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null) continue;

                foreach (var candidate in root.DescendantNodes().Where(IsSimilarityCandidate))
                {
                    // Fingerprint either body shape, symmetrically with the query
                    // (BodyNodeOf). Candidates span methods, constructors, operators,
                    // local functions, and accessors. Skip trivial BLOCK bodies that
                    // match everything; expression bodies carry no statements but
                    // self-filter — too few tokens to form a shingle scores 0.
                    // statementCount reports the block's count, or 1 for an expression body.
                    if (BodyNodeOf(candidate) is not { } candidateBody) continue;
                    var blockBody = BlockBodyOf(candidate);
                    var statementCount = blockBody?.Statements.Count ?? 1;
                    // The statement floor gates block bodies; expression bodies are gated
                    // instead by their token fingerprint (too few tokens to form a shingle
                    // scores 0), which is the meaningful triviality measure for one
                    // expression. Subjecting them to a statement count would wrongly drop
                    // substantial expression-bodied methods.
                    if (blockBody != null && statementCount < MinCandidateStatements) continue;
                    if (queryTree != null && candidate.SyntaxTree == queryTree && candidate.Span == querySpan) continue;

                    var score = JaccardSimilarity(
                        queryFingerprint, FingerprintTokens(candidateBody.DescendantTokens()));
                    if (score < threshold) continue;

                    var lineSpan = candidate.GetLocation().GetLineSpan();
                    var memberName = CandidateMemberName(candidate);
                    var containingType = candidate.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                        .FirstOrDefault()?.Identifier.Text;
                    matches.Add((
                        containingType != null ? $"{containingType}.{memberName}" : memberName,
                        FormatPath(document.FilePath),
                        lineSpan.StartLinePosition.Line,
                        score,
                        statementCount));
                }
            }
        }

        var ordered = matches.OrderByDescending(m => m.score).ThenBy(m => m.method, StringComparer.Ordinal).ToList();
        var returned = ordered.Take(maxResults).ToList();

        return CreateSuccessResponse(
            data: new
            {
                query = queryDescription,
                threshold,
                results = returned.Select(m => new
                {
                    m.method,
                    location = new { filePath = m.filePath, line = m.line },
                    score = Math.Round(m.score, 3),
                    m.statementCount
                }).ToList()
            },
            suggestedNextTools: returned.Count > 0
                ? new[]
                {
                    "get_method_source on a match to compare implementations before writing a new one",
                    "Reuse or extract the existing helper instead of duplicating it"
                }
                : new[] { "No structural twins at this threshold; lower threshold to widen the net" },
            totalCount: ordered.Count,
            returnedCount: returned.Count);
    }

    // The fingerprintable body of any method-like member — methods, constructors,
    // operators, destructors, local functions, and property/event accessors.
    private static SyntaxNode? BodyNodeOf(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
        LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.Body ?? lf.ExpressionBody,
        AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
        _ => null
    };

    // The block body (if any) of a method-like member, for the statement-count floor.
    private static BlockSyntax? BlockBodyOf(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax m => m.Body,
        LocalFunctionStatementSyntax lf => lf.Body,
        AccessorDeclarationSyntax a => a.Body,
        _ => null
    };

    // Whether a node is a method-like member that find_similar_code fingerprints.
    private static bool IsSimilarityCandidate(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax;

    private static string CandidateMemberName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        DestructorDeclarationSyntax d => "~" + d.Identifier.Text,
        OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax co => "operator " + co.Type,
        LocalFunctionStatementSyntax lf => lf.Identifier.Text,
        AccessorDeclarationSyntax a => AccessorMemberName(a),
        _ => "<member>"
    };

    private static string AccessorMemberName(AccessorDeclarationSyntax accessor)
    {
        var ownerName = accessor.Ancestors().OfType<BasePropertyDeclarationSyntax>().FirstOrDefault() switch
        {
            PropertyDeclarationSyntax p => p.Identifier.Text,
            IndexerDeclarationSyntax => "this[]",
            EventDeclarationSyntax e => e.Identifier.Text,
            _ => "?"
        };
        return $"{ownerName}.{accessor.Keyword.Text}";
    }

    // Multiset of ShingleSize-grams over the normalized token-kind sequence.
    internal static Dictionary<string, int> FingerprintTokens(IEnumerable<SyntaxToken> tokens)
    {
        var kinds = new List<string>();
        foreach (var token in tokens)
        {
            var kind = token.Kind();
            if (kind == SyntaxKind.EndOfFileToken) continue; // ParseTokens appends one; bodies have none
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
