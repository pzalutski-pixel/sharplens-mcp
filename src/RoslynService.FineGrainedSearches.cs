using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SharpLensMcp;

// Fine-grained usage searches over one shared scanner: where a type is constructed
// (find_type_instantiations), pattern-matched (find_pattern_usages), thrown
// (find_throw_sites), and caught (find_catch_blocks). Each tool supplies a node matcher;
// the scanner walks the solution's own documents (no generated trees, no metadata) in
// every project, a multi-targeted project's shared files once per target framework so
// #if branches are all seen, and reports each site once as {location, lineText, kind}, in
// canonical location order before the cap. Types are compared by the solution's
// cross-compilation identity (CrossCompilationSymbolComparer over OriginalDefinitions),
// so a type bound in another project's compilation matches and two same-named types of
// different assemblies do not; includeDerived extends the throw/catch pair to derived
// types. The target type's shadow notice is reported as data.notice.
public partial class RoslynService
{
    private const int MaxTypeUsageResults = 1000;

    public Task<object> FindTypeInstantiationsAsync(
        string typeName, int maxResults = 100, CancellationToken cancellationToken = default) =>
        ScanTypeUsagesAsync(typeName, includeDerived: false, maxResults, MatchInstantiation, cancellationToken);

    public Task<object> FindPatternUsagesAsync(
        string typeName, int maxResults = 100, CancellationToken cancellationToken = default) =>
        ScanTypeUsagesAsync(typeName, includeDerived: false, maxResults, MatchPattern, cancellationToken);

    public Task<object> FindThrowSitesAsync(
        string typeName, bool includeDerived = true, int maxResults = 100,
        CancellationToken cancellationToken = default) =>
        ScanTypeUsagesAsync(typeName, includeDerived, maxResults, MatchThrow, cancellationToken);

    public Task<object> FindCatchBlocksAsync(
        string typeName, bool includeDerived = true, int maxResults = 100,
        CancellationToken cancellationToken = default) =>
        ScanTypeUsagesAsync(typeName, includeDerived, maxResults, MatchCatch, cancellationToken);

    // A matcher's verdict on a node: the kind of usage and the node to report it at.
    private delegate (string kind, SyntaxNode site)? UsageMatcher(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct);

