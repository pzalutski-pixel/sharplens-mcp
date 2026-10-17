using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The (file, zero-based line, zero-based column) a result is reported at — the shape
// every tool emits as location { filePath, line, column }. FilePath is the path as the
// tool reports it (normally RoslynService.FormatPath's solution-relative form), so the
// canonical order sorts exactly what the caller sees.
internal readonly record struct ResultLocation(string FilePath, int Line, int Column)
{
    // The start of a source location, with the path as the source tree records it; null
    // for a location outside source.
    public static ResultLocation? From(Location location)
    {
        if (!location.IsInSource) return null;
        var span = location.GetLineSpan();
        return new ResultLocation(span.Path, span.StartLinePosition.Line, span.StartLinePosition.Character);
    }
}
