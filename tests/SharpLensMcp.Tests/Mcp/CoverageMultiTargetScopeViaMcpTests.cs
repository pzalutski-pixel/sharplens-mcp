using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Mirrors CoverageMultiTargetScopeTests through the MCP dispatcher: find_untested_code
// accepts a multi-targeted project's plain name and lists each uncovered member once.
public class CoverageMultiTargetScopeViaMcpTests
{
    [Fact]
    public async Task FindUntestedCode_PlainMultiTargetName_ListsEachUncoveredMemberOnce()
    {
        using var fixture = new MultiTargetSolutionFixture(
            CoverageMultiTargetScopeTests.MultiSource, CoverageMultiTargetScopeTests.ConsumerSource);
        var server = new McpServer(TimeSpan.FromMinutes(5));
        server.LoadFromWorkspaceForTesting(fixture.Workspace);

        var inner = await new McpHarnessOps(server).CallToolAsync(
            "find_untested_code", new { projectName = MultiTargetSolutionFixture.MultiName });

        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["productionProject"]!.Value<string>().Should().Be("Multi");
        ((JArray)inner["data"]!["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>())
            .Should().Equal("Multi.Calc.Modern()", "Multi.Calc.Never()");
    }
}