    private async Task<object> ScanTypeUsagesAsync(
        string typeName, bool includeDerived, int maxResults, UsageMatcher matcher, CancellationToken cancellationToken)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxTypeUsageResults) is { } invalid) return CreateErrorResponse(invalid);

        var resolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        if (resolution.Error != null) return resolution.Error;
        if (resolution.Type is not { } target)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName });
        }

        var found = new List<(ResultLocation location, string lineText, string kind)>();
        // A multi-targeted project compiles each shared file once per target framework;
        // a site is one result whichever compilation reported it.
        var seen = new HashSet<SourceSpanKey>();

        foreach (var project in GetLogicalProjects().Projects.SelectMany(p => p.Projects))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!IsSolutionSourceTree(tree)) continue;
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken);

                foreach (var node in root.DescendantNodes())
                {
                    if (matcher(node, semanticModel, target, includeDerived, cancellationToken) is not { } match) continue;
                    if (!seen.Add(new SourceSpanKey(tree.FilePath, match.site.Span))) continue;

                    var lineSpan = match.site.GetLocation().GetLineSpan();
                    found.Add((
                        new ResultLocation(FormatPath(lineSpan.Path), lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                        tree.GetText(cancellationToken).Lines[lineSpan.StartLinePosition.Line].ToString().Trim(),
                        match.kind));
                }
            }
        }

        var ordered = CanonicalOrder.ByLocation(found, f => f.location);
        var matches = ordered.Take(maxResults).Select(f => new
        {
            location = new
            {
                filePath = f.location.FilePath,
                line = f.location.Line,
                column = f.location.Column
            },
            lineText = f.lineText,
            kind = f.kind
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                typeName = target.ToDisplayString(),
                includeDerived,
                matches,
                notice = resolution.ShadowNotice
            },
            ResultCounts.Capped(ordered.Count, matches.Count),
            suggestedNextTools: new[] { "get_method_source on a result's containing method for full context" });
    }

    private static (string kind, SyntaxNode site)? MatchInstantiation(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct) =>
        node is BaseObjectCreationExpressionSyntax creation
        && TypeMatches(model.GetTypeInfo(creation, ct).Type, target, includeDerived: false)
            ? ("objectCreation", creation)
            : null;

    // Matches the type tests and patterns the bound tree resolves to the target type:
    // `is T` (isExpression), `as T` (asExpression), and every declaration, type and
    // recursive pattern whose matched type is the target — at the top of an is/switch, in
    // a `case T:` label, under not/and/or/parentheses, as a list-pattern element, or nested
    // in a property subpattern. A type-less recursive pattern (`{ Total: > 5 }`) matches
    // against its input's type, which the bound pattern carries. A pattern the compiler
    // implies (the nested pattern of an extended property pattern `{ A.B: 3 }`) is not one
    // the code spells, and is not reported.
    private static (string kind, SyntaxNode site)? MatchPattern(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct)
    {
        switch (node)
        {
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.IsExpression):
                return model.GetOperation(binary, ct) is IIsTypeOperation isType && TypeMatches(isType.TypeOperand, target, false)
                    ? ("isExpression", binary)
                    : null;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AsExpression):
                return model.GetOperation(binary, ct) is IConversionOperation { IsTryCast: true } tryCast
                       && TypeMatches(tryCast.Type, target, false)
                    ? ("asExpression", binary)
                    : null;
            // `case T:` spells a type pattern with an expression; the bound pattern is
            // marked implicit, but the code names it.
            case CaseSwitchLabelSyntax label:
                return model.GetOperation(label, ct) is IPatternCaseClauseOperation { Pattern: { } labelPattern }
                    ? MatchPatternOperation(labelPattern, target)
                    : null;
            case PatternSyntax pattern:
                return model.GetOperation(pattern, ct) is IPatternOperation { IsImplicit: false } operation && operation.Syntax == pattern
                    ? MatchPatternOperation(operation, target)
                    : null;
            default:
                return null;
        }
    }

    private static (string kind, SyntaxNode site)? MatchPatternOperation(IPatternOperation pattern, INamedTypeSymbol target)
    {
        var (kind, matched) = pattern switch
        {
            IDeclarationPatternOperation declaration => ("declarationPattern", declaration.MatchedType),
            ITypePatternOperation type => ("typePattern", type.MatchedType),
            IRecursivePatternOperation recursive => ("recursivePattern", recursive.MatchedType),
            _ => (null, null)
        };
        return kind != null && TypeMatches(matched, target, false) ? (kind, pattern.Syntax) : null;
    }

    private static (string kind, SyntaxNode site)? MatchThrow(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct)
    {
        var thrown = node switch
        {
            ThrowStatementSyntax { Expression: { } expression } => expression,
            ThrowExpressionSyntax throwExpression => throwExpression.Expression,
            _ => null
        };
        return thrown != null && TypeMatches(model.GetTypeInfo(thrown, ct).Type, target, includeDerived)
            ? ("throw", node)
            : null;
    }

    // Catch semantics are the inverse of throw: a `catch (C)` catches the target T when C
    // IS T or C is a BASE of T, so with includeDerived the TARGET is tested for deriving
    // from the caught type. A catch with no exception type (`catch { }`) catches every
    // exception — a base of every T — and is reported as catchAll with includeDerived.
    private static (string kind, SyntaxNode site)? MatchCatch(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct)
    {
        if (node is not CatchClauseSyntax catchClause) return null;
        if (catchClause.Declaration is not { Type: { } caughtTypeSyntax })
        {
            return includeDerived ? ("catchAll", catchClause) : null;
        }
        var caught = model.GetTypeInfo(caughtTypeSyntax, ct).Type;
        if (caught == null) return null;
        if (SameDefinition(target, caught)) return ("catch", catchClause);
        if (!includeDerived) return null;
        for (var baseType = target.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (SameDefinition(baseType, caught)) return ("catch", catchClause);
        }
        return null;
    }

    // Whether a candidate type is the target (with includeDerived, also a type that derives
    // from or implements it), by cross-compilation identity of their definitions.
    private static bool TypeMatches(ITypeSymbol? candidate, INamedTypeSymbol target, bool includeDerived)
    {
        if (candidate == null) return false;
        if (SameDefinition(candidate, target)) return true;
        if (!includeDerived) return false;

        for (var baseType = candidate.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (SameDefinition(baseType, target)) return true;
        }
        return candidate.AllInterfaces.Any(i => SameDefinition(i, target));
    }

    private static bool SameDefinition(ITypeSymbol x, ITypeSymbol y) =>
        CrossCompilationSymbolComparer.Instance.Equals(x.OriginalDefinition, y.OriginalDefinition);
}
