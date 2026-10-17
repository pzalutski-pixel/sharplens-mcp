namespace SharpLensMcp;

// The counts and truncation a tool states explicitly for meta (TotalCount,
// ReturnedCount, Truncated), through RoslynService.CreateSuccessResponse(data, counts, ...).
// Truncated means exactly one thing: results exist that this response does not carry
// because a cap or page cut them. It is NOT inferred from ReturnedCount < TotalCount,
// which is also true on the last page of a paged list and is unrelated to items a
// filter removed or a batch failed to resolve. Pick the factory that names the case:
//   - Complete(count): the whole result is returned.
//   - Capped(totalCount, returnedCount): the first returnedCount of totalCount results
//     are returned because of a cap; truncated when returnedCount < totalCount.
//     TotalCount counts what the tool would return without the cap (after filters).
//   - Page(page): one page of a paged list; truncated exactly when the page has more
//     after it (ResultPage.HasMore), so a last page is not truncated.
//   - Bounded(returnedCount, truncated): a whole-but-bounded result (a graph cut by a
//     depth or node budget) whose untruncated size is unknown; TotalCount is null.
//   - Explicit(totalCount, returnedCount, truncated): any other case, stated outright
//     (e.g. a batch: totalCount requested, returnedCount resolved, not truncated).
internal readonly record struct ResultCounts(int? TotalCount, int? ReturnedCount, bool Truncated)
{
    public static ResultCounts Complete(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), count, "count must be 0 or greater");
        return new ResultCounts(count, count, false);
    }

    public static ResultCounts Capped(int totalCount, int returnedCount)
    {
        if (returnedCount < 0 || returnedCount > totalCount)
        {
            throw new ArgumentOutOfRangeException(nameof(returnedCount), returnedCount,
                $"returnedCount must be between 0 and totalCount ({totalCount})");
        }
        return new ResultCounts(totalCount, returnedCount, returnedCount < totalCount);
    }

    public static ResultCounts Page<T>(ResultPage<T> page) =>
        new(page.TotalCount, page.Items.Count, page.HasMore);

    public static ResultCounts Bounded(int returnedCount, bool truncated)
    {
        if (returnedCount < 0) throw new ArgumentOutOfRangeException(nameof(returnedCount), returnedCount, "returnedCount must be 0 or greater");
        return new ResultCounts(null, returnedCount, truncated);
    }

    public static ResultCounts Explicit(int? totalCount, int? returnedCount, bool truncated) =>
        new(totalCount, returnedCount, truncated);
}
