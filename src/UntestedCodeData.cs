namespace SharpLensMcp;

// Typed result of find_untested_code, composed by GetProjectHealthAsync.
internal sealed record UntestedCodeData(
    string ProductionProject,
    IReadOnlyList<string> TestProjectsScanned,
    int TestMethodCount,
    int ReachableSymbolCount,
    int TotalCount,
    IReadOnlyList<UncoveredSymbolEntry> Symbols);
