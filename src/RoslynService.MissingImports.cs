using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// add_missing_imports: fix CS0246/CS0103 by computing the right using
// directives — compiler-accurate import suggestion. COMPUTE-ONLY by product
// rule: returns the updated file content with each unambiguous using inserted
// at its sorted position (the organize_usings comparer, without reordering
// anything that already exists); the agent writes the file and syncs.
// Ambiguous names report their candidate namespaces and change nothing;
// names with no candidate are reported unresolvable.
public partial class RoslynService
{
    public async Task<object> AddMissingImportsAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var document = TryFindDocument(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath });
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null || await document.GetSyntaxRootAsync(cancellationToken)
                is not CompilationUnitSyntax root)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model for the file",
                context: new { filePath });
        }

        // The compiler's own verdict on what is unresolved.
        var unresolvedNames = new SortedSet<string>(StringComparer.Ordinal);
        var nonIdentifierUnresolved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in semanticModel.GetDiagnostics(cancellationToken: cancellationToken))
        {
            // CS0246/CS0103: an unresolved type or name. CS1061: a member that does
            // not exist on the receiver — most often an extension method missing its
            // namespace using (e.g. Where without using System.Linq).
            if (diagnostic.Id is not ("CS0246" or "CS0103" or "CS1061")) continue;
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            var name = node switch
            {
                SimpleNameSyntax simple => simple.Identifier.Text,
                _ => node.ToString()
            };
            // A dotted/qualified name cannot be resolved by adding a using (the
            // qualifier IS the namespace). Report it as unresolvable instead of
            // silently dropping it.
            if (SyntaxFacts.IsValidIdentifier(name)) unresolvedNames.Add(name);
            else nonIdentifierUnresolved.Add(name);
        }

        if (unresolvedNames.Count == 0 && nonIdentifierUnresolved.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    appliesEditsAutomatically = false,
                    filePath = FormatPath(document.FilePath),
                    added = Array.Empty<object>(),
                    ambiguous = Array.Empty<object>(),
                    unresolvable = Array.Empty<string>(),
                    updatedContent = (string?)null
                },
                suggestedNextTools: new[] { "Nothing unresolved: the file has no CS0246/CS0103 diagnostics" });
        }

        // One traversal of the merged global namespace (source AND referenced
        // metadata) collects candidate namespaces for every unresolved name.
        var candidatesByName = unresolvedNames.ToDictionary(
            n => n, _ => new SortedSet<string>(StringComparer.Ordinal));
        CollectNamespaceCandidates(
            semanticModel.Compilation.GlobalNamespace, candidatesByName, semanticModel.Compilation);

        var added = new List<object>();
        var ambiguous = new List<object>();
        // Dotted/qualified unresolved names are reported up front — no using fixes them.
        var unresolvable = new List<string>(nonIdentifierUnresolved);
        var updatedRoot = root;

        foreach (var (name, namespaces) in candidatesByName)
        {
            switch (namespaces.Count)
            {
                case 0:
                    unresolvable.Add(name);
                    break;
                case 1:
                    var namespaceName = namespaces.First();
                    if (updatedRoot.Usings.Any(u => u.Name?.ToString() == namespaceName)) break;
                    updatedRoot = InsertUsingSorted(updatedRoot, namespaceName);
                    added.Add(new { name, @namespace = namespaceName, @using = $"using {namespaceName};" });
                    break;
                default:
                    ambiguous.Add(new { name, candidates = namespaces.ToList() });
                    break;
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                appliesEditsAutomatically = false,
                filePath = FormatPath(document.FilePath),
                added,
                ambiguous,
                unresolvable,
                updatedContent = added.Count > 0 ? updatedRoot.ToFullString() : null
            },
            suggestedNextTools: added.Count > 0
                ? new[]
                {
                    "Write updatedContent over the file, then sync_documents and get_diagnostics",
                    "For ambiguous names, qualify the usage or add the intended using yourself"
                }
                : new[] { "No unambiguous import to add; resolve ambiguous/unresolvable names manually" },
            totalCount: added.Count + ambiguous.Count + unresolvable.Count,
            returnedCount: added.Count + ambiguous.Count + unresolvable.Count);
    }

    // Walks every namespace visible to the compilation (source and referenced
    // metadata are merged in GlobalNamespace) once, recording the namespaces
    // that declare a public type bearing one of the wanted simple names.
    private static void CollectNamespaceCandidates(
        INamespaceSymbol namespaceSymbol, Dictionary<string, SortedSet<string>> candidatesByName,
        Compilation compilation)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            // Only types a using would actually make reachable from this assembly:
            // public types, or internal types in the same assembly / one granting
            // InternalsVisibleTo. An in-source internal type in ANOTHER assembly is
            // visible to the walk but a using cannot resolve it.
            if (!compilation.IsSymbolAccessibleWithin(type, compilation.Assembly)) continue;
            if (namespaceSymbol.IsGlobalNamespace) continue;

            // The type itself resolves a missing type name.
            if (candidatesByName.TryGetValue(type.Name, out var typeNamespaces))
                typeNamespaces.Add(namespaceSymbol.ToDisplayString());

            // A public extension method resolves a missing member name (CS1061) by
            // bringing its declaring namespace into scope.
            if (type.MightContainExtensionMethods)
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.IsExtensionMethod
                        && method.DeclaredAccessibility == Accessibility.Public
                        && candidatesByName.TryGetValue(method.Name, out var methodNamespaces))
                    {
                        methodNamespaces.Add(namespaceSymbol.ToDisplayString());
                    }
                }
            }
        }
        foreach (var child in namespaceSymbol.GetNamespaceMembers())
        {
            CollectNamespaceCandidates(child, candidatesByName, compilation);
        }
    }

    // Inserts a using at the position the organize_usings comparer dictates,
    // WITHOUT reordering anything already present — adding an import must not
    // reshuffle the file's existing using block.
    private static CompilationUnitSyntax InsertUsingSorted(
        CompilationUnitSyntax root, string namespaceName)
    {
        var newUsing = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(namespaceName))
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.LineFeed);
        var newKey = UsingSortKey(newUsing);

        var insertIndex = 0;
        while (insertIndex < root.Usings.Count
               && UsingSortKey(root.Usings[insertIndex]).CompareTo(newKey) <= 0)
        {
            insertIndex++;
        }

        if (root.Usings.Count == 0)
            return root.WithUsings(root.Usings.Insert(insertIndex, newUsing));

        // Inserting before the current first using would strand its leading trivia
        // (a file header) below the new using; the shared helper keeps that header
        // pinned to whichever directive ends up first.
        var inserted = root.Usings.Insert(insertIndex, newUsing);
        return root.WithUsings(PinHeaderTrivia(root.Usings[0], inserted));
    }
}
