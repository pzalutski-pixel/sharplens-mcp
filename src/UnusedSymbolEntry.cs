using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One unused symbol in an UnusedCodeData result.
internal sealed record UnusedSymbolEntry(
    string Name,
    string FullName,
    string Kind,
    string Accessibility,
    string? ContainingType,
    string FilePath,
    int Line,
    int Column,
    // The detected symbol itself — remove_unused_code maps its declaration
    // syntax; null only in hand-built test data.
    ISymbol? Symbol = null);
