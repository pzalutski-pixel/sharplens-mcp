using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Tests for get_call_graph: depth bound, cycle detection, fan-out cap,
// callers vs callees direction. Solution-loaded so we exercise the real
// SymbolFinder.FindCallersAsync path for the callers direction.
public class CallGraphTests : RoslynServiceTestBase
{
    private async Task<(string filePath, int line, int column)> LocateAsync(string methodName)
    {
        var searchResult = await Service.SearchSymbolsAsync(methodName, kind: "Method", maxResults: 20);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty($"fixture must declare {methodName}");
        var match = symbols!.First(s =>
            s["containingType"]?.Value<string>()?.EndsWith("CallGraphFixture") == true);
        var loc = match["location"]!;
        return (loc["filePath"]!.Value<string>()!, loc["line"]!.Value<int>(), loc["column"]!.Value<int>());
    }

    [Fact]
    public async Task GetCallGraph_CalleesDepth2_FromChainA_ReachesC()
    {
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 2, maxNodes: 100);
        AssertSuccess(result);

        var data = GetData(result);
        var nodes = data["nodes"] as JArray;
        nodes.Should().NotBeNull();
        var names = nodes!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainA()"));
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().Contain(n => n.EndsWith(".ChainC()"), "depth:2 must reach the chain leaf");
    }

    [Fact]
    public async Task GetCallGraph_CalleesDepth1_FromChainA_StopsAtB_AndFlagsTruncation()
    {
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 1, maxNodes: 100);
        AssertSuccess(result);

        var data = GetData(result);
        var nodes = data["nodes"] as JArray;
        var names = nodes!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().NotContain(n => n.EndsWith(".ChainC()"), "depth:1 must stop at ChainB");
        data["truncatedByDepth"]?.Value<bool>().Should().BeTrue("ChainB has unexplored callees at depth 2");
    }

    [Fact]
    public async Task GetCallGraph_OnCycle_TerminatesAndRecordsCycle()
    {
        var (file, line, col) = await LocateAsync("CycleD");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 5, maxNodes: 50);
        AssertSuccess(result);

        var data = GetData(result);
        var nodes = data["nodes"] as JArray;
        nodes.Should().NotBeNullOrEmpty();
        var names = nodes!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".CycleD()"));
        names.Should().Contain(n => n.EndsWith(".CycleE()"));

        var cycles = data["cyclesDetected"] as JArray;
        cycles.Should().NotBeNullOrEmpty("D -> E -> D is a cycle and must be reported");
    }

    [Fact]
    public async Task GetCallGraph_FanOutHub_RespectsMaxNodes()
    {
        var (file, line, col) = await LocateAsync("HubMethod");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 2, maxNodes: 3);
        AssertSuccess(result);

        var data = GetData(result);
        var nodes = data["nodes"] as JArray;
        nodes!.Count.Should().BeLessOrEqualTo(3, "maxNodes cap must hold");
        data["truncatedByNodes"]?.Value<bool>().Should().BeTrue("hub has 4 leaves but only 3 nodes allowed");
    }

    [Fact]
    public async Task GetCallGraph_CallersDirection_OnChainC_FindsChainBAndA()
    {
        var (file, line, col) = await LocateAsync("ChainC");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callers", maxDepth: 5, maxNodes: 50);
        AssertSuccess(result);

        var data = GetData(result);
        var nodes = data["nodes"] as JArray;
        nodes.Should().NotBeNullOrEmpty();
        var names = nodes!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"), "ChainC is called by ChainB");
        names.Should().Contain(n => n.EndsWith(".ChainA()"), "ChainA transitively calls ChainC via ChainB");
    }

    [Fact]
    public async Task GetCallGraph_RejectsZeroMaxDepth()
    {
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, maxDepth: 0);
        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task GetCallGraph_RejectsMaxDepthAbove10()
    {
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, maxDepth: 100);
        AssertError(result, ErrorCodes.InvalidParameter);
    }
}
