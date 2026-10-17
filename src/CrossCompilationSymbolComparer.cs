using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The equality comparer for every set or dictionary that pools, de-duplicates or
// matches symbols surfaced by several project compilations of one solution, the
// per-target-framework compilations of a multi-targeted project included: two symbols
// are equal when their CrossCompilationSymbol.SolutionKey is equal, whichever
// compilation produced them. A symbol without a SolutionKey (a local function, lambda
// or anonymous type, each only ever bound inside its declaring project, or an error
// type) falls back to SymbolEqualityComparer.Default. See CrossCompilationSymbol for
// the rule.
internal sealed class CrossCompilationSymbolComparer : IEqualityComparer<ISymbol>
{
    public static readonly CrossCompilationSymbolComparer Instance = new();

    private CrossCompilationSymbolComparer() { }

    public bool Equals(ISymbol? x, ISymbol? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;

        var xKey = CrossCompilationSymbol.SolutionKey(x);
        var yKey = CrossCompilationSymbol.SolutionKey(y);
        if (xKey != null || yKey != null) return string.Equals(xKey, yKey, StringComparison.Ordinal);
        return SymbolEqualityComparer.Default.Equals(x, y);
    }

    public int GetHashCode(ISymbol obj) =>
        CrossCompilationSymbol.SolutionKey(obj) is { } key
            ? StringComparer.Ordinal.GetHashCode(key)
            : SymbolEqualityComparer.Default.GetHashCode(obj);
}
