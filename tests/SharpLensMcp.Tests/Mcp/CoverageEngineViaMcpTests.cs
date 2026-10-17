using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// find_tests and find_untested_code through the MCP dispatcher against in-memory
// workspaces (each test drives a fresh McpServer, so the shared solution fixture is
// never touched). The fixtures are the ones the unit tests pin, and every value here
// is read from the camelCase wire shape.
public class CoverageEngineViaMcpTests
{
    private static async Task<JObject> CallAsync(Workspace workspace, string toolName, object arguments)
    {
        var server = new McpServer(TimeSpan.FromMinutes(5));
        server.LoadFromWorkspaceForTesting(workspace);
        return await new McpHarnessOps(server).CallToolAsync(toolName, arguments);
    }

    private static JToken SuccessData(JObject inner)
    {
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        return inner["data"]!;
    }

    // Mirrors CoverageRetargetingTests.FindTests_DirectlyCalledLibMember_IsCoveredByItsTest.
    [Fact]
    public async Task FindTests_Retargeted_DirectlyCalledMember_IsCovered()
    {
        using var fixture = new RetargetedSolutionFixture(
            CoverageRetargetingTests.LibSource, CoverageRetargetingTests.TestSource);
        var data = SuccessData(await CallAsync(fixture.Workspace, "find_tests",
            new { typeName = "Lib.Calculator", memberName = "Add" }));

        data["totalTests"]!.Value<int>().Should().Be(3);
        var covering = (JArray)data["coveringTests"]!;
        covering.Select(t => t["testMethod"]!.Value<string>()).Should().Equal("AddTest");
        covering[0]["containingType"]!.Value<string>().Should().Be("Lib.Tests.CalculatorTests");
        covering[0]["testProject"]!.Value<string>().Should().Be(RetargetedSolutionFixture.TestProjectName);
    }

