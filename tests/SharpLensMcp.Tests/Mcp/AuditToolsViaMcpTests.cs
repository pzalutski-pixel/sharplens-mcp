using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Audit & Quality category (2 tools).
public class AuditToolsViaMcpTests : McpTestBase
{
    public AuditToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task FindGodObjects_OnTestsProject_FlagsFixtureClass()
    {
        var data = await CallAndGetDataAsync("roslyn:find_god_objects", new
        {
            projectName = "SharpLensMcp.Tests",
            minEfferentCoupling = 20,
            minMembers = 20,
            maxResults = 20
        });
        var candidates = data["candidates"] as JArray;
        candidates.Should().NotBeNull();
        var names = candidates!.Select(c => c["typeName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith("GodObjectFixtureClass"),
            "the engineered god-object fixture (25 fields, 25 distinct types) must be flagged");
    }

    [Fact]
    public async Task FindGodObjects_ImpossibleThresholds_ReturnsEmpty()
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
    public async Task FindUntestedCode_OnTestsProject_FlagsNeverCalled()
    {
        var data = await CallAndGetDataAsync("roslyn:find_untested_code", new
        {
            projectName = "SharpLensMcp.Tests",
            includeProperties = false,
            includeInternal = false,
            maxResults = 200
        });
        var uncovered = data["uncoveredSymbols"] as JArray;
        uncovered.Should().NotBeNull();
        var names = uncovered!.Select(s => s["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.Contains("CoverageTarget.NeverCalled"));
    }
}
