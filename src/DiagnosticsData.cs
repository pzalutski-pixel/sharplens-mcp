namespace SharpLensMcp;

// Typed result of the diagnostics sweep, composed by GetProjectHealthAsync
// without JSON serialization or `dynamic`.
internal sealed record DiagnosticsData(
    int ErrorCount,
    int WarningCount,
    int AnalyzerCount,
    bool AnalyzersRan,
    int TotalCount,
    IReadOnlyList<DiagnosticEntry> Diagnostics);
