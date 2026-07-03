using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for the Discovery category (2 tools). Both assertions are
// grounded in concrete facts about the SharpLens codebase:
//  - SharpLensMcp is a stdio MCP server with no DI container, so
//    get_di_registrations is deterministically empty.
//  - RoslynService's response-reading helpers (IsSuccessResponse, GetResponseData,
//    GetResponseError) use Type.GetProperty + PropertyInfo.GetValue.
//    find_reflection_usage must surface them with `reflectionApi` =
//    "Type.GetProperty" / "PropertyInfo.GetValue".
//
// Response shapes covered: get_di_registrations, find_reflection_usage.
//
// Tightening rule: every accessor uses `!.Value<T>()`.
public class DiscoveryToolsViaMcpTests : McpTestBase
{
    public DiscoveryToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetDiRegistrations_OnOurSolution_IsEmptyWithMatchingMetaCounts()
    {
        // SharpLensMcp doesn't use IServiceCollection. Expected count is exactly
        // zero — a regression that started flagging non-DI Add* methods would
        // populate this list.
        var inner = await CallToolAsync("get_di_registrations", new
        {
            projectName = (string?)null
        });
        inner["success"]!.Value<bool>().Should().BeTrue();

        var data = inner["data"]!;
        var registrations = (data["registrations"] as JArray)!;
        registrations.Count.Should().Be(0,
            "SharpLensMcp has no AddScoped/AddTransient/AddSingleton call sites");

        // Lock the meta counters too. A regression that
        // populated registrations but left meta.totalCount stale would slip past
        // the registrations.Count check.
        var meta = inner["meta"]!;
        meta["totalCount"]!.Value<int>().Should().Be(0);
        meta["returnedCount"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task FindReflectionUsage_FindsTypeGetPropertyInRoslynServiceWithExpectedShape()
    {
        var data = await CallAndGetDataAsync("find_reflection_usage", new
        {
            projectName = (string?)null,
            maxResults = 50
        });
        var usages = (data["usages"] as JArray)!;
        usages.Should().NotBeEmpty(
            "RoslynService.IsSuccessResponse uses Type.GetProperty which must be detected");

        // IsSuccessResponse, GetResponseData, GetResponseError each call
        // response.GetType().GetProperty(...). The tool labels the API as
        // "{ContainingTypeName}.{MethodName}" = "Type.GetProperty"
        // The predicate's `?.` chain returning false-on-null
        // is safe here — non-matching entries are skipped, not silently counted.
        var getPropertyHits = usages
            .Where(u => u["reflectionApi"]?.Value<string>() == "Type.GetProperty"
                     && u["location"]?["filePath"]?.Value<string>()?.EndsWith("RoslynService.cs") == true)
            .ToList();
        getPropertyHits.Count.Should().BeGreaterOrEqualTo(3,
            "IsSuccessResponse + GetResponseData + GetResponseError each invoke Type.GetProperty");

        // Per-entry shape: reflectionApi, context, location.
        foreach (var hit in getPropertyHits)
        {
            hit["reflectionApi"]!.Value<string>().Should().Be("Type.GetProperty");
            hit["context"]!.Value<string>().Should().NotBeNullOrEmpty(
                "context is the invocation source text (truncated at 200 chars)");
            var location = hit["location"]!;
            location["filePath"]!.Value<string>()!.Should().EndWith("RoslynService.cs");
            location["line"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
            location["column"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        }
    }

    [Fact]
    public async Task FindReflectionUsage_FindsPropertyInfoGetValueInRoslynService()
    {
        var data = await CallAndGetDataAsync("find_reflection_usage", new
        {
            projectName = (string?)null,
            maxResults = 50
        });
        var usages = (data["usages"] as JArray)!;

        // The same three helpers each call prop.GetValue(response). PropertyInfo
        // lives in System.Reflection so the namespace.StartsWith("System.Reflection")
        // reflection-detection branch fires.
        var getValueHits = usages
            .Where(u => u["reflectionApi"]?.Value<string>() == "PropertyInfo.GetValue"
                     && u["location"]?["filePath"]?.Value<string>()?.EndsWith("RoslynService.cs") == true)
            .ToList();
        getValueHits.Count.Should().BeGreaterOrEqualTo(3,
            "the three response-reading helpers each call PropertyInfo.GetValue");
    }

    [Fact]
    public async Task FindReflectionUsage_HonestTotalCount_TotalExceedsReturnedWhenCapped()
    {
        // Contract: totalCount must report the TRUE unbounded count even when
        // results are capped (totalFound increments BEFORE the
        // maxResults check at line 198). Cap to 1 and assert total > returned.
        var inner = await CallToolAsync("find_reflection_usage", new
        {
            projectName = (string?)null,
            maxResults = 1
        });
        inner["success"]!.Value<bool>().Should().BeTrue();

        var meta = inner["meta"]!;
        var total = meta["totalCount"]!.Value<int>();
        var returned = meta["returnedCount"]!.Value<int>();
        returned.Should().Be(1, "maxResults:1 must cap returnedCount");
        total.Should().BeGreaterThan(returned,
            "totalCount must report the true unbounded count, not the capped one");
    }
}
