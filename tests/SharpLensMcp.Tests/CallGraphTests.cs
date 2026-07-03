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
    public async Task GetCallGraph_CalleesDepth3_FromChainA_ReachesC_AndLocksRootEdgesDirection()
    {
        // Depth 3 fully covers the 3-node chain with truncatedByDepth=false: the BFS
        // raises the flag only when a node at the depth boundary actually has neighbors
        // left to expand, so the ChainC leaf at the boundary does not trip it.
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 3, maxNodes: 100);
        AssertSuccess(result);

        var data = GetData(result);
        // Root node metadata round-trips the starting symbol.
        data["root"]!["fullName"].Should().NotBeNull();
        data["root"]!["fullName"]!.Value<string>().Should().EndWith(".ChainA()",
            "the root field must echo the symbol the caller pointed at");
        data["direction"].Should().NotBeNull();
        data["direction"]!.Value<string>().Should().Be("callees",
            "direction must round-trip the caller's input");
        data["maxDepth"].Should().NotBeNull();
        data["maxDepth"]!.Value<int>().Should().Be(3);
        data["truncatedByDepth"].Should().NotBeNull();
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse(
            "depth=3 lets every leaf process its (empty) callee list before the depth check");
        data["truncatedByNodes"].Should().NotBeNull();
        data["truncatedByNodes"]!.Value<bool>().Should().BeFalse(
            "maxNodes=100 far exceeds the 3-node chain");
        GetMeta(result)["Truncated"]!.Value<bool>().Should().BeFalse(
            "neither the depth nor the node cap dropped anything, so meta.Truncated must be false");

        var nodes = data["nodes"] as JArray;
        nodes.Should().NotBeNullOrEmpty();
        var nodesByName = nodes!.ToDictionary(
            n => n["fullName"]?.Value<string>() ?? "",
            n => n["id"]?.Value<int>() ?? -1);
        var aId = nodesByName.Single(kv => kv.Key.EndsWith(".ChainA()")).Value;
        var bId = nodesByName.Single(kv => kv.Key.EndsWith(".ChainB()")).Value;
        var cId = nodesByName.Single(kv => kv.Key.EndsWith(".ChainC()")).Value;

        var edges = data["edges"] as JArray;
        edges.Should().NotBeNullOrEmpty("a 3-node chain must produce 2 edges");
        var edgePairs = edges!
            .Select(e => (from: e["from"]?.Value<int>() ?? -1, to: e["to"]?.Value<int>() ?? -1))
            .ToList();
        edgePairs.Should().Contain((aId, bId),
            "ChainA → ChainB must appear in edges (direction=callees)");
        edgePairs.Should().Contain((bId, cId),
            "ChainB → ChainC must appear in edges (direction=callees)");
    }

    [Fact]
    public async Task GetCallGraph_CalleesDepth1_FromChainA_StopsAtB_TruncatedByDepthOnlyNotByNodes()
    {
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 1, maxNodes: 100);
        AssertSuccess(result);

        var data = GetData(result);
        var nodes = data["nodes"] as JArray;
        var names = nodes!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().NotContain(n => n.EndsWith(".ChainC()"), "depth:1 must stop at ChainB");
        data["truncatedByDepth"].Should().NotBeNull();
        data["truncatedByDepth"]!.Value<bool>().Should().BeTrue("ChainB has unexplored callees at depth 2");
        data["truncatedByNodes"].Should().NotBeNull();
        data["truncatedByNodes"]!.Value<bool>().Should().BeFalse(
            "maxNodes=100 is not the cause of truncation — depth is");
        GetMeta(result)["Truncated"]!.Value<bool>().Should().BeTrue(
            "the depth bound dropped paths beyond ChainB, so meta.Truncated must surface it");
    }

    [Fact]
    public async Task GetCallGraph_CalleesDepth2_FromChainA_LeafAtBoundaryDoesNotTripTruncation()
    {
        // ChainA → ChainB → ChainC. At maxDepth=2, ChainC is reached at the depth
        // boundary but calls nothing, so the depth-truncation flag must stay false:
        // a true leaf loses nothing by not being expanded.
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 2, maxNodes: 100);
        AssertSuccess(result);

        var data = GetData(result);
        var names = (data["nodes"] as JArray)!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainC()"), "depth 2 reaches the full chain");
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse(
            "ChainC is a leaf at the boundary — there is nothing deeper to truncate");
    }

    [Fact]
    public async Task GetCallGraph_CallersDirection_OnCycle_DetectsTheCycle()
    {
        // CycleD ⇄ CycleE. Walking the CALLER direction must detect the back-edge
        // as a cycle via ancestor reachability, not only the self-loop case.
        var (file, line, col) = await LocateAsync("CycleD");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callers", maxDepth: 5, maxNodes: 50);
        AssertSuccess(result);

        var cycles = GetData(result)["cyclesDetected"] as JArray;
        cycles.Should().NotBeNullOrEmpty("D ⇄ E is a cycle reachable in the callers direction too");
        cycles!.Any(c =>
        {
            var s = c.Value<string>() ?? "";
            return s.Contains("CycleD") && s.Contains("CycleE");
        }).Should().BeTrue("the callers-direction cycle entry must name both CycleD and CycleE");
    }

    [Fact]
    public async Task GetCallGraph_OnCycle_TerminatesAndRecordsCycleMentioningBothMethods()
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
        // Lock the entry's content: at least one cycle string must mention BOTH CycleD and CycleE.
        cycles!.Any(c =>
        {
            var s = c.Value<string>() ?? "";
            return s.Contains("CycleD") && s.Contains("CycleE");
        }).Should().BeTrue(
            "the cycle entry must name both CycleD and CycleE — not just an unnamed cycle count");
    }

    [Fact]
    public async Task GetCallGraph_FanOutHub_RespectsMaxNodesExactly()
    {
        var (file, line, col) = await LocateAsync("HubMethod");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 2, maxNodes: 3);
        AssertSuccess(result);

        var data = GetData(result);
        var nodes = data["nodes"] as JArray;
        // HubMethod calls 4 leaves; with cap=3 we expect Hub + 2 leaves = exactly 3.
        nodes!.Count.Should().Be(3, "maxNodes=3 must fill the cap when more candidates exist");
        data["truncatedByNodes"].Should().NotBeNull();
        data["truncatedByNodes"]!.Value<bool>().Should().BeTrue("hub has 4 leaves but only 3 nodes allowed");
        GetMeta(result)["Truncated"]!.Value<bool>().Should().BeTrue(
            "the node cap dropped a leaf, so meta.Truncated must surface it");
    }

    [Fact]
    public async Task GetCallGraph_DiamondAtNodeCap_KeepsBothEdgesIntoSharedNode()
    {
        // Top -> Left -> Bottom and Top -> Right -> Bottom. With maxNodes=4 every
        // node fits, so BOTH edges into Bottom must be present. The second one
        // (Right -> Bottom) was previously dropped: Bottom got marked visited but
        // the BFS stopped at the cap before the edge back to it could be drawn.
        var (file, line, col) = await LocateAsync("DiamondTop");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callees", maxDepth: 5, maxNodes: 4);
        AssertSuccess(result);

        var data = GetData(result);
        var nodes = data["nodes"] as JArray;
        nodes!.Count.Should().Be(4, "the diamond has exactly four distinct nodes");
        data["truncatedByNodes"]!.Value<bool>().Should().BeFalse(
            "four nodes fit a cap of four — nothing is truncated");

        var nodesByName = nodes!.ToDictionary(
            n => n["fullName"]!.Value<string>()!,
            n => n["id"]!.Value<int>());
        var topId = nodesByName.Single(kv => kv.Key.EndsWith(".DiamondTop()")).Value;
        var leftId = nodesByName.Single(kv => kv.Key.EndsWith(".DiamondLeft()")).Value;
        var rightId = nodesByName.Single(kv => kv.Key.EndsWith(".DiamondRight()")).Value;
        var bottomId = nodesByName.Single(kv => kv.Key.EndsWith(".DiamondBottom()")).Value;

        var edgePairs = (data["edges"] as JArray)!
            .Select(e => (from: e["from"]!.Value<int>(), to: e["to"]!.Value<int>()))
            .ToList();
        edgePairs.Should().Contain((topId, leftId));
        edgePairs.Should().Contain((topId, rightId));
        edgePairs.Should().Contain((leftId, bottomId));
        edgePairs.Should().Contain((rightId, bottomId),
            "the second edge into the shared node must survive the node cap");

        // Negative control: the diamond is acyclic. The second edge into the shared
        // node (Right -> Bottom) must NOT read as a back-edge — recording the edge
        // before the ancestor check used to make every DAG join a false cycle.
        var cycles = data["cyclesDetected"] as JArray;
        cycles.Should().NotBeNull("the response must always carry a cyclesDetected array");
        cycles!.Should().BeEmpty(
            "no path leads from Bottom back to any ancestor, so there is no cycle");
    }

    [Fact]
    public async Task GetCallGraph_CallersDirection_OnChainC_FindsChainBAndA()
    {
        var (file, line, col) = await LocateAsync("ChainC");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "callers", maxDepth: 5, maxNodes: 50);
        AssertSuccess(result);

        var data = GetData(result);
        data["direction"].Should().NotBeNull();
        data["direction"]!.Value<string>().Should().Be("callers",
            "direction must round-trip");
        var nodes = data["nodes"] as JArray;
        nodes.Should().NotBeNullOrEmpty();
        var names = nodes!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"), "ChainC is called by ChainB");
        names.Should().Contain(n => n.EndsWith(".ChainA()"), "ChainA transitively calls ChainC via ChainB");
    }

    [Fact]
    public async Task GetCallGraph_BothDirection_OnChainB_IncludesCallerAAndCalleeC()
    {
        // ChainA → ChainB → ChainC. With direction=both starting at ChainB,
        // the graph must reach ChainA (caller) AND ChainC (callee).
        var (file, line, col) = await LocateAsync("ChainB");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "both", maxDepth: 2, maxNodes: 50);
        AssertSuccess(result);

        var data = GetData(result);
        data["direction"].Should().NotBeNull();
        data["direction"]!.Value<string>().Should().Be("both");
        var names = (data["nodes"] as JArray)!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"), "the root must always be present");
        names.Should().Contain(n => n.EndsWith(".ChainA()"),
            "direction=both must include the caller of ChainB");
        names.Should().Contain(n => n.EndsWith(".ChainC()"),
            "direction=both must include the callee of ChainB");

        // No edge may appear twice: the ChainB→ChainC edge is reached by both the callee
        // walk (from ChainB) and the caller walk (from ChainC), so without dedup it was
        // emitted twice.
        var edgePairs = (data["edges"] as JArray)!
            .Select(e => (from: e["from"]!.Value<int>(), to: e["to"]!.Value<int>()))
            .ToList();
        edgePairs.Should().OnlyHaveUniqueItems("direction=both must not duplicate an edge reached from both walks");
    }

    [Fact]
    public async Task GetCallGraph_RejectsZeroMaxDepth_HintMentionsValidRange()
    {
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, maxDepth: 0);
        AssertError(result, ErrorCodes.InvalidParameter);
        // The message carries the valid range (the hint explains why the bound exists).
        // In-process JObject.FromObject keeps RoslynError's PascalCase Message; the old
        // lowercase `["message"]` read was always null and silently passed.
        var json = JObject.FromObject(result);
        json["error"].Should().NotBeNull();
        json["error"]!["Message"].Should().NotBeNull();
        json["error"]!["Message"]!.Value<string>().Should().Contain("between 1 and 10",
            "the error message must spell out the valid maxDepth range");
    }

    [Fact]
    public async Task GetCallGraph_RejectsMaxDepthAbove10_HintMentionsValidRange()
    {
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, maxDepth: 100);
        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"].Should().NotBeNull();
        json["error"]!["Message"].Should().NotBeNull();
        json["error"]!["Message"]!.Value<string>().Should().Contain("between 1 and 10");
    }

    [Fact]
    public async Task GetCallGraph_RejectsUnknownDirection_ReturnsInvalidParameter()
    {
        // The impl enforces direction ∈ {callees, callers, both}.
        var (file, line, col) = await LocateAsync("ChainA");
        var result = await Service.GetCallGraphAsync(file, line, col, direction: "sideways");
        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"].Should().NotBeNull();
        json["error"]!["Message"].Should().NotBeNull();
        var messageText = json["error"]!["Message"]!.Value<string>();
        messageText.Should().Contain("callees");
        messageText.Should().Contain("callers");
        messageText.Should().Contain("both");
    }

    [Fact]
    public async Task GetCallGraph_OnNonMethodPosition_ReturnsNotAMethod()
    {
        // Point at the class declaration (not a method) → impl returns NotAMethod.
        var searchResult = await Service.SearchSymbolsAsync("CallGraphFixture", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();
        var loc = symbols![0]["location"]!;

        var result = await Service.GetCallGraphAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertError(result, ErrorCodes.NotAMethod);
    }
}
