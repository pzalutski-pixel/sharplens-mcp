using System.Text;
using System.Xml.Linq;

namespace SharpLensMcp;

// One named documentation entry: a <param>/<typeparam> (Name = parameter name)
// or an <exception> (Name = exception type).
internal sealed record DocEntry(string Name, string Text);

// Structured form of a symbol's XML documentation — the sections an agent
// actually wants, parsed from the raw <member>...</member> XML Roslyn returns.
// Shared by get_documentation (with inheritdoc expansion) and the structured
// docs of get_external_type_info.
internal sealed record StructuredDocs(
    string? Summary,
    string? Returns,
    string? Remarks,
    string? Overloads,
    IReadOnlyList<DocEntry> Params,
    IReadOnlyList<DocEntry> TypeParams,
    IReadOnlyList<DocEntry> Exceptions)
{
    public static readonly StructuredDocs Empty = new(
        null, null, null, null,
        Array.Empty<DocEntry>(), Array.Empty<DocEntry>(), Array.Empty<DocEntry>());

    public bool IsEmpty =>
        Summary == null && Returns == null && Remarks == null && Overloads == null
        && Params.Count == 0 && TypeParams.Count == 0 && Exceptions.Count == 0;

    // Element-level inheritdoc merge: THIS (local) doc wins, the inherited doc
    // fills sections the local omits and per-name param/typeparam entries the
    // local doesn't document, and exception lists union by type.
    public StructuredDocs MergeInherited(StructuredDocs inherited) => new(
        Summary ?? inherited.Summary,
        Returns ?? inherited.Returns,
        Remarks ?? inherited.Remarks,
        Overloads ?? inherited.Overloads,
        MergeByName(Params, inherited.Params),
        MergeByName(TypeParams, inherited.TypeParams),
        MergeByName(Exceptions, inherited.Exceptions));

    private static IReadOnlyList<DocEntry> MergeByName(
        IReadOnlyList<DocEntry> local, IReadOnlyList<DocEntry> inherited)
    {
        if (inherited.Count == 0) return local;
        if (local.Count == 0) return inherited;
        var localNames = local.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        return local.Concat(inherited.Where(e => !localNames.Contains(e.Name))).ToList();
    }
}

// Parses Roslyn's GetDocumentationCommentXml() output. Pure: no symbols, no
// workspace — unit-testable against raw XML strings.
internal static class XmlDocParser
{
    // Returns the structured docs plus whether a member-level <inheritdoc> is
    // present and its (already-bound) cref documentation id, if any.
    public static (StructuredDocs docs, bool hasInheritDoc, string? inheritCref) Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return (StructuredDocs.Empty, false, null);

        XElement? root;
        try { root = XDocument.Parse(xml).Root; }
        catch { return (StructuredDocs.Empty, false, null); }
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

    private static string? SectionText(XElement? element) =>
        element == null ? null : Normalize(ExtractText(element));

    // Flattens an element's inline content to text. XElement.Value drops empty
    // inline elements, so "See <see cref=\"Foo.Bar\"/> for details" would lose the
    // reference — node-walking substitutes the cref/name/langword attribute value.
    private static string ExtractText(XElement element)
    {
        var sb = new StringBuilder();
        AppendNodes(element, sb);
        return sb.ToString();
    }

    private static void AppendNodes(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement child:
                    AppendInlineElement(child, sb);
                    break;
            }
        }
    }

    private static void AppendInlineElement(XElement child, StringBuilder sb)
    {
        switch (child.Name.LocalName)
        {
            case "see":
                if (child.Attribute("langword") is { } langword) sb.Append(langword.Value);
                else if (!child.IsEmpty && !string.IsNullOrWhiteSpace(child.Value)) AppendNodes(child, sb);
                else if (child.Attribute("cref") is { } cref) sb.Append(StripDocIdPrefix(cref.Value));
                else if (child.Attribute("href") is { } href) sb.Append(href.Value);
                break;
            case "paramref":
            case "typeparamref":
                if (child.Attribute("name") is { } name) sb.Append(name.Value);
                break;
            default:
                // Other inline elements (c, code, para, …): keep their inner content.
                AppendNodes(child, sb);
                break;
        }
    }

    // Doc comments carry the source indentation; collapse runs of whitespace
    // to single spaces and trim. Null when there is no text content.
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split(
            new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : string.Join(" ", parts);
    }

    // "T:Foo.Bar" / "M:Foo.Bar(...)" → "Foo.Bar(...)" for display.
    private static string? StripDocIdPrefix(string? cref) =>
        cref is { Length: > 2 } && cref[1] == ':' ? cref[2..] : cref;
}
