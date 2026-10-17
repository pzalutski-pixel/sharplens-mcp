namespace SharpLensMcp;

// The canonical order of location results: file path (separators normalized to '/',
// then ordinal), then line, then column. Ordinal comparison is culture- and
// platform-independent, so the order — and every capped page of it — is the same on
// every run and machine.
internal sealed class ResultLocationComparer : IComparer<ResultLocation>
{
    public static readonly ResultLocationComparer Instance = new();

    private ResultLocationComparer() { }

    public int Compare(ResultLocation x, ResultLocation y)
    {
        var byPath = string.CompareOrdinal(NormalizePath(x.FilePath), NormalizePath(y.FilePath));
        if (byPath != 0) return byPath;
        var byLine = x.Line.CompareTo(y.Line);
        return byLine != 0 ? byLine : x.Column.CompareTo(y.Column);
    }

    private static string NormalizePath(string? path) => (path ?? "").Replace('\\', '/');
}
