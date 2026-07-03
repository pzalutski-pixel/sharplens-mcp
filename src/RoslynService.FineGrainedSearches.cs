using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// Fine-grained usage searches over one shared scanner: where a type is
// constructed (find_type_instantiations), pattern-matched (find_pattern_usages),
// thrown (find_throw_sites), and caught (find_catch_blocks). Each tool supplies
// a node matcher; the scanner walks every source tree of every project's base
// compilation (source only — no generated trees, no metadata) and reports
// {location, lineText, kind}. Type identity is compared across compilations via
// CrossCompilationSymbol.TypeKey, with an includeDerived option for the
// throw/catch pair.
public partial class RoslynService
{
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

    private async Task<object> ScanTypeUsagesAsync(
        string typeName, bool includeDerived, int maxResults,
        Func<SyntaxNode, SemanticModel, INamedTypeSymbol, bool, CancellationToken, string?> matcher,
        CancellationToken cancellationToken)
    {
        EnsureSolutionLoaded();

        if (maxResults is < 1 or > 1000)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxResults must be between 1 and 1000 (got {maxResults})",
                context: new { maxResults });
        }

        var (target, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
        if (target == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName });
        }

        var matches = new List<object>();
        var totalFound = 0;
        // A multi-targeted project appears once per TFM, each compiling the same
        // shared source files; dedup hits by physical path + span so they are not
        // counted once per target framework.
        var seen = new HashSet<(string path, int start, int length)>();

        foreach (var project in _solution!.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Base compilation: its syntax trees are exactly this project's own
            // source documents — no generated trees, no referenced assemblies.
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken);
                SourceText? text = null;

                foreach (var node in root.DescendantNodes())
                {
                    var kind = matcher(node, semanticModel, target, includeDerived, cancellationToken);
                    if (kind == null) continue;

                    if (!seen.Add((tree.FilePath ?? string.Empty, node.Span.Start, node.Span.Length))) continue;

                    totalFound++;
                    if (matches.Count >= maxResults) continue;

                    var lineSpan = node.GetLocation().GetLineSpan();
                    text ??= tree.GetText(cancellationToken);
                    matches.Add(new
                    {
                        location = new
                        {
                            filePath = FormatPath(lineSpan.Path),
                            line = lineSpan.StartLinePosition.Line,
                            column = lineSpan.StartLinePosition.Character
                        },
                        lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim(),
                        kind
                    });
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                typeName = target.ToDisplayString(),
                includeDerived,
                matches
            },
            suggestedNextTools: new[] { "get_method_source on a result's containing method for full context" },
            totalCount: totalFound,
            returnedCount: matches.Count);
    }

    private static string? MatchInstantiation(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct) =>
        node switch
        {
            ObjectCreationExpressionSyntax oc when TypeMatches(model.GetTypeInfo(oc, ct).Type, target, false)
                => "objectCreation",
            ImplicitObjectCreationExpressionSyntax ioc when TypeMatches(model.GetTypeInfo(ioc, ct).Type, target, false)
                => "objectCreation",
            _ => null
        };

    // Matches patterns that name a TYPE (`is T`, `as T`, declaration/type/recursive
    // patterns whose type token is the target) AND type-LESS recursive/property
    // patterns (a bare `{ Prop: ... }` with no type token): those are attributed to
    // the type of the expression they match — the `is`/switch input at the top level,
    // or the member's type when nested in a property subpattern (see PatternInputType).
    // Positional deconstruction elements are not resolved.
    private static string? MatchPattern(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct) =>
        node switch
        {
            BinaryExpressionSyntax be when be.IsKind(SyntaxKind.IsExpression)
                && TypeMatches(model.GetTypeInfo(be.Right, ct).Type, target, false) => "isExpression",
            BinaryExpressionSyntax be when be.IsKind(SyntaxKind.AsExpression)
                && TypeMatches(model.GetTypeInfo(be.Right, ct).Type, target, false) => "asExpression",
            DeclarationPatternSyntax dp when TypeMatches(model.GetTypeInfo(dp.Type, ct).Type, target, false)
                => "declarationPattern",
            TypePatternSyntax tp when TypeMatches(model.GetTypeInfo(tp.Type, ct).Type, target, false)
                => "typePattern",
            // A bare type in pattern position (e.g. a switch arm `T => ...`) can
            // parse as a constant pattern whose expression binds to the type.
            ConstantPatternSyntax cp when model.GetSymbolInfo(cp.Expression, ct).Symbol is ITypeSymbol constantType
                && TypeMatches(constantType, target, false) => "typePattern",
            RecursivePatternSyntax { Type: { } recursiveType } when TypeMatches(model.GetTypeInfo(recursiveType, ct).Type, target, false)
                => "recursivePattern",
            // A type-less recursive/positional/property pattern (`order is { Total: … }`,
            // `p is (var x, var y)`) carries no type of its own; it matches against the
            // type of the expression being matched, so resolve that input's type.
            RecursivePatternSyntax { Type: null } rp when PatternInputType(rp, model, ct) is { } inputType
                && TypeMatches(inputType, target, false) => "recursivePattern",
            _ => null
        };

    // The type a pattern is matched against: the `is` operand, the switch-expression
    // governing expression, or the switch-statement expression. Used to attribute a
    // type-less pattern to the type whose shape it inspects.
    private static ITypeSymbol? PatternInputType(
        PatternSyntax pattern, SemanticModel model, CancellationToken ct) => pattern.Parent switch
    {
        IsPatternExpressionSyntax ip when ip.Pattern == pattern
            => model.GetTypeInfo(ip.Expression, ct).Type,
        SwitchExpressionArmSyntax arm when arm.Pattern == pattern && arm.Parent is SwitchExpressionSyntax se
            => model.GetTypeInfo(se.GoverningExpression, ct).Type,
        CasePatternSwitchLabelSyntax label when label.Pattern == pattern
            => label.FirstAncestorOrSelf<SwitchStatementSyntax>() is { } sw
                ? model.GetTypeInfo(sw.Expression, ct).Type
                : null,
        // A pattern nested in a property subpattern (`{ Member: <pattern> }`) matches
        // against the member's type; the subpattern's name binds to that member.
        SubpatternSyntax { NameColon.Name: { } memberName }
            => model.GetSymbolInfo(memberName, ct).Symbol switch
            {
                IPropertySymbol prop => prop.Type,
                IFieldSymbol field => field.Type,
                _ => null
            },
        _ => null
    };

    private static string? MatchThrow(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct) =>
        node switch
        {
            ThrowStatementSyntax { Expression: { } thrown } when TypeMatches(model.GetTypeInfo(thrown, ct).Type, target, includeDerived)
                => "throw",
            ThrowExpressionSyntax te when TypeMatches(model.GetTypeInfo(te.Expression, ct).Type, target, includeDerived)
                => "throw",
            _ => null
        };

    // Catch semantics are the inverse of throw: a `catch (C)` catches the
    // target T when C IS T or C is a BASE of T. So with includeDerived we test
    // whether the TARGET derives from the caught type, not the other way round.
    private static string? MatchCatch(
        SyntaxNode node, SemanticModel model, INamedTypeSymbol target, bool includeDerived, CancellationToken ct)
    {
        if (node is not CatchClauseSyntax { Declaration.Type: { } caughtTypeSyntax }) return null;
        var caught = model.GetTypeInfo(caughtTypeSyntax, ct).Type;
        if (caught == null) return null;

        var caughtName = CrossCompilationSymbol.TypeKey(caught);
        if (CrossCompilationSymbol.TypeKey(target) == caughtName) return "catch";
        if (!includeDerived) return null;

        for (var baseType = target.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (CrossCompilationSymbol.TypeKey(baseType) == caughtName) return "catch";
        }
        return null;
    }

    // Cross-compilation type identity via CrossCompilationSymbol.TypeKey; with
    // includeDerived, also matches when the candidate derives from (or implements)
    // the target.
    private static bool TypeMatches(ITypeSymbol? candidate, INamedTypeSymbol target, bool includeDerived)
    {
        if (candidate == null) return false;
        var targetName = CrossCompilationSymbol.TypeKey(target);
        if (CrossCompilationSymbol.TypeKey(candidate) == targetName) return true;
        if (!includeDerived) return false;

        for (var baseType = candidate.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (CrossCompilationSymbol.TypeKey(baseType) == targetName) return true;
        }
        return candidate.AllInterfaces.Any(i => CrossCompilationSymbol.TypeKey(i) == targetName);
    }
}
