namespace SharpLensMcp;

// One find_async_issues finding: its category (asyncVoid, blockingCall, tokenNotForwarded),
// the member it occurs in, what is wrong, where (Location, the reported path and
// zero-based position; null outside source) and the exact source span that identifies it
// across target frameworks (null outside source).
internal sealed record AsyncIssue(
    string Category, string Symbol, string Detail, ResultLocation? Location, SourceSpanKey? Span);
