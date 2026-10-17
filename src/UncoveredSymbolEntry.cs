namespace SharpLensMcp;

// One uncovered (untested) symbol in an UntestedCodeData result, with why it is uncovered.
internal sealed record UncoveredSymbolEntry(
    string FullName,
    string Kind,
    int Complexity,
    string Accessibility,
    object? Location,
    string Reason)
{
    // No entry point reaches the member at all.
    public const string UnreachableReason = "Not reachable from any test method";

    // A test reaches an interface or base member the symbol implements or overrides, through
    // a virtual or interface dispatch, but never the symbol itself: what runs behind that
    // receiver may be another implementation or a mock.
    public const string DispatchOnlyReason = "Reached only via interface or virtual dispatch";
}
