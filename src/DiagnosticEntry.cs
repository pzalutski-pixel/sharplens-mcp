namespace SharpLensMcp;

// One diagnostic in a DiagnosticsData result.
internal sealed record DiagnosticEntry(
    string Id,
    string Severity,
    string Message,
    string FilePath,
    int Line);
