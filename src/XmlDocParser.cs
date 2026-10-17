using System.Text;
using System.Xml.Linq;

namespace SharpLensMcp;

// Parses Roslyn's GetDocumentationCommentXml() output into StructuredDocs. Pure: no
// symbols, no workspace — unit-testable against raw XML strings.
//
// A section's text keeps the comment's block structure. The block elements — <para>,
// <code> and <list> — are separated from the text around them, and from each other, by
// a blank line. Inline text has its whitespace runs (the comment's line breaks and
// indentation) collapsed to single spaces, and <br/> is a line break. A <code> block
// keeps its own line breaks, without the comment's common indentation. A <list> is one
// line per item: "- " (or "1. " for a numbered list) followed by "term: description".
// Inline references render as a name: <see>/<seealso> as their cref (doc-id prefix
// stripped), langword or href, unless they carry text of their own; <paramref> and
// <typeparamref> as the name.
internal static class XmlDocParser
{
    // Stands for <br/> inside collected inline text until the text is rendered: unlike a
    // newline from the comment's own layout, it is not collapsed into a space. U+2028
    // (LINE SEPARATOR) never occurs in doc text Roslyn hands back.
    private const char LineBreak = (char)0x2028;

    // Returns the structured docs plus whether a member-level <inheritdoc> is
    // present and its (already-bound) cref documentation id, if any.
    public static (StructuredDocs docs, bool hasInheritDoc, string? inheritCref) Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return (StructuredDocs.Empty, false, null);

        XElement? root;
        try { root = XDocument.Parse(xml).Root; }
        catch (System.Xml.XmlException) { return (StructuredDocs.Empty, false, null); }
        if (root == null) return (StructuredDocs.Empty, false, null);

        var inherit = root.Element("inheritdoc");

        var docs = new StructuredDocs(
            SectionText(root.Element("summary")),
            SectionText(root.Element("returns")),
            SectionText(root.Element("remarks")),
            SectionText(root.Element("overloads")),
            NamedEntries(root, "param", "name"),
            NamedEntries(root, "typeparam", "name"),
            NamedEntries(root, "exception", "cref"));

        return (docs, inherit != null, inherit?.Attribute("cref")?.Value);
    }

    private static IReadOnlyList<DocEntry> NamedEntries(XElement root, string element, string nameAttribute)
    {
        var entries = new List<DocEntry>();
        foreach (var e in root.Elements(element))
        {
            var name = e.Attribute(nameAttribute)?.Value;
            if (nameAttribute == "cref") name = StripDocIdPrefix(name);
            if (string.IsNullOrEmpty(name)) continue;
            entries.Add(new DocEntry(name, SectionText(e) ?? ""));
        }
        return entries;
    }

    // A section's text (see the header), or null when it has none.
    private static string? SectionText(XElement? element)
    {
        if (element == null) return null;
        var blocks = new List<string>();
        AppendBlocks(element, blocks);
        return blocks.Count == 0 ? null : string.Join("\n\n", blocks);
    }

    private static void AppendBlocks(XElement element, List<string> blocks)
    {
        var inline = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    inline.Append(text.Value);
                    break;
                case XElement { Name.LocalName: "para" } para:
                    FlushInline(inline, blocks);
                    AppendBlocks(para, blocks);
                    break;
                case XElement { Name.LocalName: "code" } code:
                    FlushInline(inline, blocks);
                    if (CodeBlock(code.Value) is { } codeText) blocks.Add(codeText);
                    break;
                case XElement { Name.LocalName: "list" } list:
                    FlushInline(inline, blocks);
                    if (ListBlock(list) is { } listText) blocks.Add(listText);
                    break;
                case XElement child:
                    AppendInline(child, inline);
                    break;
            }
        }
        FlushInline(inline, blocks);
    }

    // Ends the inline text collected so far as a block: whitespace collapsed within each
    // line, lines (from <br/>) joined by a line break; nothing for blank text.
    private static void FlushInline(StringBuilder inline, List<string> blocks)
    {
        var lines = inline.ToString()
            .Split(LineBreak)
            .Select(CollapseWhitespace)
            .Where(line => line.Length > 0)
            .ToList();
        inline.Clear();
        if (lines.Count > 0) blocks.Add(string.Join("\n", lines));
    }

    private static void AppendInline(XElement element, StringBuilder sb)
    {
        switch (element.Name.LocalName)
        {
            case "see":
            case "seealso":
                if (element.Attribute("langword") is { } langword) sb.Append(langword.Value);
                else if (!element.IsEmpty && !string.IsNullOrWhiteSpace(element.Value)) AppendInlineNodes(element, sb);
                else if (element.Attribute("cref") is { } cref) sb.Append(StripDocIdPrefix(cref.Value));
                else if (element.Attribute("href") is { } href) sb.Append(href.Value);
                break;
            case "paramref":
            case "typeparamref":
                if (element.Attribute("name") is { } name) sb.Append(name.Value);
                break;
            case "br":
                sb.Append(LineBreak);
                break;
            default:
                // Other inline elements (c, b, i, ...): keep their inner content.
                AppendInlineNodes(element, sb);
                break;
        }
    }

    private static void AppendInlineNodes(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement child:
                    AppendInline(child, sb);
                    break;
            }
        }
    }

    // A <code> block: its lines without the leading and trailing blank lines and without
    // the indentation every non-blank line shares (the comment's own); null when empty.
    private static string? CodeBlock(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0])) lines.RemoveAt(0);
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return null;

        var indent = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Min(l => l.Length - l.TrimStart().Length);
        return string.Join("\n", lines.Select(l => (string.IsNullOrWhiteSpace(l) ? "" : l[indent..]).TrimEnd()));
    }

    // A <list>: one line per <item> (its term and description, or its text).
    private static string? ListBlock(XElement list)
    {
        var numbered = list.Attribute("type")?.Value == "number";
        var lines = list.Elements("item")
            .Select(item =>
            {
                var term = InlineText(item.Element("term"));
                var description = InlineText(item.Element("description"));
                return term != null && description != null ? $"{term}: {description}"
                    : term ?? description ?? InlineText(item);
            })
            .Where(text => text != null)
            .Select((text, index) => numbered ? $"{index + 1}. {text}" : $"- {text}")
            .ToList();
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static string? InlineText(XElement? element)
    {
        if (element == null) return null;
        var sb = new StringBuilder();
        AppendInlineNodes(element, sb);
        var text = CollapseWhitespace(sb.ToString().Replace(LineBreak, ' '));
        return text.Length == 0 ? null : text;
    }

    // Runs of whitespace (the comment's line breaks and indentation) as single spaces,
    // trimmed.
    private static string CollapseWhitespace(string value) =>
        string.Join(" ", value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));

    // "T:Foo.Bar" / "M:Foo.Bar(...)" → "Foo.Bar(...)" for display.
    private static string? StripDocIdPrefix(string? cref) =>
        cref is { Length: > 2 } && cref[1] == ':' ? cref[2..] : cref;
}
