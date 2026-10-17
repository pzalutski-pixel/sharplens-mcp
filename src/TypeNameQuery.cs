using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// A by-name type reference as a caller types it, normalized to dotted segments.
// Accepted spellings, each optionally `global::`-prefixed:
//   - a C# keyword alias: int, string, object, ... (see KeywordMetadataName);
//   - a metadata name: Lib.Outer+Inner, System.Collections.Generic.List`1;
//   - a C# display name: Lib.Outer.Inner, Lib.Repo<T>, Repo<int>, Dictionary<,>;
//   - any dotted suffix of those: Outer.Inner, Repo<T>, Builder.
// Type arguments only contribute their count (the arity): `Repo<int>` denotes the
// generic definition Repo<T>. Array, pointer and nullable spellings are not type
// names and do not parse.
internal sealed record TypeNameQuery(IReadOnlyList<TypeNameSegment> Segments)
{
    private static readonly Dictionary<string, string> KeywordAliases = new(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["nint"] = "System.IntPtr",
        ["nuint"] = "System.UIntPtr",
        ["char"] = "System.Char",
        ["float"] = "System.Single",
        ["double"] = "System.Double",
        ["decimal"] = "System.Decimal",
        ["string"] = "System.String",
        ["object"] = "System.Object",
        ["void"] = "System.Void",
    };

    private const string GlobalPrefix = "global::";

    // The innermost segment's name: the simple name the type itself is declared with.
    public string SimpleName => Segments[^1].Name;

    // The metadata name a C# keyword alias stands for (int → System.Int32), or null when
    // the text is not one. Keywords are case-sensitive, as in C#.
    public static string? KeywordMetadataName(string text) =>
        KeywordAliases.TryGetValue(text.Trim(), out var metadataName) ? metadataName : null;

    // Parses a type reference, or returns null when the text is not a type name.
    public static TypeNameQuery? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.StartsWith(GlobalPrefix, StringComparison.Ordinal))
            compact = compact[GlobalPrefix.Length..];

        var segments = new List<TypeNameSegment>();
        var name = new System.Text.StringBuilder();
        int? arity = null;
        var i = 0;
        while (i < compact.Length)
        {
            var c = compact[i];
            if (c is '.' or '+')
            {
                if (name.Length == 0) return null;
                segments.Add(new TypeNameSegment(name.ToString(), arity));
                name.Clear();
                arity = null;
                i++;
            }
            else if (c == '<')
            {
                if (name.Length == 0 || arity != null) return null;
                var depth = 1;
                var commas = 0;
                i++;
                while (i < compact.Length && depth > 0)
                {
                    switch (compact[i])
                    {
                        case '<': depth++; break;
                        case '>': depth--; break;
                        case ',' when depth == 1: commas++; break;
                    }
                    i++;
                }
                if (depth != 0) return null;
                if (i < compact.Length && compact[i] is not ('.' or '+')) return null;
                arity = commas + 1;
            }
            else if (c == '`')
            {
                if (name.Length == 0 || arity != null) return null;
                var start = ++i;
                while (i < compact.Length && char.IsDigit(compact[i])) i++;
                if (i == start || !int.TryParse(compact[start..i], out var parsed)) return null;
                if (i < compact.Length && compact[i] is not ('.' or '+')) return null;
                arity = parsed;
            }
            else if (c == '@' && name.Length == 0)
            {
                i++;
            }
            else if (char.IsLetterOrDigit(c) || c == '_')
            {
                name.Append(c);
                i++;
            }
            else
            {
                return null;
            }
        }
        if (name.Length == 0) return null;
        segments.Add(new TypeNameSegment(name.ToString(), arity));
        return new TypeNameQuery(segments);
    }

    // Every metadata name the segments can denote, for GetTypeByMetadataName: each
    // split into a namespace prefix and a chain of containing types (nested types
    // joined with '+'), longest namespace first. A segment without an arity is read as
    // non-generic, the way metadata names spell it. Namespace segments carry no arity.
    public IEnumerable<string> MetadataNameCandidates()
    {
        for (var typeStart = Segments.Count - 1; typeStart >= 0; typeStart--)
        {
            if (Segments.Take(typeStart).Any(s => s.Arity is > 0)) continue;
            var namespacePart = string.Join(".", Segments.Take(typeStart).Select(s => s.Name));
            var typePart = string.Join("+", Segments.Skip(typeStart)
                .Select(s => s.Arity is > 0 ? $"{s.Name}`{s.Arity}" : s.Name));
            yield return namespacePart.Length == 0 ? typePart : $"{namespacePart}.{typePart}";
        }
    }

    // Whether the type's containment chain ends with these segments, compared
    // case-insensitively: the last segment names the type itself, each earlier one its
    // containing type or namespace, outward. A segment without an arity matches any
    // arity; a qualified query need not reach the global namespace (Outer.Inner matches
    // Lib.Outer.Inner).
    public bool Matches(INamedTypeSymbol type)
    {
        ISymbol? current = type;
        for (var i = Segments.Count - 1; i >= 0; i--)
        {
            var segment = Segments[i];
            switch (current)
            {
                case INamedTypeSymbol named
                    when named.Name.Equals(segment.Name, StringComparison.OrdinalIgnoreCase)
                         && (segment.Arity == null || segment.Arity == named.Arity):
                    current = (ISymbol?)named.ContainingType ?? named.ContainingNamespace;
                    break;
                case INamespaceSymbol { IsGlobalNamespace: false } ns
                    when segment.Arity is null or 0
                         && ns.Name.Equals(segment.Name, StringComparison.OrdinalIgnoreCase):
                    current = ns.ContainingNamespace;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }
}
