namespace SharpLensMcp;

// One page of an ordered result list (Paging.Slice). Items are the page; Offset and
// Limit are the request; TotalCount is the length of the whole (already filtered and
// canonically ordered) list. HasMore is true exactly when results exist after this page;
// NextOffset is then the offset of the next page and is always greater than Offset (a
// non-empty page advances it), and null on the last page — so a client that follows
// NextOffset until it is null visits every item once and terminates.
internal sealed class ResultPage<T>
{
    public ResultPage(IReadOnlyList<T> items, int offset, int limit, int totalCount)
    {
        Items = items;
        Offset = offset;
        Limit = limit;
        TotalCount = totalCount;
    }

    public IReadOnlyList<T> Items { get; }
    public int Offset { get; }
    public int Limit { get; }
    public int TotalCount { get; }

    public bool HasMore => Offset + Items.Count < TotalCount;

    public int? NextOffset => HasMore ? Offset + Items.Count : null;
}
