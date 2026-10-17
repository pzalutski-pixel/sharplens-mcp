namespace SharpLensMcp;

// One place a symbol is referenced, as find_references and analyze_change_impact report
// it: the path as the tools emit it (FormatPath), the zero-based line and column, the
// trimmed text of that line, the reference kind (RoslynService.ClassifyReferenceKind),
// and whether it is inside source-generator output rather than a solution document.
internal sealed record ReferenceSite(
    string FilePath,
    int Line,
    int Column,
    string LineText,
    string Kind,
    bool InGeneratedCode);
