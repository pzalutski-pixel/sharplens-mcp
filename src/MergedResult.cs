namespace SharpLensMcp;

// One result after TargetFrameworkDeduplication: the representative item of a group of
// duplicates, the target frameworks the group occurred in (distinct, ordered ordinally;
// empty when every occurrence came from a project without a target-framework suffix),
// and how many raw occurrences were merged into it.
internal sealed record MergedResult<T>(T Item, IReadOnlyList<string> TargetFrameworks, int Occurrences);
