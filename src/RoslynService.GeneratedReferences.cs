using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// Generated-code reference discovery. SymbolFinder runs on the workspace
// solution, which has no generated trees, so a generated serializer calling
// your method — or a source-generated interface implementation — is invisible
// to find_references / find_callers / find_implementations. These scans walk
// the generator-augmented compilations' generated trees and surface the hits
// flagged inGeneratedCode. Generator-less projects are skipped entirely, so
// the common path costs nothing and those solutions' responses are unchanged.
public partial class RoslynService
{
    internal sealed record GeneratedReferenceHit(
        string HintPath, int Line, int Column, string LineText, string Kind, ISymbol? EnclosingSymbol);

    internal sealed record GeneratedImplementationHit(
        string Name, string Kind, string? ContainingNamespace, string HintPath, int Line, int Column);

    // References to `target` inside generated code: identifier/generic-name nodes
    // that bind to it, cross-compilation matched by documentation id.
    private async Task<List<GeneratedReferenceHit>> ScanGeneratedReferencesAsync(
        ISymbol target, CancellationToken cancellationToken)
    {
        var hits = new List<GeneratedReferenceHit>();
        var targetDocId = CrossCompilationSymbol.ReferenceId(target);
        var targetName = target.Name;

        await ForEachGeneratedTreeAsync(async (tree, semanticModel, root) =>
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
                // identifier text can't be the target. Known v1 limitation: a generated
                // reference reached via a `using` alias (identifier text differs from the
                // target's name) is not matched. Generators almost never emit aliases, so
                // this trades a vanishingly-rare miss for not binding every name node.
                if (simpleName != targetName) continue;

                var symbol = semanticModel.GetSymbolInfo(node, cancellationToken).Symbol;
                if (symbol == null || !MatchesTarget(symbol, targetDocId)) continue;

                var lineSpan = tree.GetLineSpan(node.Span, cancellationToken);
                var lineText = tree.GetText(cancellationToken)
                    .Lines[lineSpan.StartLinePosition.Line].ToString().Trim();
                var enclosing = semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken);
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
        var targetDocId = CrossCompilationSymbol.ReferenceId(target);

        await ForEachGeneratedTreeAsync(async (tree, semanticModel, root) =>
        {
            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken)
                    is not INamedTypeSymbol declared) continue;

                var derivesFromTarget =
                    declared.AllInterfaces.Any(i => MatchesTarget(i, targetDocId))
                    || BaseChainContains(declared, targetDocId);
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

    // Runs `visit` over each GENERATED tree (those not among the project's own
    // documents) of every project that actually has generators.
    private async Task ForEachGeneratedTreeAsync(
        Func<SyntaxTree, SemanticModel, SyntaxNode, Task> visit, CancellationToken cancellationToken)
    {
        foreach (var project in _solution!.Projects)
        {
            var hasGenerators = project.AnalyzerReferences
                .SelectMany(ar => ar.GetGenerators(LanguageNames.CSharp)).Any();
            if (!hasGenerators) continue;

            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

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

    // Generated-tree symbols come from the augmented compilation and won't satisfy
    // SymbolEqualityComparer against a workspace-solution target, so compare by the
    // stable reference id (see CrossCompilationSymbol for the rule).
    private static bool MatchesTarget(ISymbol candidate, string? targetDocId) =>
        CrossCompilationSymbol.SameReference(candidate, targetDocId);

    private static bool BaseChainContains(INamedTypeSymbol type, string? targetDocId)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (MatchesTarget(current, targetDocId)) return true;
        }
        return false;
    }
}
