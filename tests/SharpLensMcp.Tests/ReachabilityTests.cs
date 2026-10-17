using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_path_between over a deterministic in-memory graph:
//   Chain.A -> Chain.B -> Chain.C   (B is the sole bridge A..C)
//   Chain.Dispatcher -> IWorker.Run -> (dispatch) Worker.Run -> Worker.Sink
//   Chain.Island                    (reached by nothing in-graph)
// Uses the AdhocWorkspace seam so the graph is self-contained and the paths are exact.
public class ReachabilityTests
{
    private const string ReachabilityCode = @"
namespace Fixtures;

public interface IWorker { void Run(); }

public sealed class Worker : IWorker
{
    public void Run() { Sink(); }
    public void Sink() { }
}

public class Chain
{
    public void A() { B(); }
    public void B() { C(); }
    public void C() { }
    public void Dispatcher(IWorker w) { w.Run(); }
    public void Island() { }
}
";

    private static (RoslynService service, string path) Load()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(ReachabilityCode, fileName: "Reachability.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, document.FilePath!);
    }

    private static List<MemberRef> Refs(params (string type, string member)[] items) =>
        items.Select(i => new MemberRef(i.type, i.member)).ToList();

    private static async Task<JObject> RunAsync(
        (string type, string member)[] sources,
        (string type, string member)[] targets,
        (string type, string member)[]? avoid = null,
        (string type, string member)[]? must = null,
        int maxHops = 8)
    {
        var (service, _) = Load();
        var result = await service.FindPathBetweenAsync(
            Refs(sources), Refs(targets),
            avoid == null ? null : Refs(avoid),
            must == null ? null : Refs(must),
            maxHops);
        return JObject.FromObject(result);
    }

    private static JObject FirstPair(JObject json) => (JObject)((JArray)json["data"]!["pairs"]!)[0];

    [Fact]
    public async Task DirectChain_AToC_ReturnsOrderedPath()
    {
        var json = await RunAsync(new[] { ("Chain", "A") }, new[] { ("Chain", "C") });
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var pair = FirstPair(json);
        pair["reachable"]!.Value<bool>().Should().BeTrue();
        pair["hopCount"]!.Value<int>().Should().Be(2, "A -> B -> C is two edges, the unit maxHops bounds");

        var hops = ((JArray)pair["hops"]!).Select(h => h["symbol"]!.Value<string>()!).ToList();
        hops.Should().HaveCount(3);
        hops[0].Should().EndWith("Chain.A()");
        hops[1].Should().EndWith("Chain.B()");
        hops[2].Should().EndWith("Chain.C()");
    }

    [Fact]
    public async Task NoPath_CToA_ReportsDisconnectedNotHopLimited()
    {
        var json = await RunAsync(new[] { ("Chain", "C") }, new[] { ("Chain", "A") });
        var pair = FirstPair(json);
        pair["reachable"]!.Value<bool>().Should().BeFalse("C calls nothing that leads to A");
        pair["hitHopLimit"]!.Value<bool>().Should().BeFalse(
            "the frontiers exhausted — genuinely disconnected, not a budget cutoff");
    }

    [Fact]
    public async Task HopLimitTooLow_ReportsHitHopLimit()
    {
        var json = await RunAsync(new[] { ("Chain", "A") }, new[] { ("Chain", "C") }, maxHops: 1);
        var pair = FirstPair(json);
        pair["reachable"]!.Value<bool>().Should().BeFalse("A -> B -> C needs two hops");
        pair["hitHopLimit"]!.Value<bool>().Should().BeTrue(
            "there was more to explore — the caller should raise maxHops");
    }

    [Fact]
    public async Task Barrier_AvoidThroughSoleBridge_CutsThePath()
    {
        var json = await RunAsync(
            new[] { ("Chain", "A") }, new[] { ("Chain", "C") },
            avoid: new[] { ("Chain", "B") });
        var pair = FirstPair(json);
        pair["reachable"]!.Value<bool>().Should().BeFalse(
            "B is the only bridge from A to C; barring it disconnects them");
    }

    [Fact]
    public async Task VirtualDispatch_ReachesConcreteThroughInterface()
    {
        // Dispatcher calls IWorker.Run; only dispatch expansion connects it to Worker.Run.
        var json = await RunAsync(new[] { ("Chain", "Dispatcher") }, new[] { ("Worker", "Run") });
        var pair = FirstPair(json);
        pair["reachable"]!.Value<bool>().Should().BeTrue(
            "the call through IWorker.Run must dispatch to the concrete Worker.Run");

        var hops = ((JArray)pair["hops"]!).Select(h => h["symbol"]!.Value<string>()!).ToList();
        hops.First().Should().EndWith("Chain.Dispatcher(Fixtures.IWorker)");
        hops.Last().Should().EndWith("Worker.Run()");
    }

    [Fact]
    public async Task MultipleTargets_ReportsEachPairIndependently()
    {
        var json = await RunAsync(
            new[] { ("Chain", "A") },
            new[] { ("Chain", "C"), ("Chain", "Island") });

        json["data"]!["pairCount"]!.Value<int>().Should().Be(2);
        json["data"]!["reachableCount"]!.Value<int>().Should().Be(1, "C is reachable, Island is not");

        var pairs = (JArray)json["data"]!["pairs"]!;
        pairs.Single(p => p["target"]!.Value<string>() == "Chain.C")["reachable"]!.Value<bool>().Should().BeTrue();
        pairs.Single(p => p["target"]!.Value<string>() == "Chain.Island")["reachable"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task MustPassThrough_OnTheBridge_StaysReachable()
    {
        var json = await RunAsync(
            new[] { ("Chain", "A") }, new[] { ("Chain", "C") },
            must: new[] { ("Chain", "B") });
        FirstPair(json)["reachable"]!.Value<bool>().Should().BeTrue(
            "the A -> B -> C path already crosses B");
    }

    [Fact]
    public async Task MustPassThrough_OffPath_IsUnreachable()
    {
        var json = await RunAsync(
            new[] { ("Chain", "A") }, new[] { ("Chain", "C") },
            must: new[] { ("Chain", "Island") });
        FirstPair(json)["reachable"]!.Value<bool>().Should().BeFalse(
            "A does not reach Island, so no path through Island exists");
    }

    [Fact]
    public async Task UnknownType_ReturnsTypeNotFound()
    {
        var json = await RunAsync(new[] { ("NoSuchType12345", "A") }, new[] { ("Chain", "C") });
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task UnknownMember_ReturnsSymbolNotFound()
    {
        var json = await RunAsync(new[] { ("Chain", "NoSuchMethod12345") }, new[] { ("Chain", "C") });
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task MaxHopsOutOfRange_ReturnsInvalidParameter()
    {
        var json = await RunAsync(new[] { ("Chain", "A") }, new[] { ("Chain", "C") }, maxHops: 0);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task MustPassThroughMultiple_ReturnsInvalidParameter()
    {
        var json = await RunAsync(
            new[] { ("Chain", "A") }, new[] { ("Chain", "C") },
            must: new[] { ("Chain", "B"), ("Chain", "C") });
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Contain("single checkpoint");
    }

    [Fact]
    public async Task EmptySources_ReturnsInvalidParameter()
    {
        var (service, _) = Load();
        var result = await service.FindPathBetweenAsync(
            new List<MemberRef>(), Refs(("Chain", "C")), null, null);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }
}
