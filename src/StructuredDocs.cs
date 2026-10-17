namespace SharpLensMcp;

// Structured form of a symbol's XML documentation — the sections an agent
// actually wants, parsed from the raw <member>...</member> XML Roslyn returns
// (XmlDocParser). Shared by get_documentation (with inheritdoc expansion) and the
// structured docs of get_external_type_info.
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
    // local doesn't document, and exception lists union by type. The inherited
    // entries must already be named for this symbol (see RoslynService's
    // inheritdoc resolution, which maps them by position).
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
