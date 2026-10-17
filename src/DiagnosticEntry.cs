namespace SharpLensMcp;

// One diagnostic in a DiagnosticsData result, at its zero-based start line and column.
// TargetFrameworks lists the target frameworks whose compilation reported it, ordered
// ordinally, when its project is multi-targeted; null for a single-targeted project.
internal sealed record DiagnosticEntry(
    string Id,
    string Severity,
    string Message,
    string FilePath,
    int Line,
    int Column,
    IReadOnlyList<string>? TargetFrameworks);
