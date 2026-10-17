using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The canonical order of symbol results, as an IComparer for APIs that take one: the
// SymbolOrderKey order (display string, then first source location, then solution key).
// A null symbol sorts first. To sort a list, prefer CanonicalOrder.BySymbol, which
// computes each key once.
internal sealed class SymbolResultComparer : IComparer<ISymbol>
{
    public static readonly SymbolResultComparer Instance = new();

    private SymbolResultComparer() { }

    public int Compare(ISymbol? x, ISymbol? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return SymbolOrderKey.Of(x).CompareTo(SymbolOrderKey.Of(y));
    }
}
