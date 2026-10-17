using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// The identity of a location result: the document path and the exact source span.
// Two results at the same span of the same file are one result, whichever project
// (target framework) or compilation reported them. Paths are normalized to forward
// slashes and compared case-insensitively on Windows, ordinally elsewhere — the file
// system's own rule — so "C:\src\A.cs" and "c:/src/A.cs" are the same file on Windows.
internal readonly struct SourceSpanKey : IEquatable<SourceSpanKey>
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public SourceSpanKey(string filePath, TextSpan span)
    {
        FilePath = filePath.Replace('\\', '/');
        Span = span;
    }

    // The path with forward slashes.
    public string FilePath { get; }

    public TextSpan Span { get; }

    // The key of a source location; null for a location outside source (metadata, none).
    public static SourceSpanKey? From(Location location) =>
        location.IsInSource && location.SourceTree is { } tree
            ? new SourceSpanKey(tree.FilePath, location.SourceSpan)
            : null;

    public bool Equals(SourceSpanKey other) =>
        Span.Equals(other.Span) && PathComparer.Equals(FilePath, other.FilePath);

    public override bool Equals(object? obj) => obj is SourceSpanKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(PathComparer.GetHashCode(FilePath), Span);

    public override string ToString() => $"{FilePath}[{Span.Start}..{Span.End})";
}
