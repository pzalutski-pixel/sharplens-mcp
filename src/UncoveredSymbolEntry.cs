namespace SharpLensMcp;

// One uncovered (untested) symbol in an UntestedCodeData result.
internal sealed record UncoveredSymbolEntry(
    string FullName,
    string Kind,
    int Complexity,
    string Accessibility,
    object? Location);
