using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The declaring parts of a symbol and the source locations a tool reports or
// addresses it at. A partial method, property, event or constructor has a
// definition part (the bodyless declaration) and an implementation part (the one
// with the body), each a distinct symbol with its own location; a partial type has
// one location per declaration; a source generator can contribute any of these
// parts in a tree that is not a solution document. Locations are ordered by
// LocationOrder: file path (case-insensitive, then ordinal), then span start.
internal static class SymbolParts
{
    // Orders source locations by file path (case-insensitive, then ordinal, so the
    // order is the same on every platform and file system), then span start; a null
    // location sorts last.
    public static readonly IComparer<Location?> LocationOrder = Comparer<Location?>.Create((a, b) =>
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return 1;
        if (b is null) return -1;
        var pathA = a.SourceTree?.FilePath ?? "";
        var pathB = b.SourceTree?.FilePath ?? "";
        var byPath = StringComparer.OrdinalIgnoreCase.Compare(pathA, pathB);
        if (byPath != 0) return byPath;
        byPath = StringComparer.Ordinal.Compare(pathA, pathB);
        return byPath != 0 ? byPath : a.SourceSpan.Start.CompareTo(b.SourceSpan.Start);
    });

    // The symbol every spelling of a member normalizes to: the original definition
    // (not a constructed generic), the unreduced form of a reduced extension method,
    // and the definition part of a partial member. Two symbols denote the same member
    // exactly when their canonical forms are equal.
    public static ISymbol Canonical(ISymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        return definition switch
        {
            IMethodSymbol { ReducedFrom: { } unreduced } => Canonical(unreduced),
            IMethodSymbol { PartialDefinitionPart: { } methodDefinition } => methodDefinition,
            IPropertySymbol { PartialDefinitionPart: { } propertyDefinition } => propertyDefinition,
            IEventSymbol { PartialDefinitionPart: { } eventDefinition } => eventDefinition,
            _ => definition
        };
    }

    public static bool SameMember(ISymbol a, ISymbol b) =>
        SymbolEqualityComparer.Default.Equals(Canonical(a), Canonical(b));

    // Every declaring part: the definition part then the implementation part for a
    // partial member that has both, otherwise the symbol's canonical form alone.
    public static IReadOnlyList<ISymbol> DeclarationParts(ISymbol symbol)
    {
        var canonical = Canonical(symbol);
        ISymbol? implementation = canonical switch
        {
            IMethodSymbol method => method.PartialImplementationPart,
            IPropertySymbol property => property.PartialImplementationPart,
            IEventSymbol @event => @event.PartialImplementationPart,
            _ => null
        };
        return implementation == null ? new[] { canonical } : new[] { canonical, implementation };
    }

    // The part that carries the body: a partial member's implementation part when it
    // has one, otherwise the symbol's canonical form.
    public static ISymbol ImplementationPart(ISymbol symbol) => DeclarationParts(symbol)[^1];

    // The source locations of every declaring part, in LocationOrder.
    public static IReadOnlyList<Location> SourceLocations(ISymbol symbol) =>
        DeclarationParts(symbol)
            .SelectMany(part => part.Locations)
            .Where(l => l.IsInSource && l.SourceTree != null)
            .Distinct()
            .OrderBy(l => l, LocationOrder)
            .ToList();

    // The location to show or address the symbol at, within user-authored source
    // (isUserSource: the solution's own documents, not generator output): the
    // implementation part's earliest location when it is user-authored, otherwise the
    // earliest user-authored location of any part. Null when no part is user-authored
    // (metadata, or a member declared only in generator output).
    public static Location? PreferredLocation(ISymbol symbol, Func<SyntaxTree, bool> isUserSource) =>
        EarliestUserLocation(ImplementationPart(symbol).Locations, isUserSource)
            ?? EarliestUserLocation(SourceLocations(symbol), isUserSource);

    private static Location? EarliestUserLocation(IEnumerable<Location> locations, Func<SyntaxTree, bool> isUserSource) =>
        locations
            .Where(l => l.IsInSource && l.SourceTree != null && isUserSource(l.SourceTree))
            .OrderBy(l => l, LocationOrder)
            .FirstOrDefault();
}
