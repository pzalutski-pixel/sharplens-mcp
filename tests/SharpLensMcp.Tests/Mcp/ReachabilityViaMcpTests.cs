using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// find_path_between through the MCP dispatcher, over the loaded SharpLensMcp solution
// (src + Tests projects). Exact path values are locked in ReachabilityTests on a
// deterministic fixture; these guard the wire path and real cross-project behavior.
public class ReachabilityViaMcpTests : McpTestBase
{
    public ReachabilityViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    private static object Endpoint(string typeName, string memberName) => new { typeName, memberName };

    [Fact]
    public async Task FindPathBetween_SameProject_HealthCheckReachesProjectCompilation()
    {
        // GetHealthCheckAsync's body calls GetProjectCompilationAsync (both in src).
        var data = await CallAndGetDataAsync("find_path_between", new
        {
            sources = new[] { Endpoint("RoslynService", "GetHealthCheckAsync") },
            targets = new[] { Endpoint("RoslynService", "GetProjectCompilationAsync") }
        });

        data["reachableCount"]!.Value<int>().Should().Be(1);
        var pair = (JObject)((JArray)data["pairs"]!)[0];
        pair["reachable"]!.Value<bool>().Should().BeTrue();
        ((JArray)pair["hops"]!).Count.Should().BeGreaterThan(1, "a reachable path lists at least source and target");
        pair["hops"]!.Last()!["symbol"]!.Value<string>().Should().Contain("GetProjectCompilationAsync");
    }

    [Fact]
    public async Task FindPathBetween_CrossProject_TestBaseReachesLoadSolution()
    {
        // RoslynServiceTestBase.InitializeAsync (Tests project) awaits Service.LoadSolutionAsync
        // (src project) — a genuine cross-project reach through the dispatcher.
        var data = await CallAndGetDataAsync("find_path_between", new
        {
            sources = new[] { Endpoint("RoslynServiceTestBase", "InitializeAsync") },
            targets = new[] { Endpoint("RoslynService", "LoadSolutionAsync") }
        });

        var pair = (JObject)((JArray)data["pairs"]!)[0];
        pair["reachable"]!.Value<bool>().Should().BeTrue(
            "InitializeAsync calls LoadSolutionAsync across the Tests -> src boundary");
        pair["hops"]!.First()!["symbol"]!.Value<string>().Should().Contain("InitializeAsync");
        pair["hops"]!.Last()!["symbol"]!.Value<string>().Should().Contain("LoadSolutionAsync");
    }

    [Fact]
    public async Task FindPathBetween_SrcCannotReachTestsMethod_Unreachable()
    {
        // The src project does not reference Tests, so no path can exist in that direction.
        var data = await CallAndGetDataAsync("find_path_between", new
        {
            sources = new[] { Endpoint("RoslynService", "GetProjectCompilationAsync") },
            targets = new[] { Endpoint("RoslynServiceTestBase", "InitializeAsync") },
            maxHops = 4
        });

        var pair = (JObject)((JArray)data["pairs"]!)[0];
        pair["reachable"]!.Value<bool>().Should().BeFalse(
            "src has no project reference to Tests, so it cannot reach a Tests method");
    }

    [Fact]
    public async Task FindPathBetween_MultipleTargets_ReportsEachPair()
    {
        var data = await CallAndGetDataAsync("find_path_between", new
        {
            sources = new[] { Endpoint("RoslynService", "GetHealthCheckAsync") },
            targets = new[]
            {
                Endpoint("RoslynService", "GetProjectCompilationAsync"),
                Endpoint("RoslynServiceTestBase", "InitializeAsync")
            },
            maxHops = 4
        });

        data["pairCount"]!.Value<int>().Should().Be(2);
        var pairs = (JArray)data["pairs"]!;
        pairs.Single(p => p["target"]!.Value<string>() == "RoslynService.GetProjectCompilationAsync")["reachable"]!
            .Value<bool>().Should().BeTrue();
        pairs.Single(p => p["target"]!.Value<string>() == "RoslynServiceTestBase.InitializeAsync")["reachable"]!
            .Value<bool>().Should().BeFalse("a src method cannot reach a Tests method");
    }

    [Fact]
    public async Task FindPathBetween_UnknownType_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync("find_path_between", new
        {
            sources = new[] { Endpoint("NoSuchType_98765", "Foo") },
            targets = new[] { Endpoint("RoslynService", "LoadSolutionAsync") }
        }, codeContains: ErrorCodes.TypeNotFound);
        error["message"]!.Value<string>()!.Should().Contain("NoSuchType_98765");
    }
}
