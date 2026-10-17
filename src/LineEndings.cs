using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// The line-break style of a source text ("\r\n", "\n" or "\r") and the normalization of
// an edited text to the style of the text it was edited from, so an edit never writes a
// file with mixed line endings. Only the three ASCII line breaks are styles; the Unicode
// line separators are left as they are.
internal static class LineEndings
{
    // The one line-break sequence the text uses, or null when it has no line break or
    // mixes several.
    public static string? UniformStyle(SourceText text)
    {
        string? style = null;
        foreach (var line in text.Lines)
        {
            var lineBreak = BreakOf(text, line);
            if (lineBreak == null) continue;
            if (style == null) style = lineBreak;
            else if (style != lineBreak) return null;
        }
        return style;
    }

    // The most frequent line-break sequence (ties go to "\r\n", then "\n"), or null when
    // the text has no line break.
    public static string? DominantStyle(SourceText text)
    {
        int crlf = 0, lf = 0, cr = 0;
        foreach (var line in text.Lines)
        {
            switch (BreakOf(text, line))
            {
                case "\r\n": crlf++; break;
                case "\n": lf++; break;
                case "\r": cr++; break;
            }
        }
        if (crlf == 0 && lf == 0 && cr == 0) return null;
        if (crlf >= lf && crlf >= cr) return "\r\n";
        return lf >= cr ? "\n" : "\r";
    }

    // Rewrites every "\r\n", "\n" and "\r" in the text to the given line break.
    public static string Convert(string text, string lineBreak)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                builder.Append(lineBreak);
            }
            else if (c == '\n')
            {
                builder.Append(lineBreak);
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    // The edited text with the original's line-break style. When the original uses one
    // style, every line break of the edited text is rewritten to it: the lines the edit did
    // not touch already use that style, so only inserted breaks change. When the original
    // mixes styles, only the text each change inserts is rewritten, to the dominant style,
    // and the untouched lines keep their mix. When the original has no line break, the
    // edited text is made uniform in its own dominant style. `changes` are the edit's
    // changes against the original.
    public static SourceText MatchOriginal(SourceText original, SourceText edited, IEnumerable<TextChange> changes)
    {
        var uniform = UniformStyle(original);
        if (uniform != null) return WithStyle(edited, uniform);

        var dominant = DominantStyle(original);
        if (dominant == null)
        {
            var editedStyle = DominantStyle(edited);
            return editedStyle == null ? edited : WithStyle(edited, editedStyle);
        }

        var normalized = changes
            .Select(c => new TextChange(c.Span, Convert(c.NewText ?? "", dominant)))
            .ToList();
        var rebuilt = original.WithChanges(normalized);
        return rebuilt.ContentEquals(edited) ? edited : SourceText.From(rebuilt.ToString(), edited.Encoding, edited.ChecksumAlgorithm);
    }

    private static SourceText WithStyle(SourceText text, string lineBreak)
    {
        var content = text.ToString();
        var converted = Convert(content, lineBreak);
        return converted == content ? text : SourceText.From(converted, text.Encoding, text.ChecksumAlgorithm);
    }

    private static string? BreakOf(SourceText text, TextLine line)
    {
        var length = line.EndIncludingLineBreak - line.End;
        if (length == 0) return null;
        var lineBreak = text.ToString(new TextSpan(line.End, length));
        return lineBreak is "\r\n" or "\n" or "\r" ? lineBreak : null;
    }
}
