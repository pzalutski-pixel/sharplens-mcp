namespace SharpLensMcp;

// Typed result of find_unused_code, composed by GetProjectHealthAsync and
// RemoveUnusedCodeAsync. Error is the canonical project-scope error (ProjectResolutionError)
// when the requested projectName does not resolve — then TotalCount is 0 and Symbols is
// empty — and null otherwise.
internal sealed record UnusedCodeData(
    int TotalCount,
    IReadOnlyList<UnusedSymbolEntry> Symbols,
    object? Error = null);
