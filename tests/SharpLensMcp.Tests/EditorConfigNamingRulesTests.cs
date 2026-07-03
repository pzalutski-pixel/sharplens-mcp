using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// Pure unit coverage for EditorConfigNamingRuleSet.Parse: a synthetic key set
// exercises the supported shape, every v1-unsupported feature, and the
// wildcard filters — no workspace involved.
public class EditorConfigNamingRulesTests
{
    private static KeyValuePair<string, string> Kv(string key, string value) => new(key, value);

    [Fact]
    public void Parse_FullySupportedRule_CarriesAllParts()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.interfaces_start_with_i.symbols", "interface_group"),
            Kv("dotnet_naming_rule.interfaces_start_with_i.style", "i_prefix_pascal"),
            Kv("dotnet_naming_rule.interfaces_start_with_i.severity", "warning"),
            Kv("dotnet_naming_symbols.interface_group.applicable_kinds", "interface"),
            Kv("dotnet_naming_symbols.interface_group.applicable_accessibilities", "public, internal"),
            Kv("dotnet_naming_style.i_prefix_pascal.capitalization", "pascal_case"),
            Kv("dotnet_naming_style.i_prefix_pascal.required_prefix", "I")
        });

        set.Unsupported.Should().BeEmpty();
        set.Rules.Should().HaveCount(1);
        var rule = set.Rules[0];
        rule.Name.Should().Be("interfaces_start_with_i");
        rule.Kinds.Should().BeEquivalentTo(new[] { "interface" });
        rule.Accessibilities.Should().BeEquivalentTo(new[] { "public", "internal" });
        rule.Capitalization.Should().Be("pascal_case");
        rule.RequiredPrefix.Should().Be("I");
        rule.RequiredSuffix.Should().BeNull();
        rule.ExpectedDescription().Should().Be("'I' + PascalCase");

        rule.IsSatisfiedBy("IWidget").Should().BeTrue();
        rule.IsSatisfiedBy("Widget").Should().BeFalse("the I prefix is required");
        rule.IsSatisfiedBy("Iwidget").Should().BeFalse("the remainder must be PascalCase");
    }

    [Fact]
    public void Parse_WildcardFilters_MeanEverything()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.all_pascal.symbols", "everything"),
            Kv("dotnet_naming_rule.all_pascal.style", "pascal"),
            Kv("dotnet_naming_symbols.everything.applicable_kinds", "*"),
            Kv("dotnet_naming_style.pascal.capitalization", "pascal_case")
        });

        set.Rules.Should().HaveCount(1);
        set.Rules[0].Kinds.Should().BeEmpty("'*' is modeled as the empty (match-all) set");
        set.Rules[0].Accessibilities.Should().BeEmpty("an absent filter matches everything");
    }

    [Fact]
    public void Parse_RequiredModifiers_IsReportedUnsupported()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.special.symbols", "group"),
            Kv("dotnet_naming_rule.special.style", "style"),
            Kv("dotnet_naming_symbols.group.applicable_kinds", "field"),
            Kv("dotnet_naming_symbols.group.required_modifiers", "const"),
            Kv("dotnet_naming_style.style.capitalization", "pascal_case")
        });

        set.Rules.Should().BeEmpty();
        set.Unsupported.Should().ContainSingle();
        set.Unsupported[0].RuleName.Should().Be("special");
        set.Unsupported[0].Reason.Should().Be("required_modifiers is not supported");
    }

    [Fact]
    public void Parse_WordSeparator_IsHonored_PerSegmentCapitalization()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.special.symbols", "group"),
            Kv("dotnet_naming_rule.special.style", "style"),
            Kv("dotnet_naming_symbols.group.applicable_kinds", "field"),
            Kv("dotnet_naming_style.style.capitalization", "pascal_case"),
            Kv("dotnet_naming_style.style.word_separator", "_")
        });

        set.Unsupported.Should().BeEmpty("word_separator with a supported capitalization is now honored");
        var rule = set.Rules.Should().ContainSingle().Subject;
        rule.WordSeparator.Should().Be("_");

        rule.IsSatisfiedBy("Foo_Bar").Should().BeTrue("each underscore-separated segment is PascalCase");
        rule.IsSatisfiedBy("FooBar").Should().BeTrue("a single PascalCase word needs no separator");
        rule.IsSatisfiedBy("Foo_bar").Should().BeFalse("the second segment is not PascalCase");
        rule.IsSatisfiedBy("_Foo").Should().BeFalse("a leading separator makes an empty segment");
        rule.IsSatisfiedBy("Foo__Bar").Should().BeFalse("a doubled separator makes an empty segment");
    }

    [Fact]
    public void Parse_UnsupportedCapitalization_IsReported()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.constants_upper.symbols", "group"),
            Kv("dotnet_naming_rule.constants_upper.style", "upper"),
            Kv("dotnet_naming_symbols.group.applicable_kinds", "field"),
            Kv("dotnet_naming_style.upper.capitalization", "all_upper")
        });

        set.Rules.Should().BeEmpty();
        set.Unsupported.Should().ContainSingle()
            .Which.Reason.Should().Be("capitalization 'all_upper' is not supported");
    }

    [Fact]
    public void Parse_DanglingRule_IsReported()
    {
        var set = EditorConfigNamingRuleSet.Parse(new[]
        {
            Kv("dotnet_naming_rule.broken.style", "nowhere")
        });

        set.Rules.Should().BeEmpty();
        set.Unsupported.Should().ContainSingle()
            .Which.Reason.Should().Be("symbols group not found");
    }
}
