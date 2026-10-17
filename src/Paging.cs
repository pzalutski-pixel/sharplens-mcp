namespace SharpLensMcp;

// The shared offset/limit pager. The caller validates the request first
// (ParameterValidation.Offset and ParameterValidation.Limit: offset >= 0, limit >= 1)
// and passes the complete, filtered, canonically ordered list; Slice then cuts the page.
// An offset at or past the end yields an empty last page (HasMore false, NextOffset
// null). A request that skipped validation is a programming error and throws.
internal static class Paging
{
    public static ResultPage<T> Slice<T>(IReadOnlyList<T> all, int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), offset, "offset must be 0 or greater");
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be 1 or greater");

        var items = all.Skip(offset).Take(limit).ToList();
        return new ResultPage<T>(items, offset, limit, all.Count);
    }
}
