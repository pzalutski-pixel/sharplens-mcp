using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// Collapses the duplicates a per-project scan produces when one logical project loads as
// several per-target-framework compilations (and the exact duplicates some Roslyn APIs
// return within one compilation, such as a reference reported once per using-alias
// cascade step). Items with equal keys merge into one MergedResult that accumulates the
// target frameworks they occurred in.
//
//   - BySymbol: symbol results, keyed by the solution's cross-compilation identity
//     (CrossCompilationSymbolComparer, i.e. CrossCompilationSymbol.SolutionKey).
//   - ByLocation: location results, keyed by SourceSpanKey (normalized file path and
//     exact span).
//   - By: any other key, with its comparer.
//
// The representative of a group is the occurrence from the ordinally-first target
// framework (an occurrence without a framework sorts first), the earliest such
// occurrence on a tie, so the choice does not depend on the order the scan visited
// projects in. Groups come out in the order their first occurrence was seen; sort them
// with CanonicalOrder before returning or capping them.
internal static class TargetFrameworkDeduplication
{
    public static List<MergedResult<T>> BySymbol<T>(
        IEnumerable<T> items, Func<T, ISymbol> symbolOf, Func<T, string?> targetFrameworkOf) =>
        By(items, symbolOf, CrossCompilationSymbolComparer.Instance, targetFrameworkOf);

    public static List<MergedResult<T>> ByLocation<T>(
        IEnumerable<T> items, Func<T, SourceSpanKey> locationOf, Func<T, string?> targetFrameworkOf) =>
        By(items, locationOf, EqualityComparer<SourceSpanKey>.Default, targetFrameworkOf);

    public static List<MergedResult<T>> By<T, TKey>(
        IEnumerable<T> items, Func<T, TKey> keyOf, IEqualityComparer<TKey> keyComparer,
        Func<T, string?> targetFrameworkOf)
        where TKey : notnull
    {
        // Per group, in first-seen order: the representative, its framework, every
        // framework seen, and the occurrence count.
        var groupOf = new Dictionary<TKey, int>(keyComparer);
        var representatives = new List<T>();
        var representativeFrameworks = new List<string?>();
        var frameworks = new List<SortedSet<string>>();
        var occurrences = new List<int>();

        foreach (var item in items)
        {
            var key = keyOf(item);
            var targetFramework = targetFrameworkOf(item);
            if (!groupOf.TryGetValue(key, out var group))
            {
                groupOf[key] = representatives.Count;
                representatives.Add(item);
                representativeFrameworks.Add(targetFramework);
                frameworks.Add(new SortedSet<string>(StringComparer.Ordinal));
                occurrences.Add(0);
                group = representatives.Count - 1;
            }
            else if (string.CompareOrdinal(targetFramework, representativeFrameworks[group]) < 0)
            {
                representatives[group] = item;
                representativeFrameworks[group] = targetFramework;
            }

            if (targetFramework != null) frameworks[group].Add(targetFramework);
            occurrences[group]++;
        }

        return representatives
            .Select((item, group) => new MergedResult<T>(item, frameworks[group].ToList(), occurrences[group]))
            .ToList();
    }
}
