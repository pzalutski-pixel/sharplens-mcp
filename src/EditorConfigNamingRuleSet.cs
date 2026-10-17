namespace SharpLensMcp;

/// <summary>
/// The naming rules of the dotnet_naming_rule / dotnet_naming_symbols / dotnet_naming_style
/// key space, read and ordered as the compiler's naming analyzer (IDE1006) reads and orders
/// them. Pure: takes key/value pairs, touches no workspace.
/// </summary>
/// <remarks>
/// A rule needs a symbols group name, a style name, a severity, and a style capitalization;
/// a rule missing one is ignored — as the compiler ignores it — and listed in
/// <see cref="Ignored"/> with the reason. A symbols group's absent or "*" filter matches
/// every kind or accessibility; unknown filter values are dropped. Rules are ordered by the
/// compiler's precedence (first applicable rule wins): a rule whose required modifiers
/// strictly contain another's (const counting as const and static) comes first, then one
/// whose accessibilities are a strict
/// subset of another's, then one whose symbol kinds are a strict subset of another's, then
/// by name (case-insensitive, then ordinal).
/// </remarks>
internal sealed record EditorConfigNamingRuleSet(
    IReadOnlyList<EditorConfigNamingRule> Rules,
    IReadOnlyList<(string RuleName, string Reason)> Ignored)
{
    private static readonly IReadOnlyList<string> Capitalizations =
        new[] { "pascal_case", "camel_case", "first_word_upper", "all_upper", "all_lower" };

    public static EditorConfigNamingRuleSet Parse(IEnumerable<KeyValuePair<string, string>> options)
    {
        var ruleProps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var symbolProps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var styleProps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in options)
        {
            var parts = key.Split('.');
            if (parts.Length < 3) continue;
            var bucket = parts[0].ToLowerInvariant() switch
            {
                "dotnet_naming_rule" => ruleProps,
                "dotnet_naming_symbols" => symbolProps,
                "dotnet_naming_style" => styleProps,
                _ => null
            };
            if (bucket == null) continue;
            var title = string.Join('.', parts[1..^1]);
            if (!bucket.TryGetValue(title, out var props))
            {
                props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bucket[title] = props;
            }
            props[parts[^1]] = value.Trim();
        }

        var rules = new List<EditorConfigNamingRule>();
        var ignored = new List<(string, string)>();

        foreach (var (ruleName, props) in ruleProps.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            if (!props.TryGetValue("symbols", out var symbolsName) || symbolsName.Length == 0)
            {
                ignored.Add((ruleName, "no symbols group is set - the rule is ignored, as the compiler ignores it"));
                continue;
            }
            if (!props.TryGetValue("style", out var styleName) || styleName.Length == 0)
            {
                ignored.Add((ruleName, "no style is set - the rule is ignored, as the compiler ignores it"));
                continue;
            }
            if (!props.TryGetValue("severity", out var severity) || severity.Length == 0)
            {
                ignored.Add((ruleName, "severity is not set - the rule is ignored, as the compiler ignores it"));
                continue;
            }
            var style = styleProps.GetValueOrDefault(styleName) ?? new Dictionary<string, string>();
            var capitalization = style.GetValueOrDefault("capitalization")?.ToLowerInvariant();
            if (capitalization == null || !Capitalizations.Contains(capitalization))
            {
                ignored.Add((ruleName, capitalization == null
                    ? $"style '{styleName}' sets no capitalization - the rule is ignored, as the compiler ignores it"
                    : $"capitalization '{capitalization}' is not one of {string.Join(", ", Capitalizations)} - the rule is ignored, as the compiler ignores it"));
                continue;
            }

            var symbols = symbolProps.GetValueOrDefault(symbolsName) ?? new Dictionary<string, string>();
            rules.Add(new EditorConfigNamingRule(
                ruleName,
                ParseList(symbols.GetValueOrDefault("applicable_kinds"), EditorConfigNamingRule.AllKinds, KindAliases),
                ParseList(symbols.GetValueOrDefault("applicable_accessibilities"), EditorConfigNamingRule.AllAccessibilities, AccessibilityAliases),
                ParseModifiers(symbols.GetValueOrDefault("required_modifiers")),
                capitalization,
                style.GetValueOrDefault("required_prefix") ?? "",
                style.GetValueOrDefault("required_suffix") ?? "",
                style.GetValueOrDefault("word_separator") ?? "",
                severity.ToLowerInvariant()));
        }

        return new EditorConfigNamingRuleSet(Order(rules), ignored);
    }

    // The compiler's rule precedence. The comparers are partial (two rules neither of whose
    // lists contains the other compare equal), so the same stable sort, over rules first
    // put in name order, reproduces the compiler's order.
    internal static IReadOnlyList<EditorConfigNamingRule> Order(IEnumerable<EditorConfigNamingRule> rules) =>
        rules
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList()
            .OrderBy(r => r, Comparer<EditorConfigNamingRule>.Create(CompareModifiers))
            .ThenBy(r => r, Comparer<EditorConfigNamingRule>.Create(CompareAccessibilities))
            .ThenBy(r => r, Comparer<EditorConfigNamingRule>.Create(CompareKinds))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

    // More required modifiers first, when they include the other rule's. For this
    // comparison const counts as const and static — a const is implicitly static — so a
    // const rule precedes a static rule, as the compiler orders them.
    private static int CompareModifiers(EditorConfigNamingRule x, EditorConfigNamingRule y)
    {
        var xModifiers = OrderingModifiers(x);
        var yModifiers = OrderingModifiers(y);
        if (xModifiers.Count > yModifiers.Count) return yModifiers.All(xModifiers.Contains) ? -1 : 0;
        if (yModifiers.Count > xModifiers.Count) return xModifiers.All(yModifiers.Contains) ? 1 : 0;
        return 0;
    }

    private static HashSet<string> OrderingModifiers(EditorConfigNamingRule rule)
    {
        var modifiers = rule.RequiredModifiers.ToHashSet(StringComparer.Ordinal);
        if (modifiers.Contains("const")) modifiers.Add("static");
        return modifiers;
    }

    // Fewer accessibilities first, when they are among the other rule's.
    private static int CompareAccessibilities(EditorConfigNamingRule x, EditorConfigNamingRule y) =>
        CompareSubsets(x.Accessibilities, y.Accessibilities);

    // Fewer symbol kinds first, when they are among the other rule's.
    private static int CompareKinds(EditorConfigNamingRule x, EditorConfigNamingRule y) =>
        CompareSubsets(x.Kinds, y.Kinds);

    private static int CompareSubsets(IReadOnlyList<string> x, IReadOnlyList<string> y)
    {
        if (x.Count < y.Count) return x.All(y.Contains) ? -1 : 0;
        if (y.Count < x.Count) return y.All(x.Contains) ? 1 : 0;
        return 0;
    }

    private static readonly IReadOnlyDictionary<string, string> KindAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, string> AccessibilityAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["friend"] = "internal",
            ["protected_friend"] = "protected_internal",
        };

    private static readonly IReadOnlyDictionary<string, string> ModifierAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["abstract"] = "abstract",
            ["must_inherit"] = "abstract",
            ["async"] = "async",
            ["const"] = "const",
            ["readonly"] = "readonly",
            ["static"] = "static",
            ["shared"] = "static",
        };

    // A comma-separated filter: absent or "*" is every value; otherwise the known values
    // it lists (aliases mapped), in the canonical order.
    private static IReadOnlyList<string> ParseList(
        string? value, IReadOnlyList<string> all, IReadOnlyDictionary<string, string> aliases)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "*") return all;
        var listed = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => aliases.GetValueOrDefault(v) ?? v.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return all.Where(listed.Contains).ToList();
    }

    private static IReadOnlyList<string> ParseModifiers(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => ModifierAliases.GetValueOrDefault(v))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
}
