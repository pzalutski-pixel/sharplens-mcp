using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_call_graph and get_outgoing_calls over a solution whose test project sees the
// library through a retargeting assembly. Lib.Calculator.Helper is bound once inside
// Lib.Tests (Uses.Call calls it) and once inside Lib (Add calls it): two symbol
// instances of one declaration. Each must be ONE node / ONE call. retarget: false is
// the control shape, where the instances are shared; both shapes must produce the
// identical result.
public class RetargetedCallGraphTests
{
    private static RoslynService Load(RetargetedSolutionFixture fixture)
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);
        return service;
    }

    private static JToken SuccessData(object result)
    {
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("the response envelope must carry success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }

    // Nodes as (fullName, depth) and edges as (fromName, toName): the graph's content
    // independent of the order SymbolFinder happens to return callers in.
    private static (List<(string name, int depth)> nodes, List<(string from, string to)> edges) ReadGraph(JToken data)
    {
        var nodeArray = (JArray)data["nodes"]!;
        var nameById = nodeArray.ToDictionary(n => n["id"]!.Value<int>(), n => n["fullName"]!.Value<string>()!);
        var nodes = nodeArray.Select(n => (n["fullName"]!.Value<string>()!, n["depth"]!.Value<int>())).ToList();
        var edges = ((JArray)data["edges"]!)
            .Select(e => (nameById[e["from"]!.Value<int>()], nameById[e["to"]!.Value<int>()]))
            .ToList();
        return (nodes, edges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallGraphCallees_FromTestMethod_HasOneNodePerLibMethod(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);
        var (line, column) = RetargetedSources.Locate(RetargetedSources.Tests, "Call()");

        var data = SuccessData(await Load(fixture).GetCallGraphAsync(
            fixture.TestFilePath, line, column, direction: "callees", maxDepth: 3, maxNodes: 100));

        // new Lib.Calculator() calls Calculator's compiler-synthesized constructor, which
        // calls object's.
        var (nodes, edges) = ReadGraph(data);
        nodes.Should().BeEquivalentTo(new[]
        {
            ("Lib.Tests.Uses.Call()", 0),
            ("Lib.Calculator.Add(int, int)", 1),
            ("Lib.Calculator.Calculator()", 1),
            ("Lib.Calculator.Helper(int)", 1),
            ("object.Object()", 2)
        }, "Helper reached from Call's body (Lib.Tests compilation) and from Add's body (Lib compilation) is one method");
        edges.Should().BeEquivalentTo(new[]
        {
            ("Lib.Tests.Uses.Call()", "Lib.Calculator.Calculator()"),
            ("Lib.Tests.Uses.Call()", "Lib.Calculator.Add(int, int)"),
            ("Lib.Tests.Uses.Call()", "Lib.Calculator.Helper(int)"),
            ("Lib.Calculator.Add(int, int)", "Lib.Calculator.Helper(int)"),
            ("Lib.Calculator.Calculator()", "object.Object()")
        });
        ((JArray)data["cyclesDetected"]!).Should().BeEmpty();
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse();
        data["truncatedByNodes"]!.Value<bool>().Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallGraphBoth_FromLibMethod_HasOneNodePerMethod(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);
        var (line, column) = RetargetedSources.Locate(RetargetedSources.Lib, "Add(int a");

        var data = SuccessData(await Load(fixture).GetCallGraphAsync(
            fixture.LibFilePath, line, column, direction: "both", maxDepth: 3, maxNodes: 100));

        // The callers walk reaches Call and Twice in Lib.Tests, binding Add in Lib.Tests'
        // compilation: the root, not a new node. "both" is the callers tree united with the
        // callees tree, so Call's own call to Helper is not part of the graph.
        var (nodes, edges) = ReadGraph(data);
        nodes.Should().BeEquivalentTo(new[]
        {
            ("Lib.Calculator.Add(int, int)", 0),
            ("Lib.Calculator.Helper(int)", 1),
            ("Lib.Tests.CalculatorExtensions.Twice(Lib.Calculator, int)", 1),
            ("Lib.Tests.Uses.Call()", 1)
        });
        edges.Should().BeEquivalentTo(new[]
        {
            ("Lib.Calculator.Add(int, int)", "Lib.Calculator.Helper(int)"),
            ("Lib.Tests.CalculatorExtensions.Twice(Lib.Calculator, int)", "Lib.Calculator.Add(int, int)"),
            ("Lib.Tests.Uses.Call()", "Lib.Calculator.Add(int, int)")
        });
        ((JArray)data["cyclesDetected"]!).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OutgoingCallsDepth2_FromTestMethod_ListsEachLibMethodOnce(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);
        var (line, column) = RetargetedSources.Locate(RetargetedSources.Tests, "Call()");

        var data = SuccessData(await Load(fixture).GetOutgoingCallsAsync(
            fixture.TestFilePath, line, column, maxDepth: 2));

        // Hop 2 walks Add's body in Lib and binds Helper there; Helper is already the
        // hop-1 call, so it is not listed a second time. Calculator's synthesized
        // constructor calls object's at hop 2.
        var calls = ((JArray)data["calls"]!)
            .Select(c => (c["method"]!.Value<string>()!, c["depth"]!.Value<int>()))
            .ToList();
        calls.Should().Equal(
            ("Lib.Calculator.Calculator()", 1),
            ("Lib.Calculator.Add(int, int)", 1),
            ("Lib.Calculator.Helper(int)", 1),
            ("object.Object()", 2));
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse();
    }
}
