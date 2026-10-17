namespace SharpLensMcp;

/// <summary>
/// One editorconfig naming rule as the compiler's naming analyzer (IDE1006) evaluates it:
/// the symbols it applies to (symbol kinds, accessibilities and required modifiers — each
/// list explicit, a wildcard or absent filter expanded to every value) and the style a
/// name must have (capitalization, required prefix and suffix, word separator), with the
/// rule's severity. A rule whose severity is "none" still claims the symbols it applies to
/// — they are then not reported — it does not hand them to a later rule.
/// </summary>
/// <remarks>
/// Kind values: namespace, class, struct, interface, enum, property, method,
/// local_function, field, event, delegate, parameter, type_parameter, local. Accessibility
/// values: public, internal, private, protected, protected_internal, private_protected,
/// local (no accessibility: locals, parameters, local functions, type parameters).
/// Modifier values: abstract, async, const, readonly, static. Capitalization values:
/// pascal_case, camel_case, first_word_upper, all_upper, all_lower.
/// </remarks>
internal sealed record EditorConfigNamingRule(
    string Name,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Accessibilities,
    IReadOnlyList<string> RequiredModifiers,
    string Capitalization,
    string RequiredPrefix,
    string RequiredSuffix,
    string WordSeparator,
    string Severity)
{
    public static readonly IReadOnlyList<string> AllKinds = new[]
    {
        "namespace", "class", "struct", "interface", "enum", "property", "method", "local_function",
        "field", "event", "delegate", "parameter", "type_parameter", "local"
    };

    public static readonly IReadOnlyList<string> AllAccessibilities = new[]
    {
        "local", "public", "internal", "private", "protected", "protected_internal", "private_protected"
    };

    // Whether a symbol of this kind, accessibility and modifiers falls under the rule.
    public bool AppliesTo(string kind, string accessibility, IReadOnlySet<string> modifiers) =>
        Kinds.Contains(kind)
        && Accessibilities.Contains(accessibility)
        && RequiredModifiers.All(modifiers.Contains);

    // A rule set to severity "none": the symbols it claims are not reported.
    public bool IsSilenced => string.Equals(Severity, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Human-readable description of what a conforming name looks like,
    /// e.g. "'_' + camelCase" or "PascalCase ending with 'Async'".
    /// </summary>
    public string ExpectedDescription()
    {
        var core = Capitalization switch
        {
            "pascal_case" => "PascalCase",
            "camel_case" => "camelCase",
            "first_word_upper" => "First word upper",
            "all_upper" => "ALL UPPER",
            "all_lower" => "all lower",
            _ => Capitalization
        };
        var prefix = RequiredPrefix.Length > 0 ? $"'{RequiredPrefix}' + " : "";
        var suffix = RequiredSuffix.Length > 0 ? $" ending with '{RequiredSuffix}'" : "";
        var separated = WordSeparator.Length > 0 ? $" (words separated by '{WordSeparator}')" : "";
        return $"{prefix}{core}{separated}{suffix}";
    }

    /// <summary>
    /// Why the name does not have the rule's style, in the compiler's own words, or null
    /// when it complies. The checks run in the compiler's order: the required prefix and
    /// suffix; a name of nothing but prefix and suffix complies; a further common prefix
    /// ("_", "m_", "s_", "t_") the style does not require fails; then each word (the name
    /// split at the word separator, or the whole name) must have the capitalization —
    /// judged on its first character, or every character for all_upper/all_lower, a
    /// character without case (a digit, '_') passing any check.
    /// </summary>
    public string? Check(string name)
    {
        if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal)) return $"Missing prefix: '{RequiredPrefix}'";
        if (!name.EndsWith(RequiredSuffix, StringComparison.Ordinal)) return $"Missing suffix: '{RequiredSuffix}'";
        if (name.Length <= RequiredPrefix.Length + RequiredSuffix.Length) return null;

        var rest = StripCommonPrefixes(name[RequiredPrefix.Length..], out var commonPrefix);
        if (commonPrefix.Length > 0)
        {
            return RequiredPrefix.Length == 0
                ? $"Prefix '{commonPrefix}' is not expected"
                : $"Prefix '{commonPrefix}' does not match expected prefix '{RequiredPrefix}'";
        }
        rest = rest[..^RequiredSuffix.Length];

        var words = WordSeparator.Length > 0
            ? rest.Split(WordSeparator, StringSplitOptions.RemoveEmptyEntries)
            : new[] { rest };
        if (words.Length == 0) return null;

        return Capitalization switch
        {
            "pascal_case" => FailingWords(words, w => FirstIsUpper(w), "These words must begin with upper case characters: {0}"),
            "camel_case" => !FirstIsLower(words[0])
                ? $"The first word, '{words[0]}', must begin with a lower case character"
                : FailingWords(words.Skip(1).ToArray(), w => FirstIsUpper(w), "These non-leading words must begin with an upper case letter: {0}"),
            "first_word_upper" => !FirstIsUpper(words[0])
                ? $"The first word, '{words[0]}', must begin with an upper case character"
                : FailingWords(words.Skip(1).ToArray(), w => FirstIsLower(w), "These non-leading words must begin with a lowercase letter: {0}"),
            "all_upper" => FailingWords(words, w => w.All(c => !HasCase(c) || char.IsUpper(c)), "These words cannot contain lower case characters: {0}"),
            "all_lower" => FailingWords(words, w => w.All(c => !HasCase(c) || char.IsLower(c)), "These words cannot contain upper case characters: {0}"),
            _ => null
        };
    }

    private static string? FailingWords(string[] words, Func<string, bool> complies, string format)
    {
        var failing = words.Where(w => !complies(w)).ToList();
        return failing.Count == 0 ? null : string.Format(format, string.Join(", ", failing));
    }

    private static bool HasCase(char c) => char.ToLowerInvariant(c) != char.ToUpperInvariant(c);
    private static bool FirstIsUpper(string word) => !HasCase(word[0]) || char.IsUpper(word[0]);
    private static bool FirstIsLower(string word) => !HasCase(word[0]) || char.IsLower(word[0]);

    // Removes the common prefixes the compiler recognizes beyond the required one — any
    // run of "_" and of "m_", "s_", "t_" (any case) — returning the rest and what was
    // removed. A single remaining character is never stripped.
    private static string StripCommonPrefixes(string name, out string prefix)
    {
        var index = 0;
        while (index + 1 < name.Length)
        {
            var c = char.ToLowerInvariant(name[index]);
            if (c is 'm' or 's' or 't' && index + 2 < name.Length && name[index + 1] == '_')
            {
                index += 2;
                continue;
            }
            if (c == '_')
            {
                index++;
                continue;
            }
            break;
        }
        prefix = name[..index];
        return name[index..];
    }
}
