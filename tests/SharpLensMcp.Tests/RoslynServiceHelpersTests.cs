using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// Direct unit tests for internal helper methods on RoslynService that are
// hard to fully exercise through tool-level integration tests:
//   - MatchesGlobPattern: regex-special-char escaping
//   - DetectCycles: actual cycle detection, cycle format
public class RoslynServiceHelpersTests
{
    [Theory]
    [InlineData("anything", "", true)]    // empty pattern → matches everything
    [InlineData("", "", true)]             // empty pattern + empty input → matches
    [InlineData("", "anything", false)]    // empty input does NOT match a non-empty pattern
    [InlineData("Foo", "Foo", true)]
    [InlineData("Foo", "foo", true)] // case-insensitive
    [InlineData("Foo", "Bar", false)]
    [InlineData("FooBar", "Foo*", true)]
    [InlineData("FooBar", "*Bar", true)]
    [InlineData("FooXBar", "Foo*Bar", true)]
    [InlineData("FooBar", "Fo?Bar", true)]
    [InlineData("FooBar", "Fo?bar", true)] // ? case-insensitive too
    [InlineData("FooBar", "F?Bar", false)] // ? is exactly one char
    [InlineData("Foo", "F*o*", true)]
    public void MatchesGlobPattern_BasicWildcards(string input, string pattern, bool expected)
    {
        RoslynService.MatchesGlobPattern(input, pattern).Should().Be(expected);
    }

    [Theory]
    [InlineData("System.Linq", "System.Linq")]
    [InlineData("System.Linq", "System.*")]
    [InlineData("System.Linq", "*.Linq")]
    [InlineData("MyApp.Core.Services", "MyApp.*.Services")]
    public void MatchesGlobPattern_DotsAreLiteralNotRegexAny(string input, string pattern)
    {
        // Dots in the pattern must be regex-escaped so 'System.Linq' does NOT
        // match 'SystemXLinq'. A regression that forgot Regex.Escape would
        // pass 'SystemXLinq' against 'System.Linq'.
        RoslynService.MatchesGlobPattern(input, pattern).Should().BeTrue();
    }

    [Theory]
    [InlineData("SystemXLinq", "System.Linq", false)]
    [InlineData("System_Linq", "System.Linq", false)]
    public void MatchesGlobPattern_DotDoesNotMatchOtherChars(string input, string pattern, bool expected)
    {
        RoslynService.MatchesGlobPattern(input, pattern).Should().Be(expected);
    }

    [Theory]
    [InlineData("List`1", "List`1", true)]    // backtick is not regex-special
    [InlineData("A+B", "A+B", true)]           // + IS regex-special (escaped)
    [InlineData("A(b)c", "A(b)c", true)]       // parens ARE regex-special (escaped)
    [InlineData("A|B", "A|B", true)]           // | IS regex-special (escaped)
    [InlineData("A[b]", "A[b]", true)]         // brackets ARE regex-special (escaped)
    public void MatchesGlobPattern_RegexSpecialCharsAreEscaped(string input, string pattern, bool expected)
    {
        RoslynService.MatchesGlobPattern(input, pattern).Should().Be(expected);
    }

    [Fact]
    public void DetectCycles_EmptyGraph_ReturnsNoCycles()
    {
        var service = new RoslynService();
        var graph = new Dictionary<string, List<string>>();

        service.DetectCycles(graph).Should().BeEmpty();
    }

    [Fact]
    public void DetectCycles_DagWithNoCycles_ReturnsNoCycles()
    {
        // A → B → C, A → D. No cycles.
        var service = new RoslynService();
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = new() { "B", "D" },
            ["B"] = new() { "C" },
            ["C"] = new(),
            ["D"] = new()
        };

        service.DetectCycles(graph).Should().BeEmpty();
    }

    [Fact]
    public void DetectCycles_SimpleTwoNodeCycle_DetectsItWithStartRepeatedAtEnd()
    {
        // A → B → A.
        var service = new RoslynService();
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = new() { "B" },
            ["B"] = new() { "A" }
        };

        var cycles = service.DetectCycles(graph);
        cycles.Should().HaveCount(1);
        // Per the impl (RoslynService.cs:586-589) the cycle is reported as
        // [first-node, ..., back-edge-target, first-node-repeated]. For A→B→A
        // starting from A, the output is [A, B, A].
        cycles[0].Should().Equal("A", "B", "A");
    }

    [Fact]
    public void DetectCycles_SelfLoop_DetectsAsLengthTwoCycle()
    {
        // A → A.
        var service = new RoslynService();
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = new() { "A" }
        };

        var cycles = service.DetectCycles(graph);
        cycles.Should().HaveCount(1);
        cycles[0].Should().Equal("A", "A");
    }

    [Fact]
    public void DetectCycles_ThreeNodeCycle_PreservesOrder()
    {
        // A → B → C → A.
        var service = new RoslynService();
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = new() { "B" },
            ["B"] = new() { "C" },
            ["C"] = new() { "A" }
        };

        var cycles = service.DetectCycles(graph);
        cycles.Should().HaveCount(1);
        cycles[0].Should().Equal("A", "B", "C", "A");
    }

    [Fact]
    public void DetectCycles_TwoDisjointCycles_DetectsBoth()
    {
        // A → B → A,  C → D → C.
        var service = new RoslynService();
        var graph = new Dictionary<string, List<string>>
        {
            ["A"] = new() { "B" },
            ["B"] = new() { "A" },
            ["C"] = new() { "D" },
            ["D"] = new() { "C" }
        };

        var cycles = service.DetectCycles(graph);
        cycles.Should().HaveCount(2);
    }

    [Fact]
    public void DetectCycles_CycleWithLeadingDagPath_RecordsOnlyTheCyclePortion()
    {
        // X → A → B → A. The cycle [A, B, A] should be reported; X is a
        // pre-cycle approach path not part of the cycle.
        var service = new RoslynService();
        var graph = new Dictionary<string, List<string>>
        {
            ["X"] = new() { "A" },
            ["A"] = new() { "B" },
            ["B"] = new() { "A" }
        };

        var cycles = service.DetectCycles(graph);
        cycles.Should().HaveCount(1);
        cycles[0].Should().Equal("A", "B", "A");
    }
}
