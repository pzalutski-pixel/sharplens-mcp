using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// FrameworkTypeResolutionTests through the JSON-RPC dispatcher: the documented
// framework examples resolve, an exact framework match beats a partial source match,
// a source Timer wins over System.Threading.Timer and a member error on it carries the
// shadowing notice, an ambiguous source Timer lists the framework Timer, and a partial
// match is TYPE_NOT_FOUND with suggestions (camelCase on the wire).
public class FrameworkTypeResolutionViaMcpTests
{
    private static McpHarnessOps Load(bool withSecondTimer = false)
    {
        var (workspace, _) = withSecondTimer
            ? TestHelpers.CreateWorkspaceWithMultipleDocuments(
                (FrameworkTypeResolutionTests.Code, "App.cs"), (FrameworkTypeResolutionTests.SecondTimer, "Other.cs"))
            : TestHelpers.CreateWorkspaceWithMultipleDocuments((FrameworkTypeResolutionTests.Code, "App.cs"));
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        return new McpHarnessOps(server);
    }

    [Fact]
    public async Task GetInstantiationOptions_StringBuilder()
    {
        var inner = await Load().CallToolAsync("get_instantiation_options", new { typeName = "StringBuilder" });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["typeName"]!.Value<string>().Should().Be("System.Text.StringBuilder");
    }

    [Fact]
    public async Task FindCatchBlocks_IOException()
    {
        var inner = await Load().CallToolAsync("find_catch_blocks", new { typeName = "IOException" });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["typeName"]!.Value<string>().Should().Be("System.IO.IOException");
        ((JArray)inner["data"]!["matches"]!).Count.Should().Be(1);
    }

    [Fact]
    public async Task FindThrowSites_Exception_IsSystemException()
    {
        var inner = await Load().CallToolAsync("find_throw_sites", new { typeName = "Exception" });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["typeName"]!.Value<string>().Should().Be("System.Exception");
        ((JArray)inner["data"]!["matches"]!).Count.Should().Be(2);
    }

    [Fact]
    public async Task GetTypeMembers_SourceTimerWinsOverTheFrameworkTimer()
    {
        var inner = await Load().CallToolAsync("get_type_members", new { typeName = "Timer" });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["typeName"]!.Value<string>().Should().Be("App.Timer");
    }

    [Fact]
    public async Task GetSuperMethod_MissingMemberOnShadowingType_CarriesTheNotice()
    {
        var inner = await Load().CallToolAsync("get_super_method", new { typeName = "Timer", memberName = "Change" });
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        inner["error"]!["hint"]!.Value<string>().Should().Be(
            "Use get_type_members to see available members. " + FrameworkTypeResolutionTests.TimerNotice);
        ((JArray)inner["error"]!["context"]!["shadowedTypes"]!).Select(s => s.Value<string>())
            .Should().Equal("System.Threading.Timer");
    }

    [Fact]
    public async Task GetTypeMembers_AmbiguousSourceTimer_ListsTheFrameworkTimer()
    {
        var inner = await Load(withSecondTimer: true).CallToolAsync("get_type_members", new { typeName = "Timer" });
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousType);
        ((JArray)inner["error"]!["context"]!["candidates"]!).Select(c => c.Value<string>())
            .Should().Equal("App.Timer", "Other.Timer", "System.Threading.Timer");
    }

    [Fact]
    public async Task GetTypeMembers_PartialMatch_ReturnsTypeNotFoundWithSuggestions()
    {
        var inner = await Load().CallToolAsync("get_type_members", new { typeName = "Ser" });
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
        ((JArray)inner["error"]!["context"]!["suggestions"]!).Select(s => s.Value<string>())
            .Should().Equal("App.Service");
    }
}
