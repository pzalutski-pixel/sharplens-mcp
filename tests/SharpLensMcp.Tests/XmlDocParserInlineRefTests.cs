using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// XmlDocParser must substitute inline empty-element references (see cref,
// paramref, see langword) instead of dropping them, and read <overloads>.
public class XmlDocParserInlineRefTests
{
    [Fact]
    public void SubstitutesInlineCrefParamrefAndLangword()
    {
        const string xml = """
            <member name="M:Foo.Do(System.Int32)">
                <summary>See <see cref="T:Foo.Bar"/> and <paramref name="x"/>; pass <see langword="null"/> for default.</summary>
            </member>
            """;
        var (docs, _, _) = XmlDocParser.Parse(xml);

        docs.Summary.Should().Be("See Foo.Bar and x; pass null for default.",
            "inline cref/paramref/langword must be substituted, not dropped");
    }

    [Fact]
    public void ReadsOverloadsSection()
    {
        const string xml = """
            <member name="M:Foo.Do">
                <summary>Does the thing.</summary>
                <overloads>Several ways to do the thing.</overloads>
            </member>
            """;
        var (docs, _, _) = XmlDocParser.Parse(xml);

        docs.Overloads.Should().Be("Several ways to do the thing.");
    }
}
