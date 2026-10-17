using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The public types of a compilation's referenced metadata assemblies (the framework
// and NuGet packages), nested types included, indexed by simple name
// case-insensitively. Types of referenced solution projects are excluded: they have
// source locations and are resolved as source types. Built once per compilation and
// dropped with it, so a by-name lookup of a framework type does not walk every
// referenced assembly on each call.
internal sealed class MetadataTypeIndex
{
    private static readonly ConditionalWeakTable<Compilation, MetadataTypeIndex> Cache = new();

    private readonly Dictionary<string, List<INamedTypeSymbol>> _byName = new(StringComparer.OrdinalIgnoreCase);

    private MetadataTypeIndex(Compilation compilation)
    {
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            AddNamespace(assembly.GlobalNamespace);
        }
    }

    public static MetadataTypeIndex For(Compilation compilation) =>
        Cache.GetValue(compilation, c => new MetadataTypeIndex(c));

    // The indexed types with the given simple name (any arity), case-insensitively.
    public IReadOnlyList<INamedTypeSymbol> Named(string simpleName) =>
        _byName.TryGetValue(simpleName, out var types) ? types : Array.Empty<INamedTypeSymbol>();

    private void AddNamespace(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers()) AddType(type);
        foreach (var child in ns.GetNamespaceMembers()) AddNamespace(child);
    }

    private void AddType(INamedTypeSymbol type)
    {
        if (type.DeclaredAccessibility != Accessibility.Public || type.Locations.Any(l => l.IsInSource)) return;
        if (!_byName.TryGetValue(type.Name, out var types))
        {
            types = new List<INamedTypeSymbol>();
            _byName[type.Name] = types;
        }
        types.Add(type);
        foreach (var nested in type.GetTypeMembers()) AddType(nested);
    }
}
