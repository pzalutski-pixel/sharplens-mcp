using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Discovery category (2 tools).
public class DiscoveryToolsViaMcpTests : McpTestBase
{
    public DiscoveryToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetDiRegistrations_AllProjects_ReturnsRegistrationsArray()
    {
        var data = await CallAndGetDataAsync("roslyn:get_di_registrations", new
        {
            projectName = (string?)null
        });
        data["registrations"].Should().NotBeNull(
            "response must always include a registrations array, even if empty");
    }

    [Fact]
    public async Task FindReflectionUsage_AllProjects_ReturnsUsagesArray()
    {
        var data = await CallAndGetDataAsync("roslyn:find_reflection_usage", new
        {
            projectName = (string?)null,
            maxResults = 50
        });
        data["usages"].Should().NotBeNull(
            "response must always include a usages array");
    }
}
