namespace SharpLensMcp;

/// <summary>
/// One parsed editorconfig naming rule in the v1-supported shape:
/// symbol-kind and accessibility filters, pascal_case/camel_case
/// capitalization, and required prefix/suffix. Empty filter sets mean "all".
/// </summary>
internal sealed record EditorConfigNamingRule(
    string Name,
    IReadOnlySet<string> Kinds,
    IReadOnlySet<string> Accessibilities,
    string? Capitalization,
    string? RequiredPrefix,
    string? RequiredSuffix,
    string? WordSeparator = null)
{
    /// <summary>
    /// Human-readable description of what a conforming name looks like,
    /// e.g. "_camelCase" or "PascalCase ending with 'Async'".
    /// </summary>
    public string ExpectedDescription()
    {
        var core = Capitalization switch
        {
            "pascal_case" => "PascalCase",
            "camel_case" => "camelCase",
            _ => "any casing"
        };
        var prefix = RequiredPrefix != null ? $"'{RequiredPrefix}' + " : "";
        var suffix = RequiredSuffix != null ? $" ending with '{RequiredSuffix}'" : "";
        var separated = WordSeparator != null ? $" (words separated by '{WordSeparator}')" : "";
        return $"{prefix}{core}{separated}{suffix}";
    }

    /// <summary>
    /// How constrained this rule's applicability is — the count of explicit
    /// kind and accessibility filters (an empty set is a wildcard, the least
    /// specific). Used to pick the most specific matching rule when several
    /// apply, modeling editorconfig precedence by specificity.
    /// </summary>
    public int Specificity => Kinds.Count + Accessibilities.Count;

    /// <summary>
    /// Whether <paramref name="name"/> conforms: required prefix/suffix are
    /// stripped first, then the remainder must match the capitalization
    /// (first-character case, no underscores).
    /// </summary>
    public bool IsSatisfiedBy(string name)
    {
        var rest = name;
        if (RequiredPrefix != null)
        {
            if (!rest.StartsWith(RequiredPrefix, StringComparison.Ordinal)) return false;
            rest = rest[RequiredPrefix.Length..];
        }
        if (RequiredSuffix != null)
        {
            if (!rest.EndsWith(RequiredSuffix, StringComparison.Ordinal)) return false;
            rest = rest[..^RequiredSuffix.Length];
        }
        if (Capitalization is not ("pascal_case" or "camel_case")) return true;
        if (rest.Length == 0) return false;

        // word_separator: each segment between separators follows the capitalization.
        // For camel_case only the first word is lower-first; the rest are upper-first.
        // Empty segments (leading/trailing/doubled separators) do not conform.
        if (WordSeparator != null)
        {
            var segments = rest.Split(WordSeparator);
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i].Length == 0) return false;
                var lowerFirst = Capitalization == "camel_case" && i == 0;
                if (lowerFirst ? !char.IsLower(segments[i][0]) : !char.IsUpper(segments[i][0]))
                    return false;
            }
            return true;
        }

        return Capitalization switch
        {
            "pascal_case" => char.IsUpper(rest[0]) && !rest.Contains('_'),
            "camel_case" => char.IsLower(rest[0]) && !rest.Contains('_'),
            _ => true
        };
    }
}

/// <summary>
/// Parses the dotnet_naming_rule / dotnet_naming_symbols / dotnet_naming_style
/// key space of analyzer-config options into the v1-supported rule shape.
/// Pure: takes key/value pairs, touches no workspace — unit-testable as such.
/// Supports pascal_case/camel_case (incl. word_separator), required prefix/suffix,
/// and kind/accessibility filters. Rules outside that scope (required_modifiers,
/// other capitalizations) are reported in
/// <see cref="Unsupported"/> rather than silently dropped. A rule the user
/// explicitly set to <c>severity = none</c> is disabled, not unsupported: it
/// is skipped so it produces no violations, matching the compiler.
/// </summary>
internal sealed record EditorConfigNamingRuleSet(
    IReadOnlyList<EditorConfigNamingRule> Rules,
    IReadOnlyList<(string RuleName, string Reason)> Unsupported)
{
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
            if (!bucket.TryGetValue(parts[1], out var props))
            {
                props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bucket[parts[1]] = props;
            }
            props[parts[2]] = value.Trim();
        }

        var rules = new List<EditorConfigNamingRule>();
        var unsupported = new List<(string, string)>();

        foreach (var (ruleName, props) in ruleProps)
        {
            // A rule explicitly disabled by the user (severity = none) is not a
            // violation source and not "unsupported" — skip it entirely.
            if (props.TryGetValue("severity", out var severity)
                && string.Equals(severity, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!props.TryGetValue("symbols", out var symbolsName)
                || !symbolProps.TryGetValue(symbolsName, out var symbols))
            {
                unsupported.Add((ruleName, "symbols group not found"));
                continue;
            }
            if (!props.TryGetValue("style", out var styleName)
                || !styleProps.TryGetValue(styleName, out var style))
            {
                unsupported.Add((ruleName, "style not found"));
                continue;
            }
            if (symbols.TryGetValue("required_modifiers", out var modifiers)
                && !string.IsNullOrWhiteSpace(modifiers))
            {
                unsupported.Add((ruleName, "required_modifiers is not supported"));
                continue;
            }
            var capitalization = style.GetValueOrDefault("capitalization");
            if (capitalization != null && capitalization is not ("pascal_case" or "camel_case"))
            {
                unsupported.Add((ruleName, $"capitalization '{capitalization}' is not supported"));
                continue;
            }

            rules.Add(new EditorConfigNamingRule(
                ruleName,
                ParseFilterSet(symbols.GetValueOrDefault("applicable_kinds")),
                ParseFilterSet(symbols.GetValueOrDefault("applicable_accessibilities")),
                capitalization,
                NullIfEmpty(style.GetValueOrDefault("required_prefix")),
                NullIfEmpty(style.GetValueOrDefault("required_suffix")),
                NullIfEmpty(style.GetValueOrDefault("word_separator"))));
        }

        return new EditorConfigNamingRuleSet(rules, unsupported);
    }

    // "*" (or absent) means every kind/accessibility — modeled as the empty set.
    private static IReadOnlySet<string> ParseFilterSet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "*")
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
