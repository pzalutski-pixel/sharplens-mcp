using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// add_missing_imports: fix CS0246/CS0103/CS1061 by computing the right using
// directives — compiler-accurate import suggestion. COMPUTE-ONLY by product
// rule: returns the updated file content with each unambiguous using inserted
// at its sorted position (the organize_usings comparer, without reordering
// anything that already exists); the agent writes the file and syncs.
// A name is looked up the way the compiler binds it once the using is added
// (UnresolvedName): an attribute name also matches its "Attribute"-suffixed attribute
// type, a generic name matches only types of its arity, and a missing member counts as
// a missing extension method only in a namespace with an extension of that name that
// applies to the receiver's type. Every name a using fixes is reported with it, also
// when several names share the one using. Ambiguous names report their candidate
// namespaces and change nothing; a name with no candidate is reported unresolvable.
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
                context: new { filePath = FormatPath(document.FilePath) });
        }

        // The compiler's own verdict on what is unresolved.
        var unresolved = new Dictionary<(string, int, bool, bool), UnresolvedName>();
        var nonIdentifierUnresolved = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var diagnostic in semanticModel.GetDiagnostics(cancellationToken: cancellationToken))
        {
            // CS0246/CS0103: an unresolved type or name. CS1061: a member that does
            // not exist on the receiver — most often an extension method missing its
            // namespace using (e.g. Where without using System.Linq).
            if (diagnostic.Id is not ("CS0246" or "CS0103" or "CS1061")) continue;
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node is not SimpleNameSyntax simple)
            {
                // A dotted/qualified name cannot be resolved by adding a using (the
                // qualifier IS the namespace). Report it as unresolvable instead of
                // silently dropping it.
                nonIdentifierUnresolved.Add(node.ToString());
                continue;
            }

            var name = simple.Identifier.ValueText;
            var arity = simple is GenericNameSyntax generic ? generic.TypeArgumentList.Arguments.Count : 0;
            var isExtension = diagnostic.Id == "CS1061";
            var isAttribute = !isExtension && IsAttributeName(simple);
            var key = (name, arity, isAttribute, isExtension);
            if (!unresolved.TryGetValue(key, out var entry))
            {
                entry = new UnresolvedName(name, arity, isAttribute, isExtension);
                unresolved[key] = entry;
            }
            if (isExtension && simple.Parent is MemberAccessExpressionSyntax access && access.Name == simple
                && semanticModel.GetTypeInfo(access.Expression, cancellationToken).Type is { TypeKind: not TypeKind.Error } receiver)
            {
                entry.Receivers.Add(receiver);
            }
        }

        if (unresolved.Count == 0 && nonIdentifierUnresolved.Count == 0)
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
                suggestedNextTools: new[] { "Nothing unresolved: the file has no CS0246/CS0103/CS1061 diagnostics" });
        }

        // One traversal of the merged global namespace (source AND referenced
        // metadata) collects candidate namespaces for every unresolved name.
        var entries = unresolved.Values
            .OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Arity)
            .ToList();
        var candidates = entries.ToDictionary(e => e, _ => new SortedSet<string>(StringComparer.Ordinal));
        CollectNamespaceCandidates(semanticModel.Compilation.GlobalNamespace, candidates, semanticModel.Compilation);

        var added = new List<object>();
        var ambiguous = new List<object>();
        // Dotted/qualified unresolved names are reported up front — no using fixes them.
        var unresolvable = new List<string>(nonIdentifierUnresolved);
        var updatedRoot = root;

        foreach (var entry in entries)
        {
            var namespaces = candidates[entry];
            switch (namespaces.Count)
            {
                case 0:
                    unresolvable.Add(entry.Name);
                    break;
                case 1:
                    // Every name the namespace fixes is reported, even when an earlier
                    // name already added its using.
                    var namespaceName = namespaces.First();
                    if (!updatedRoot.Usings.Any(u => u.Alias == null && u.Name?.ToString() == namespaceName))
                        updatedRoot = InsertUsingSorted(updatedRoot, namespaceName);
                    added.Add(new { name = entry.Name, @namespace = namespaceName, @using = $"using {namespaceName};" });
                    break;
                default:
                    ambiguous.Add(new { name = entry.Name, candidates = namespaces.ToList() });
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
                updatedContent = added.Count > 0
                    ? await MatchDocumentLineEndingsAsync(document, updatedRoot.ToFullString(), cancellationToken)
                    : null
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

    // Whether the simple name is (the right-most part of) an attribute's name.
    private static bool IsAttributeName(SimpleNameSyntax name)
    {
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node) node = qualified;
        return node.Parent is AttributeSyntax attribute && attribute.Name == node;
    }

    // Walks every namespace visible to the compilation (source and referenced
    // metadata are merged in GlobalNamespace) once, recording for each wanted name the
    // namespaces whose types (or extension methods) would bind it.
    private static void CollectNamespaceCandidates(
        INamespaceSymbol namespaceSymbol, Dictionary<UnresolvedName, SortedSet<string>> candidates,
        Compilation compilation)
    {
        if (!namespaceSymbol.IsGlobalNamespace)
        {
            var attributeType = compilation.GetTypeByMetadataName("System.Attribute");
            foreach (var type in namespaceSymbol.GetTypeMembers())
            {
                // Only types a using would actually make reachable from this assembly:
                // public types, or internal types in the same assembly / one granting
                // InternalsVisibleTo. An in-source internal type in ANOTHER assembly is
                // visible to the walk but a using cannot resolve it.
                if (!compilation.IsSymbolAccessibleWithin(type, compilation.Assembly)) continue;

                foreach (var (wanted, namespaces) in candidates)
                {
                    if (wanted.IsExtension)
                    {
                        // An extension method resolves a missing member (CS1061) only when it
                        // applies to the receiver's type.
                        if (!type.MightContainExtensionMethods) continue;
                        var applies = type.GetMembers(wanted.Name).OfType<IMethodSymbol>().Any(method =>
                            method.IsExtensionMethod
                            && compilation.IsSymbolAccessibleWithin(method, compilation.Assembly)
                            && wanted.Receivers.Any(receiver => method.ReduceExtensionMethod(receiver) != null));
                        if (applies) namespaces.Add(namespaceSymbol.ToDisplayString());
                        continue;
                    }

                    if (type.Arity != wanted.Arity) continue;
                    if (type.Name == wanted.Name
                        || (wanted.IsAttribute && type.Name == wanted.Name + "Attribute" && DerivesFrom(type, attributeType)))
                    {
                        namespaces.Add(namespaceSymbol.ToDisplayString());
                    }
                }
            }
        }
        foreach (var child in namespaceSymbol.GetNamespaceMembers())
        {
            CollectNamespaceCandidates(child, candidates, compilation);
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol? baseType)
    {
        if (baseType == null) return false;
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
        }
        return false;
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
