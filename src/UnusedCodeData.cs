namespace SharpLensMcp;

// Typed result of find_unused_code, composed by GetProjectHealthAsync.
internal sealed record UnusedCodeData(
    int TotalCount,
    IReadOnlyList<UnusedSymbolEntry> Symbols);
