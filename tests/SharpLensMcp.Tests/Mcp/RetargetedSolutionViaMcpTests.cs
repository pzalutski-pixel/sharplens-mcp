using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// The cross-compilation identity of get_call_graph, get_outgoing_calls,
// find_god_objects and check_architecture through the JSON-RPC dispatcher, on a
// solution whose test project sees the library through a retargeting assembly.
// Each call runs on a fresh McpServer loaded with the in-memory fixture, so the
// wire shape (camelCase over System.Text.Json) is what is asserted. retarget: false
// is the control shape; both shapes must produce the identical result.
public class RetargetedSolutionViaMcpTests
{
    private static McpHarnessOps Load(RetargetedSolutionFixture fixture)
    {
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(fixture.Workspace);
        return new McpHarnessOps(server);
    }

    private static async Task<JToken> CallAndGetDataAsync(McpHarnessOps ops, string tool, object arguments)
    {
        var inner = await ops.CallToolAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeTrue($"{tool} must succeed; got: {inner}");
        return inner["data"]!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetCallGraph_Callees_HasOneNodePerLibMethod(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);
        var (line, column) = RetargetedSources.Locate(RetargetedSources.Tests, "Call()");

        var data = await CallAndGetDataAsync(Load(fixture), "get_call_graph", new
        {
            filePath = fixture.TestFilePath, line, column, direction = "callees", maxDepth = 3, maxNodes = 100
        });

        var nodeArray = (JArray)data["nodes"]!;
        var nameById = nodeArray.ToDictionary(n => n["id"]!.Value<int>(), n => n["fullName"]!.Value<string>()!);
        nodeArray.Select(n => (n["fullName"]!.Value<string>()!, n["depth"]!.Value<int>()))
            .Should().BeEquivalentTo(new[]
            {
                ("Lib.Tests.Uses.Call()", 0),
                ("Lib.Calculator.Add(int, int)", 1),
                ("Lib.Calculator.Calculator()", 1),
                ("Lib.Calculator.Helper(int)", 1),
                ("object.Object()", 2)
            }, "new Lib.Calculator() calls Calculator's synthesized constructor, which calls object's");
        ((JArray)data["edges"]!)
            .Select(e => (nameById[e["from"]!.Value<int>()], nameById[e["to"]!.Value<int>()]))
            .Should().BeEquivalentTo(new[]
            {
                ("Lib.Tests.Uses.Call()", "Lib.Calculator.Calculator()"),
                ("Lib.Tests.Uses.Call()", "Lib.Calculator.Add(int, int)"),
                ("Lib.Tests.Uses.Call()", "Lib.Calculator.Helper(int)"),
                ("Lib.Calculator.Add(int, int)", "Lib.Calculator.Helper(int)"),
                ("Lib.Calculator.Calculator()", "object.Object()")
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetOutgoingCalls_Depth2_ListsEachLibMethodOnce(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);
        var (line, column) = RetargetedSources.Locate(RetargetedSources.Tests, "Call()");

        var data = await CallAndGetDataAsync(Load(fixture), "get_outgoing_calls", new
        {
            filePath = fixture.TestFilePath, line, column, maxDepth = 2
        });

        ((JArray)data["calls"]!)
            .Select(c => (c["method"]!.Value<string>()!, c["depth"]!.Value<int>()))
            .Should().Equal(
                ("Lib.Calculator.Calculator()", 1),
                ("Lib.Calculator.Add(int, int)", 1),
                ("Lib.Calculator.Helper(int)", 1),
                ("object.Object()", 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindGodObjects_CountsAfferentCouplingFromTheTestProject(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests, retarget);

        var data = await CallAndGetDataAsync(Load(fixture), "find_god_objects", new
        {
            minEfferentCoupling = 0, minMembers = 0, maxResults = 50
        });

        var calculator = ((JArray)data["candidates"]!)
            .Single(c => c["typeName"]!.Value<string>() == "Lib.Calculator");
        calculator["afferentCoupling"]!.Value<int>().Should().Be(2,
            "Uses and CalculatorExtensions in Lib.Tests both depend on Lib.Calculator");
        calculator["efferentCoupling"]!.Value<int>().Should().Be(0);
        calculator["memberCount"]!.Value<int>().Should().Be(2);
        calculator["score"]!.Value<double>().Should().Be(1.2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckArchitecture_ProjectLevel_ResolvesCrossProjectTargetsToTheirProjectName(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(
            RetargetedSources.Lib, RetargetedSources.Tests, retarget, libAssemblyName: "Contoso.Lib");

        var data = await CallAndGetDataAsync(Load(fixture), "check_architecture", new
        {
            rules = new[]
            {
                new { from = "Lib.Tests", deny = "Lib" },
                new { from = "Lib.Tests", deny = "Contoso.Lib" }
            },
            level = "project"
        });

        data["violationCount"]!.Value<int>().Should().Be(3);
        ((JArray)data["violations"]!)
            .Select(v => (
                v["fromType"]!.Value<string>()!,
                v["toType"]!.Value<string>()!,
                v["rule"]!["deny"]!.Value<string>()!))
            .Should().BeEquivalentTo(new[]
            {
                ("Lib.Tests.Square", "Lib.ShapeBase", "Lib"),
                ("Lib.Tests.CalculatorExtensions", "Lib.Calculator", "Lib"),
                ("Lib.Tests.Uses", "Lib.Calculator", "Lib")
            });
    }
}
