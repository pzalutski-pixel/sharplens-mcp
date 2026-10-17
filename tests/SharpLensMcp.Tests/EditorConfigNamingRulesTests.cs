using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// Pure unit coverage for EditorConfigNamingRuleSet.Parse and EditorConfigNamingRule.Check —
// no workspace involved. Every Check expectation below is the verdict (and wording) the
// compiler's IDE1006 analyzer gives the same name under the same style, taken from a
// `dotnet build` with EnforceCodeStyleInBuild.
public class EditorConfigNamingRulesTests
{
    private static KeyValuePair<string, string> Kv(string key, string value) => new(key, value);

    // One rule "r" over a symbols group "g" and a style "s" with the given style keys.
    private static EditorConfigNamingRule Rule(params (string Key, string Value)[] style)
    {
        var options = new List<KeyValuePair<string, string>>
        {
            Kv("dotnet_naming_rule.r.symbols", "g"),
            Kv("dotnet_naming_rule.r.style", "s"),
            Kv("dotnet_naming_rule.r.severity", "warning"),
            Kv("dotnet_naming_symbols.g.applicable_kinds", "field"),
        };
        options.AddRange(style.Select(s => Kv($"dotnet_naming_style.s.{s.Key}", s.Value)));
        var set = EditorConfigNamingRuleSet.Parse(options);
        set.Ignored.Should().BeEmpty();
        return set.Rules.Should().ContainSingle().Subject;
    }

