namespace SharpLensMcp;

// Typed records returned by the *DataAsync internal methods on RoslynService.
// Used by GetProjectHealthAsync to compose audit signals without going through
// JSON serialization or `dynamic` on anonymous types.

internal sealed record DiagnosticEntry(
    string Id,
    string Severity,
    string Message,
    string FilePath,
    int Line);

internal sealed record DiagnosticsData(
    int ErrorCount,
    int WarningCount,
    int AnalyzerCount,
    bool AnalyzersRan,
    int TotalCount,
    IReadOnlyList<DiagnosticEntry> Diagnostics);

internal sealed record UnusedSymbolEntry(
    string Name,
    string FullName,
    string Kind,
    string Accessibility,
    string? ContainingType,
    string FilePath,
    int Line,
    int Column);

internal sealed record UnusedCodeData(
    int TotalCount,
    IReadOnlyList<UnusedSymbolEntry> Symbols);

internal sealed record GodObjectCandidate(
    string TypeName,
    int EfferentCoupling,
    int AfferentCoupling,
    int MemberCount,
    double Score,
    object? Location);

internal sealed record GodObjectsData(
    int TotalCount,
    IReadOnlyList<GodObjectCandidate> Candidates);

internal sealed record UncoveredSymbolEntry(
    string FullName,
    string Kind,
    int Complexity,
    string Accessibility,
    object? Location);

internal sealed record UntestedCodeData(
    string ProductionProject,
    IReadOnlyList<string> TestProjectsScanned,
    int TestMethodCount,
    int ReachableSymbolCount,
    int TotalCount,
    IReadOnlyList<UncoveredSymbolEntry> Symbols);