    // Mirrors CoverageRetargetingTests.FindUntestedCode_DirectlyCalledLibMembers_AreNotReportedUntested.
    [Fact]
    public async Task FindUntestedCode_Retargeted_ReportsOnlyTheUnreachedMember()
    {
        using var fixture = new RetargetedSolutionFixture(
            CoverageRetargetingTests.LibSource, CoverageRetargetingTests.TestSource);
        var data = SuccessData(await CallAsync(fixture.Workspace, "find_untested_code",
            new { projectName = RetargetedSolutionFixture.LibProjectName }));

        data["testMethodCount"]!.Value<int>().Should().Be(3);
        ((JArray)data["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>())
            .Should().Equal("Lib.Calculator.Never()");
    }

    // Mirrors CoverageBindOnceTests.FindTests_SharedHelperChain_BindsEachBodyOnce_AndStopsAtTheTarget
    // (the bind counts are a unit-level seam; the wire locks the answer the shared walk gives).
    [Fact]
    public async Task FindTests_SharedHelperChain_ReportsEveryTestReachingTheTarget()
    {
        using var fixture = new RetargetedSolutionFixture(CoverageBindOnceTests.ChainLib, CoverageBindOnceTests.ChainTests);
        var data = SuccessData(await CallAsync(fixture.Workspace, "find_tests",
            new { typeName = "Lib.Chain", memberName = "H3" }));

        data["totalTests"]!.Value<int>().Should().Be(4);
        ((JArray)data["coveringTests"]!).Select(t => t["testMethod"]!.Value<string>())
            .Should().Equal("T1", "T2", "T3");
    }

    // Mirrors CoverageCallEdgeTests.PropertyAndIndexerAccessors_WalkTheAccessorActuallyUsed and
    // PropertyGetter_FindTestsReportsTheReadingTest.
    [Fact]
    public async Task CallEdges_PropertyAccessors_OverTheWire()
    {
        using var fixture = new RetargetedSolutionFixture(
            CoverageCallEdgeTests.PropertyLib, CoverageCallEdgeTests.PropertyTests);

        var untested = SuccessData(await CallAsync(fixture.Workspace, "find_untested_code",
            new { projectName = RetargetedSolutionFixture.LibProjectName, includeProperties = false }));
        ((JArray)untested["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>()).Should().Equal(
            "Lib.P.IndexSet(int, int)", "Lib.P.InitGet()", "Lib.P.SetCore(int)", "Lib.P.SinkGet()");

        var tests = SuccessData(await CallAsync(fixture.Workspace, "find_tests",
            new { typeName = "Lib.P", memberName = "Twice" }));
        ((JArray)tests["coveringTests"]!).Select(t => t["testMethod"]!.Value<string>()).Should().Equal("Uses");
    }

    // Mirrors CoverageEntryPointTests.FindUntestedCode_RecognisesEachFrameworksDiscoveryContract and
    // FindTests_ReportsTheKindOfEachEntryPoint.
    [Fact]
    public async Task EntryPoints_CountsAndKinds_OverTheWire()
    {
        using var fixture = new RetargetedSolutionFixture(
            CoverageEntryPointTests.DiscoveryLib, CoverageEntryPointTests.DiscoveryTests);

        var untested = SuccessData(await CallAsync(fixture.Workspace, "find_untested_code",
            new { projectName = RetargetedSolutionFixture.LibProjectName }));
        untested["testMethodCount"]!.Value<int>().Should().Be(6);
        untested["entryPointCounts"].Should().NotBeNull("the entry-point breakdown must cross the wire");
        untested["entryPointCounts"]!["tests"]!.Value<int>().Should().Be(6);
        untested["entryPointCounts"]!["stepBindings"]!.Value<int>().Should().Be(2);
        untested["entryPointCounts"]!["hooks"]!.Value<int>().Should().Be(2);
        ((JArray)untested["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>())
            .Should().Equal("Lib.Target.Unreached()");

        var step = SuccessData(await CallAsync(fixture.Workspace, "find_tests",
            new { typeName = "Lib.Target", memberName = "ViaReqnrollStep" }));
        var entry = ((JArray)step["coveringTests"]!).Should().ContainSingle().Subject;
        entry["testMethod"]!.Value<string>().Should().Be("WhenItRuns");
        entry["kind"]!.Value<string>().Should().Be("stepBinding");

        var hook = SuccessData(await CallAsync(fixture.Workspace, "find_tests",
            new { typeName = "Lib.Target", memberName = "ViaReqnrollHook" }));
        var hookEntry = ((JArray)hook["coveringTests"]!).Should().ContainSingle().Subject;
        hookEntry["testMethod"]!.Value<string>().Should().Be("BeforeScenario");
        hookEntry["kind"]!.Value<string>().Should().Be("hook");
    }

    // Mirrors CoverageDispatchOnlyTests.FindUntestedCode_LabelsImplementationsAndOverridesReachedOnlyByDispatch
    // and FindTests_ImplementationReachedOnlyByDispatch_ListsDispatchOnlyTests.
    [Fact]
    public async Task DispatchOnly_ReasonAndDispatchOnlyTests_OverTheWire()
    {
        using var fixture = new RetargetedSolutionFixture(CoverageDispatchOnlyTests.Lib, CoverageDispatchOnlyTests.Tests);

        var untested = SuccessData(await CallAsync(fixture.Workspace, "find_untested_code",
            new { projectName = RetargetedSolutionFixture.LibProjectName, includeProperties = false }));
        ((JArray)untested["uncoveredSymbols"]!)
            .Select(s => (s["fullName"]!.Value<string>()!, s["reason"]!.Value<string>()!))
            .Should().Equal(
                ("Lib.Calculator.Triple(int)", "Reached only via interface or virtual dispatch"),
                ("Lib.OtherGreeter.Greet()", "Not reachable from any test method"),
                ("Lib.Shape.Name()", "Not reachable from any test method"),
                ("Lib.Square.Area()", "Reached only via interface or virtual dispatch"),
                ("Lib.Square.Name()", "Not reachable from any test method"));

        var tests = SuccessData(await CallAsync(fixture.Workspace, "find_tests",
            new { typeName = "Lib.Calculator", memberName = "Triple" }));
        ((JArray)tests["coveringTests"]!).Should().BeEmpty();
        var dispatchOnly = (JArray)tests["dispatchOnlyTests"]!;
        dispatchOnly.Select(t => t["testMethod"]!.Value<string>()).Should().Equal("ViaInterface", "ViaFake");
        dispatchOnly[0]["kind"]!.Value<string>().Should().Be("test");
        dispatchOnly[0]["containingType"]!.Value<string>().Should().Be("Lib.Tests.DispatchTests");
    }

    [Fact]
    public async Task FindUntestedCode_UnknownProject_ReturnsInvalidParameterOverTheWire()
    {
        using var fixture = new RetargetedSolutionFixture(
            CoverageRetargetingTests.LibSource, CoverageRetargetingTests.TestSource);
        var inner = await CallAsync(fixture.Workspace, "find_untested_code", new { projectName = "NoSuchProject" });

        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        inner["error"]!["message"]!.Value<string>().Should().Be("Project not found: NoSuchProject");
    }
}
