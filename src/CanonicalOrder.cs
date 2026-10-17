using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// Deterministic ordering for every result list a tool returns or caps: sort with these
// BEFORE taking a page, so a capped page is the same on every run. Location results
// order by ResultLocationComparer (path, line, column); symbol results by SymbolOrderKey
// (display string, then first source location, then solution key). Both sorts are
// stable, so items that tie keep their input order.
internal static class CanonicalOrder
{
    public static List<T> ByLocation<T>(IEnumerable<T> items, Func<T, ResultLocation> locationOf) =>
        items.OrderBy(locationOf, ResultLocationComparer.Instance).ToList();

    public static List<T> BySymbol<T>(IEnumerable<T> items, Func<T, ISymbol> symbolOf) =>
        items.OrderBy(item => SymbolOrderKey.Of(symbolOf(item))).ToList();
}