    [Fact]
    public void Parse_FullRule_CarriesEveryPart_AndChecksLikeTheCompiler()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.interfaces_start_with_i.symbols", "interface_group"),
            Kv("dotnet_naming_rule.interfaces_start_with_i.style", "i_prefix_pascal"),
            Kv("dotnet_naming_rule.interfaces_start_with_i.severity", "warning"),
            Kv("dotnet_naming_symbols.interface_group.applicable_kinds", "interface"),
            Kv("dotnet_naming_symbols.interface_group.applicable_accessibilities", "public, friend"),
            Kv("dotnet_naming_symbols.interface_group.required_modifiers", "shared, must_inherit"),
            Kv("dotnet_naming_style.i_prefix_pascal.capitalization", "pascal_case"),
            Kv("dotnet_naming_style.i_prefix_pascal.required_prefix", "I")
        });

        set.Ignored.Should().BeEmpty();
        var rule = set.Rules.Should().ContainSingle().Subject;
        rule.Name.Should().Be("interfaces_start_with_i");
        rule.Kinds.Should().Equal("interface");
        rule.Accessibilities.Should().Equal("public", "internal");
        rule.RequiredModifiers.Should().Equal("static", "abstract");
        rule.Capitalization.Should().Be("pascal_case");
        rule.RequiredPrefix.Should().Be("I");
        rule.RequiredSuffix.Should().Be("");
        rule.Severity.Should().Be("warning");
        rule.ExpectedDescription().Should().Be("'I' + PascalCase");

        rule.Check("IWidget").Should().BeNull();
        rule.Check("Widget").Should().Be("Missing prefix: 'I'");
        rule.Check("Iwidget").Should().Be("These words must begin with upper case characters: widget");
    }

    [Fact]
    public void Parse_WildcardAndAbsentFilters_MeanEveryValue()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.all_pascal.symbols", "everything"),
            Kv("dotnet_naming_rule.all_pascal.style", "pascal"),
            Kv("dotnet_naming_rule.all_pascal.severity", "suggestion"),
            Kv("dotnet_naming_symbols.everything.applicable_kinds", "*"),
            Kv("dotnet_naming_style.pascal.capitalization", "pascal_case")
        });

        var rule = set.Rules.Should().ContainSingle().Subject;
        rule.Kinds.Should().Equal(EditorConfigNamingRule.AllKinds);
        rule.Accessibilities.Should().Equal(EditorConfigNamingRule.AllAccessibilities);
        rule.RequiredModifiers.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Foo_Bar", null)]
    [InlineData("FooBar", null)]
    [InlineData("Foo__Bar", null)]
    [InlineData("Foo_bar", "These words must begin with upper case characters: bar")]
    [InlineData("_Foo", "Prefix '_' is not expected")]
    public void PascalCase_WithWordSeparator(string name, string? reason) =>
        Rule(("capitalization", "pascal_case"), ("word_separator", "_")).Check(name).Should().Be(reason);

    [Theory]
    [InlineData("Bad_Name", null)]
    [InlineData("x", "These words must begin with upper case characters: x")]
    [InlineData("m_Prop", "Prefix 'm_' is not expected")]
    [InlineData("s_Prop", "Prefix 's_' is not expected")]
    [InlineData("__Prop", "Prefix '__' is not expected")]
    public void PascalCase_AcceptsUnderscoresInsideAWord_ButNotACommonPrefix(string name, string? reason) =>
        Rule(("capitalization", "pascal_case")).Check(name).Should().Be(reason);

    [Theory]
    [InlineData("foo_Bar", null)]
    [InlineData("foo_bar", "These non-leading words must begin with an upper case letter: bar")]
    [InlineData("Foo_Bar", "The first word, 'Foo', must begin with a lower case character")]
    public void CamelCase_WithWordSeparator(string name, string? reason) =>
        Rule(("capitalization", "camel_case"), ("word_separator", "_")).Check(name).Should().Be(reason);

    [Theory]
    [InlineData("Foo_bar", null)]
    [InlineData("Foo_Bar", "These non-leading words must begin with a lowercase letter: Bar")]
    public void FirstWordUpper_WithWordSeparator(string name, string? reason) =>
        Rule(("capitalization", "first_word_upper"), ("word_separator", "_")).Check(name).Should().Be(reason);

    [Theory]
    [InlineData("foo_bar", null)]
    [InlineData("Foo_bar", "These words cannot contain upper case characters: Foo")]
    public void AllLower_WithWordSeparator(string name, string? reason) =>
        Rule(("capitalization", "all_lower"), ("word_separator", "_")).Check(name).Should().Be(reason);

    [Theory]
    [InlineData("MAX_ITEMS", null)]
    [InlineData("MaxItems", "These words cannot contain lower case characters: MaxItems")]
    public void AllUpper_WithWordSeparator(string name, string? reason) =>
        Rule(("capitalization", "all_upper"), ("word_separator", "_")).Check(name).Should().Be(reason);

    [Theory]
    [InlineData("DoAsync", null)]
    [InlineData("Do", "Missing suffix: 'Async'")]
    public void RequiredSuffix(string name, string? reason) =>
        Rule(("capitalization", "pascal_case"), ("required_suffix", "Async")).Check(name).Should().Be(reason);

    [Fact]
    public void Parse_RulesTheCompilerIgnores_AreListedWithTheReason()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.no_severity.symbols", "g"),
            Kv("dotnet_naming_rule.no_severity.style", "s"),
            Kv("dotnet_naming_rule.no_style.symbols", "g"),
            Kv("dotnet_naming_rule.no_style.severity", "warning"),
            Kv("dotnet_naming_rule.no_symbols.style", "s"),
            Kv("dotnet_naming_rule.no_symbols.severity", "warning"),
            Kv("dotnet_naming_rule.bad_case.symbols", "g"),
            Kv("dotnet_naming_rule.bad_case.style", "odd"),
            Kv("dotnet_naming_rule.bad_case.severity", "warning"),
            Kv("dotnet_naming_style.s.capitalization", "pascal_case"),
            Kv("dotnet_naming_style.odd.capitalization", "snake_case"),
        });

        set.Rules.Should().BeEmpty();
        set.Ignored.Select(i => $"{i.RuleName}: {i.Reason}").Should().Equal(
            "bad_case: capitalization 'snake_case' is not one of pascal_case, camel_case, first_word_upper, all_upper, all_lower - the rule is ignored, as the compiler ignores it",
            "no_severity: severity is not set - the rule is ignored, as the compiler ignores it",
            "no_style: no style is set - the rule is ignored, as the compiler ignores it",
            "no_symbols: no symbols group is set - the rule is ignored, as the compiler ignores it");
    }

    [Fact]
    public void Order_IsTheCompilersPrecedence_NotTheFilterCount()
    {
        // members: every accessibility, five kinds. private_fields: private only, one kind.
        // consts: const modifier, two kinds. Roslyn orders by modifiers, then accessibility
        // subsets, then kind subsets — so consts precede private_fields precede members.
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.members.symbols", "m"),
            Kv("dotnet_naming_rule.members.style", "p"),
            Kv("dotnet_naming_rule.members.severity", "warning"),
            Kv("dotnet_naming_symbols.m.applicable_kinds", "property, method, field, event, delegate"),
            Kv("dotnet_naming_rule.private_fields.symbols", "f"),
            Kv("dotnet_naming_rule.private_fields.style", "p"),
            Kv("dotnet_naming_rule.private_fields.severity", "warning"),
            Kv("dotnet_naming_symbols.f.applicable_kinds", "field"),
            Kv("dotnet_naming_symbols.f.applicable_accessibilities", "private"),
            Kv("dotnet_naming_rule.consts.symbols", "c"),
            Kv("dotnet_naming_rule.consts.style", "p"),
            Kv("dotnet_naming_rule.consts.severity", "none"),
            Kv("dotnet_naming_symbols.c.applicable_kinds", "field, local"),
            Kv("dotnet_naming_symbols.c.required_modifiers", "const"),
            Kv("dotnet_naming_style.p.capitalization", "pascal_case"),
        });

        set.Rules.Select(r => r.Name).Should().Equal("consts", "private_fields", "members");
        set.Rules[0].IsSilenced.Should().BeTrue("severity none silences what the rule claims");
        set.Rules[1].IsSilenced.Should().BeFalse();
    }

    [Fact]
    public void Order_ConstPrecedesStatic_AndFewerKindsPrecedeMore_OverTheRuleName()
    {
        // The compiler's verdicts: a const field takes z_const over a_static (a const is
        // implicitly static, so const is the larger modifier set), and a plain field takes
        // z_narrow over a_wide (its kinds are a subset) — names would order them the other way.
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.a_static.symbols", "statics"),
            Kv("dotnet_naming_rule.a_static.style", "p"),
            Kv("dotnet_naming_rule.a_static.severity", "warning"),
            Kv("dotnet_naming_symbols.statics.applicable_kinds", "field"),
            Kv("dotnet_naming_symbols.statics.required_modifiers", "static"),
            Kv("dotnet_naming_rule.z_const.symbols", "consts"),
            Kv("dotnet_naming_rule.z_const.style", "p"),
            Kv("dotnet_naming_rule.z_const.severity", "warning"),
            Kv("dotnet_naming_symbols.consts.applicable_kinds", "field"),
            Kv("dotnet_naming_symbols.consts.required_modifiers", "const"),
            Kv("dotnet_naming_rule.a_wide.symbols", "wide"),
            Kv("dotnet_naming_rule.a_wide.style", "p"),
            Kv("dotnet_naming_rule.a_wide.severity", "warning"),
            Kv("dotnet_naming_symbols.wide.applicable_kinds", "field, local"),
            Kv("dotnet_naming_rule.z_narrow.symbols", "narrow"),
            Kv("dotnet_naming_rule.z_narrow.style", "p"),
            Kv("dotnet_naming_rule.z_narrow.severity", "warning"),
            Kv("dotnet_naming_symbols.narrow.applicable_kinds", "field"),
            Kv("dotnet_naming_style.p.capitalization", "pascal_case"),
        });

        set.Rules.Select(r => r.Name).Should().Equal("z_const", "a_static", "z_narrow", "a_wide");
    }
}
