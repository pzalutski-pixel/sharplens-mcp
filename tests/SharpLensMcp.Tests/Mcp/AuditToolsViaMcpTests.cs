using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Audit & Quality category. Every assertion is grounded
// in the engineered fixtures GodObjectFixture and TestCoverageFixture:
//  - GodObjectFixtureClass has 25 fields of 25 distinct types and must be
//    flagged with efferentCoupling >= 25 and memberCount >= 25.
//  - SmallFocusedFixture has 3 members + 2 type deps and must NOT be flagged.
//  - CoverageTarget.NeverCalled has zero callers; OnlyCalledByProduction is
//    reached only from non-test code; CoveredByTest and ChainedFromTest are
//    reached transitively from a [Fact] method.
public class AuditToolsViaMcpTests : McpTestBase
{
    public AuditToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FindGodObjects_FlagsFixtureWithExpectedMetrics()
    {
        var data = await CallAndGetDataAsync("roslyn:find_god_objects", new
        {
            projectName = "SharpLensMcp.Tests",
            minEfferentCoupling = 20,
            minMembers = 20,
            maxResults = 20
        });
        var candidates = data["candidates"] as JArray;
        candidates.Should().NotBeNullOrEmpty();

        var god = candidates!.FirstOrDefault(c =>
            c["typeName"]?.Value<string>()?.EndsWith("GodObjectFixtureClass") == true);
        god.Should().NotBeNull("the engineered god-object fixture must appear in candidates");
        god!["efferentCoupling"]?.Value<int>().Should().BeGreaterOrEqualTo(25,
            "the fixture has 25 fields, each of a distinct GodObjectTypeNN type");
        god["memberCount"]?.Value<int>().Should().BeGreaterOrEqualTo(25,
            "the fixture has 25 fields");
        god["score"]?.Value<double>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FindGodObjects_DoesNotFlagSmallFocusedFixture()
    {
        var data = await CallAndGetDataAsync("roslyn:find_god_objects", new
        {
            projectName = "SharpLensMcp.Tests",
            minEfferentCoupling = 20,
            minMembers = 20,
            maxResults = 50
        });
        var candidates = data["candidates"] as JArray;
        candidates!.Any(c => c["typeName"]?.Value<string>()?.EndsWith("SmallFocusedFixture") == true)
            .Should().BeFalse(
                "SmallFocusedFixture has 3 members and 2 type deps; far below thresholds");
    }

    [Fact]
    public async Task FindGodObjects_ImpossibleThresholds_ReturnsExactlyZero()
    {
        var data = await CallAndGetDataAsync("roslyn:find_god_objects", new
        {
            projectName = "SharpLensMcp.Tests",
            minEfferentCoupling = 100000,
            minMembers = 100000,
            maxResults = 5
        });
        var candidates = data["candidates"] as JArray;
        candidates!.Count.Should().Be(0);
    }

    [Fact]
    public async Task FindUntestedCode_FlagsNeverCalled_AndOnlyCalledByProduction()
    {
        var data = await CallAndGetDataAsync("roslyn:find_untested_code", new
        {
            projectName = "SharpLensMcp.Tests",
            includeProperties = false,
            includeInternal = false,
            maxResults = 200
        });
        data["productionProject"]?.Value<string>().Should().Be("SharpLensMcp.Tests");
        data["testMethodCount"]?.Value<int>().Should().BeGreaterThan(0,
            "the Tests project has many [Fact] methods");

        var uncovered = data["uncoveredSymbols"] as JArray;
        var names = uncovered!.Select(s => s["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.Contains("CoverageTarget.NeverCalled"),
            "NeverCalled has no caller of any kind");
        names.Should().Contain(n => n.Contains("CoverageTarget.OnlyCalledByProduction"),
            "OnlyCalledByProduction is reached only from production code, not from any [Fact]");
    }

    [Fact]
    public async Task FindUntestedCode_ExcludesSymbolsReachedFromTests()
    {
        var data = await CallAndGetDataAsync("roslyn:find_untested_code", new
        {
            projectName = "SharpLensMcp.Tests",
            includeProperties = false,
            includeInternal = false,
            maxResults = 200
        });
        var uncovered = data["uncoveredSymbols"] as JArray;
        var names = uncovered!.Select(s => s["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().NotContain(n => n.Contains("CoverageTarget.CoveredByTest"),
            "CoveredByTest is invoked directly from a [Fact]");
        names.Should().NotContain(n => n.Contains("CoverageTarget.ChainedFromTest"),
            "ChainedFromTest is invoked directly from a [Fact]");
        names.Should().NotContain(n => n.Contains("CoverageTarget.CalledByChainedFromTest"),
            "CalledByChainedFromTest is reached transitively through ChainedFromTest");
    }
}
