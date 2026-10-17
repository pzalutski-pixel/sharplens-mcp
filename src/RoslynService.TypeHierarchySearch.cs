using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace SharpLensMcp;

// The types below a type in the hierarchy, for find_implementations, get_derived_types and
// get_type_hierarchy. SymbolFinder searches the projects that can see the compilation the
// given symbol came from; a multi-targeted project loads once per target framework and a
// consumer references only one of them (MSBuild picks the nearest framework), so a type
// resolved in its net8.0 compilation would miss the implementers of a project that
// references the net10.0 one. The search therefore runs for the type's counterpart in every
// per-framework compilation of its project, and the results merge by the solution's
// identity rule (TargetFrameworkDeduplication.BySymbol), each with the target frameworks it
// occurred in, in the canonical symbol order.
public partial class RoslynService
{
    // The symbol and its counterparts in every other compilation that compiles the file
    // declaring it (the per-target-framework projects of a multi-targeted project).
    private async Task<IReadOnlyList<ISymbol>> TargetFrameworkCounterpartsAsync(
        ISymbol symbol, CancellationToken cancellationToken)
    {
        var definition = symbol.OriginalDefinition;
        var counterparts = new List<ISymbol> { definition };
        if (definition.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree is not { } tree
            || string.IsNullOrEmpty(tree.FilePath)
            || DocumentationCommentId.CreateDeclarationId(definition) is not { } declarationId)
        {
            return counterparts;
        }

        foreach (var documentId in _solution!.GetDocumentIdsWithFilePath(tree.FilePath))
        {
            if (_solution.GetProject(documentId.ProjectId) is not { } project) continue;
            if (await project.GetCompilationAsync(cancellationToken) is not { } compilation) continue;
            foreach (var candidate in DocumentationCommentId.GetSymbolsForDeclarationId(declarationId, compilation))
            {
                if (CrossCompilationSymbolComparer.Instance.Equals(candidate, definition)
                    && !counterparts.Contains(candidate, SymbolEqualityComparer.Default))
                {
                    counterparts.Add(candidate);
                }
            }
        }
        return counterparts;
    }

    // The types deriving from `type`: for a class, the classes deriving from it; for an
    // interface, the interfaces deriving from it and the classes and structs implementing
    // it. transitive includes indirect ones (a class deriving from an implementer, an
    // interface deriving from a derived interface); otherwise only the types that name
    // `type` among their own bases.
    private async Task<List<MergedResult<INamedTypeSymbol>>> FindDerivedTypesAsync(
        INamedTypeSymbol type, bool transitive, CancellationToken cancellationToken)
    {
        var found = new List<INamedTypeSymbol>();
        foreach (var counterpart in (await TargetFrameworkCounterpartsAsync(type, cancellationToken)).OfType<INamedTypeSymbol>())
        {
            if (counterpart.TypeKind == TypeKind.Interface)
            {
                found.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(counterpart, _solution!, transitive, cancellationToken: cancellationToken));
                found.AddRange(await SymbolFinder.FindImplementationsAsync(counterpart, _solution!, transitive, cancellationToken: cancellationToken));
            }
            else
            {
                found.AddRange(await SymbolFinder.FindDerivedClassesAsync(counterpart, _solution!, transitive, cancellationToken: cancellationToken));
            }
        }
        return MergeTypes(found);
    }

    // The implementations of `type`: for an interface, every class and struct implementing
    // it (directly or through a base); for a class, every class deriving from it — abstract
    // ones included and flagged by the caller.
    private async Task<List<MergedResult<INamedTypeSymbol>>> FindImplementingTypesAsync(
        INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var found = new List<INamedTypeSymbol>();
        foreach (var counterpart in (await TargetFrameworkCounterpartsAsync(type, cancellationToken)).OfType<INamedTypeSymbol>())
        {
            found.AddRange(counterpart.TypeKind == TypeKind.Interface
                ? await SymbolFinder.FindImplementationsAsync(counterpart, _solution!, transitive: true, cancellationToken: cancellationToken)
                : await SymbolFinder.FindDerivedClassesAsync(counterpart, _solution!, transitive: true, cancellationToken: cancellationToken));
        }
        return MergeTypes(found.Where(t => t.TypeKind != TypeKind.Interface));
    }

    private List<MergedResult<INamedTypeSymbol>> MergeTypes(IEnumerable<INamedTypeSymbol> types) =>
        CanonicalOrder.BySymbol(
            TargetFrameworkDeduplication.BySymbol(types, t => t, TargetFrameworkOf),
            m => m.Item);
}
