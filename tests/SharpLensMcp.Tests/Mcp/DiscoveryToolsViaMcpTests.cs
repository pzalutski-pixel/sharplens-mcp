using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Discovery category (2 tools). Both assertions are
// grounded in concrete facts about the SharpLens codebase:
//  - SharpLensMcp is a stdio MCP server with no DI container; the
//    get_di_registrations result is deterministically empty.
//  - RoslynService.cs uses System.Type.GetProperty + PropertyInfo.GetValue
//    in IsSuccessResponse/GetResponseData/GetResponseError (3 each = 6
//    reflection sites), so find_reflection_usage must surface them with
//    those exact reflectionApi labels.
public class DiscoveryToolsViaMcpTests : McpTestBase
{
    public DiscoveryToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetDiRegistrations_OnOurSolution_IsEmpty()
    {
        // SharpLensMcp doesn't use IServiceCollection. The expected count is exactly zero;
        // if this ever changes, the test should fail and force the maintainer to update
        // both the implementation and this assertion together.
        var data = await CallAndGetDataAsync("roslyn:get_di_registrations", new
        {
            projectName = (string?)null
        });
        var registrations = data["registrations"] as JArray;
        registrations.Should().NotBeNull();
        registrations!.Count.Should().Be(0,
            "SharpLensMcp has no AddScoped/AddTransient/AddSingleton call sites");
    }

    [Fact]
    public async Task FindReflectionUsage_FindsTypeGetPropertyInRoslynService()
    {
        var data = await CallAndGetDataAsync("roslyn:find_reflection_usage", new
        {
            projectName = (string?)null,
            maxResults = 50
        });
        var usages = data["usages"] as JArray;
        usages.Should().NotBeNullOrEmpty(
            "RoslynService.IsSuccessResponse uses Type.GetProperty which must be detected");

        // IsSuccessResponse, GetResponseData, GetResponseError each call response.GetType().GetProperty(...).
        // The tool labels the API as "{ContainingTypeName}.{MethodName}" -> "Type.GetProperty".
        var getPropertyHits = usages!
            .Where(u => u["reflectionApi"]?.Value<string>() == "Type.GetProperty"
                     && u["location"]?["filePath"]?.Value<string>()?.EndsWith("RoslynService.cs") == true)
            .ToList();
        getPropertyHits.Count.Should().BeGreaterOrEqualTo(3,
            "IsSuccessResponse + GetResponseData + GetResponseError each invoke Type.GetProperty");
    }

    [Fact]
    public async Task FindReflectionUsage_FindsPropertyInfoGetValueInRoslynService()
    {
        var data = await CallAndGetDataAsync("roslyn:find_reflection_usage", new
        {
            projectName = (string?)null,
            maxResults = 50
        });
        var usages = data["usages"] as JArray;

        // The same three helpers each call prop.GetValue(response) — PropertyInfo lives
        // in System.Reflection so the tool's namespace.StartsWith("System.Reflection")
        // branch matches.
        var getValueHits = usages!
            .Where(u => u["reflectionApi"]?.Value<string>() == "PropertyInfo.GetValue"
                     && u["location"]?["filePath"]?.Value<string>()?.EndsWith("RoslynService.cs") == true)
            .ToList();
        getValueHits.Count.Should().BeGreaterOrEqualTo(3,
            "the three response-reading helpers each call PropertyInfo.GetValue");
    }

    [Fact]
    public async Task FindReflectionUsage_HonestTotalCount()
    {
        // The 1.5.3 totalCount fix says totalFound must increment even when results
        // are capped by maxResults. Cap to 1; assert totalCount > returnedCount.
        var inner = await CallToolAsync("roslyn:find_reflection_usage", new
        {
            projectName = (string?)null,
            maxResults = 1
        });
        inner["success"]?.Value<bool>().Should().BeTrue();

        var meta = inner["meta"]!;
        var total = meta["totalCount"]!.Value<int>();
        var returned = meta["returnedCount"]!.Value<int>();
        returned.Should().Be(1, "maxResults:1 must cap returnedCount");
        total.Should().BeGreaterThan(returned,
            "totalCount must report the true unbounded count, not the capped one");
    }
}
