using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// Generated-code reference discovery for the tools that label hits inside generator output
// (find_references, find_implementations, analyze_method's callers): a generated
// serializer calling your method, or a source-generated interface implementation. These
// scans walk the generated trees (those that are not one of the project's own documents)
// of the generator-augmented compilations and report the hits flagged inGeneratedCode; the
// tools de-duplicate them against SymbolFinder's results, which include source-generated
// documents. Only a project with source generators whose compilation is the target's own
// assembly or references it can contain a generated reference to it, so only those
// projects are scanned; SDK projects carry the SDK's generators, so that is usually every
// project that can see the target. A simple-name pre-filter keeps binding to the name
// nodes spelled like the target. find_callers and get_call_graph do not scan: they read
// callers from the call model (CallGraphSession), which binds generated bodies like any
// other.
public partial class RoslynService
{
    // A reference inside generated code. EnclosingSymbol is the member containing it (a
    // lambda's or local function's containing member), or the enclosing symbol Roslyn
    // reports for a non-member position (a field, a type).
    internal sealed record GeneratedReferenceHit(
        string HintPath, int Line, int Column, string LineText, string Kind, ISymbol? EnclosingSymbol);

    internal sealed record GeneratedImplementationHit(
        string Name, string Kind, string? ContainingNamespace, string HintPath, int Line, int Column);

    // References to `target` inside generated code: identifier/generic-name nodes that bind
    // to it, matched by the solution's cross-compilation identity (a generic instantiation,
    // a reduced extension method or either part of a partial member is the target).
    private async Task<List<GeneratedReferenceHit>> ScanGeneratedReferencesAsync(
        ISymbol target, CancellationToken cancellationToken)
    {
        var hits = new List<GeneratedReferenceHit>();
        var targetName = target.Name;

        await ForEachGeneratedTreeAsync(target, async (tree, semanticModel, root) =>
        {
            foreach (var node in root.DescendantNodes())
            {
                var simpleName = node switch
                {
                    GenericNameSyntax generic => generic.Identifier.Text,
                    IdentifierNameSyntax identifier => identifier.Identifier.Text,
                    _ => null
                };
                // The simple-name pre-filter skips the (expensive) bind for nodes whose
                // identifier text can't be the target. A generated reference reached via a
                // `using` alias (identifier text differs from the target's name) is not
                // matched; generators almost never emit aliases.
                if (simpleName != targetName) continue;

                var symbol = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol == null || !MatchesTarget(symbol, target)) continue;

                var lineSpan = tree.GetLineSpan(node.Span, cancellationToken);
                var lineText = tree.GetText(cancellationToken)
                    .Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                var enclosing = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken);
                while (enclosing is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction })
                {
                    enclosing = enclosing.ContainingSymbol;
                }
                hits.Add(new GeneratedReferenceHit(
                    tree.FilePath,
                    lineSpan.StartLinePosition.Line,
                    lineSpan.StartLinePosition.Character,
                    lineText,
                    ClassifyReferenceKind(node),
                    enclosing));
            }
            await Task.CompletedTask;
        }, cancellationToken);

        return hits;
    }

    // Types declared in generated code that implement or derive from `target`.
    private async Task<List<GeneratedImplementationHit>> ScanGeneratedImplementationsAsync(
        INamedTypeSymbol target, CancellationToken cancellationToken)
    {
        var hits = new List<GeneratedImplementationHit>();

        await ForEachGeneratedTreeAsync(target, async (tree, semanticModel, root) =>
        {
            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken)
                    is not INamedTypeSymbol declared) continue;

                var derivesFromTarget =
                    declared.AllInterfaces.Any(i => MatchesTarget(i, target))
                    || BaseChainContains(declared, target);
                if (!derivesFromTarget) continue;

                var lineSpan = tree.GetLineSpan(declaration.Identifier.Span, cancellationToken);
                hits.Add(new GeneratedImplementationHit(
                    declared.ToDisplayString(),
                    GetTypeKindString(declared),
                    declared.ContainingNamespace?.ToDisplayString(),
                    tree.FilePath,
                    lineSpan.StartLinePosition.Line,
                    lineSpan.StartLinePosition.Character));
            }
            await Task.CompletedTask;
        }, cancellationToken);

        return hits;
    }

    // Runs `visit` over each GENERATED tree (those not among the project's own documents)
    // of every project that has generators and can reference `target`: its compilation is
    // the target's assembly or references that assembly by name.
    private async Task ForEachGeneratedTreeAsync(
        ISymbol target, Func<SyntaxTree, SemanticModel, SyntaxNode, Task> visit, CancellationToken cancellationToken)
    {
        var targetAssembly = target.ContainingAssembly?.Name;
        foreach (var project in _solution!.Projects)
        {
            var hasGenerators = project.AnalyzerReferences
                .SelectMany(ar => ar.GetGenerators(LanguageNames.CSharp)).Any();
            if (!hasGenerators) continue;

            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            if (targetAssembly != null
                && compilation.Assembly.Name != targetAssembly
                && !compilation.ReferencedAssemblyNames.Any(identity => identity.Name == targetAssembly))
            {
                continue;
            }

            var projectDocumentPaths = new HashSet<string>(
                project.Documents.Where(d => d.FilePath != null).Select(d => d.FilePath!),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (projectDocumentPaths.Contains(tree.FilePath)) continue;
                var semanticModel = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken);
                await visit(tree, semanticModel, root);
            }
        }
    }

    // Generated-tree symbols come from the augmented compilation, a distinct instance of
    // the target's declaration, so they are matched by the solution's cross-compilation
    // identity of the member whose body a reference reaches (CrossCompilationSymbol).
    private static bool MatchesTarget(ISymbol candidate, ISymbol target) =>
        CrossCompilationSymbolComparer.Instance.Equals(
            CallEdgeCollector.ResolveBodyCarrier(candidate), CallEdgeCollector.ResolveBodyCarrier(target));

    private static bool BaseChainContains(INamedTypeSymbol type, ISymbol target)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (MatchesTarget(current, target)) return true;
        }
        return false;
    }
}
