using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The canonical sort key of a symbol result: its display string (ordinal), then its
// first source location in ResultLocationComparer order (a symbol with no source
// location — a metadata symbol — before any symbol with one), then its
// CrossCompilationSymbol.SolutionKey (ordinal; a symbol without one first). Symbols that
// tie on all three are the same declaration by the solution's identity rule, so the
// order between them is immaterial once results are de-duplicated. Computed once per
// item, so sorting n symbols renders n display strings rather than n log n.
internal readonly record struct SymbolOrderKey(string Display, ResultLocation? Location, string? SolutionKey)
    : IComparable<SymbolOrderKey>
{
    public static SymbolOrderKey Of(ISymbol symbol) => new(
        symbol.ToDisplayString(),
        symbol.Locations.Select(ResultLocation.From).FirstOrDefault(l => l.HasValue),
        CrossCompilationSymbol.SolutionKey(symbol));

    public int CompareTo(SymbolOrderKey other)
    {
        var byDisplay = string.CompareOrdinal(Display, other.Display);
        if (byDisplay != 0) return byDisplay;

        if (Location is { } location && other.Location is { } otherLocation)
        {
            var byLocation = ResultLocationComparer.Instance.Compare(location, otherLocation);
            if (byLocation != 0) return byLocation;
        }
        else if (Location.HasValue != other.Location.HasValue)
        {
            return Location.HasValue ? 1 : -1;
        }

        return string.CompareOrdinal(SolutionKey, other.SolutionKey);
    }
}
