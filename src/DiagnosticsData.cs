namespace SharpLensMcp;

// Typed result of the diagnostics sweep, composed by GetProjectHealthAsync without JSON
// serialization or `dynamic`. Diagnostics is the COMPLETE list after the hidden and
// severity filters — errors first, then canonical location order — so a caller computes
// breakdowns from all of it and caps only what it returns. ErrorCount and WarningCount
// count the set before the severity filter. AnalyzerCount is the number of distinct
// analyzers that ran (zero when analyzers were not requested).
internal sealed record DiagnosticsData(
    int ErrorCount,
    int WarningCount,
    int AnalyzerCount,
    bool AnalyzersRan,
    IReadOnlyList<DiagnosticEntry> Diagnostics);
