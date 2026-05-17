using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Compound Tools category (7 tools). Compound tools
// aggregate other tools, so the assertions both pin shape AND verify that
// the aggregation surfaces concrete known content.
public class CompoundToolsViaMcpTests : McpTestBase
{
    public CompoundToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetTypeOverview_OnRoslynService_ReturnsClassWithKnownMemberCounts()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_overview", new
        {
            typeName = "RoslynService"
        });
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");

        // memberSummary aggregates counts per member kind. Compound.cs:292 emits
        // `methods` (not `methodCount`); pin the exact field name.
        var memberSummary = data["memberSummary"]!;
        memberSummary["methods"]?.Value<int>().Should().BeGreaterOrEqualTo(20,
            "RoslynService has well over 20 ordinary methods across the partials");
        memberSummary["fields"]?.Value<int>().Should().BeGreaterOrEqualTo(1,
            "RoslynService has fields like _workspace, _solution, etc.");
    }

    [Fact]
    public async Task GetTypeOverview_OnRecordFixture_ReportsRecordKind()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_overview", new
        {
            typeName = "Person"
        });
        data["typeKind"]?.Value<string>().Should().Be("Record",
            "the Person fixture is `public record Person(...)` and must report typeKind=Record");
    }

    [Fact]
    public async Task AnalyzeMethod_OnLoadSolutionAsync_ReturnsExpectedSignatureAndCallers()
    {
        var data = await CallAndGetDataAsync("roslyn:analyze_method", new
        {
            typeName = "RoslynService",
            methodName = "LoadSolutionAsync",
            includeCallers = true,
            includeOutgoingCalls = false
        });
        // signature is a nested object — verified against Compound.cs:376
        var signature = data["signature"]!;
        signature["name"]?.Value<string>().Should().Be("LoadSolutionAsync");
        signature["returnType"]?.Value<string>().Should().Contain("Task<object>",
            "LoadSolutionAsync returns Task<object>");
        // Roslyn's default ToDisplayString omits parameter names, so the rendered signature
        // is "...LoadSolutionAsync(string)" — the parameter NAME is exposed via the
        // parameters[] array (asserted below), the TYPE is in the full signature.
        signature["fullSignature"]?.Value<string>().Should().Contain("LoadSolutionAsync(string)");

        var parameters = signature["parameters"] as JArray;
        parameters.Should().NotBeNullOrEmpty();
        parameters!.First()["name"]?.Value<string>().Should().Be("solutionPath");
        parameters!.First()["type"]?.Value<string>().Should().Be("string");

        // includeCallers:true must populate the callers array. LoadSolutionAsync is
        // dispatched by McpServer.HandleToolCallAsync and called by tests.
        var callers = data["callers"] as JArray;
        callers.Should().NotBeNull();
        data["totalCallers"]?.Value<int>().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task AnalyzeMethod_WithOutgoingCalls_ListsCalls()
    {
        var data = await CallAndGetDataAsync("roslyn:analyze_method", new
        {
            typeName = "RoslynService",
            methodName = "LoadSolutionAsync",
            includeCallers = false,
            includeOutgoingCalls = true
        });
        // LoadSolutionAsync calls MSBuildWorkspace.Create / OpenSolutionAsync /
        // _documentCache.Clear etc. Outgoing calls must be non-empty.
        var outgoing = data["outgoingCalls"] as JArray;
        outgoing.Should().NotBeNullOrEmpty(
            "LoadSolutionAsync makes several method calls inside its body");
    }

    [Fact]
    public async Task GetFileOverview_OnRoslynService_ReportsTypeDeclarationsIncludingRoslynService()
    {
        var data = await CallAndGetDataAsync("roslyn:get_file_overview", new
        {
            filePath = Fixture.RoslynServicePath
        });
        data["filePath"]?.Value<string>().Should().Contain("RoslynService");
        data["lineCount"]?.Value<int>().Should().BeGreaterThan(400,
            "RoslynService.cs has hundreds of lines after the partial-class split");

        var typeDecls = data["typeDeclarations"] as JArray;
        typeDecls.Should().NotBeNullOrEmpty();
        typeDecls!.Any(t => t["name"]?.Value<string>() == "RoslynService")
            .Should().BeTrue("the file declares the RoslynService partial class");
    }

    [Fact]
    public async Task GetMethodSource_ReturnsFullSourceWithSignatureAndBody()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_source", new
        {
            typeName = "RoslynService",
            methodName = "GetHealthCheckAsync"
        });
        var source = data["fullSource"]!.Value<string>()!;
        source.Should().Contain("public async Task<object> GetHealthCheckAsync()",
            "the rendered source must include the exact declaration");
        source.Should().Contain("CreateSuccessResponse",
            "the body uses CreateSuccessResponse after the 1.5.3 health_check shape fix");

        data["lineCount"]?.Value<int>().Should().BeGreaterThan(20,
            "GetHealthCheckAsync spans well over 20 lines");
    }

    [Fact]
    public async Task GetMethodSourceBatch_ReturnsBothSources()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_source_batch", new
        {
            methods = new object[]
            {
                new { typeName = "RoslynService", methodName = "LoadSolutionAsync" },
                new { typeName = "RoslynService", methodName = "GetHealthCheckAsync" }
            }
        });
        data["successCount"]?.Value<int>().Should().Be(2);
        data["errorCount"]?.Value<int>().Should().Be(0);

        var results = data["results"] as JArray;
        results!.Count.Should().Be(2);
        results[0]["data"]?["fullSource"]?.Value<string>().Should().Contain("LoadSolutionAsync");
        results[1]["data"]?["fullSource"]?.Value<string>().Should().Contain("GetHealthCheckAsync");
    }

    [Fact]
    public async Task GetInstantiationOptions_OnRoslynService_ListsParameterlessConstructor()
    {
        var data = await CallAndGetDataAsync("roslyn:get_instantiation_options", new
        {
            typeName = "RoslynService"
        });
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        data["isAbstract"]?.Value<bool>().Should().BeFalse();
        data["implementsIDisposable"]?.Value<bool>().Should().BeFalse(
            "RoslynService does not implement IDisposable");

        var ctors = data["constructors"] as JArray;
        ctors.Should().NotBeNullOrEmpty();
        // RoslynService has a single parameterless public constructor.
        ctors!.Any(c => (c["parameters"] as JArray)?.Count == 0)
            .Should().BeTrue("RoslynService exposes a parameterless ctor");
    }

    [Fact]
    public async Task GetProjectHealth_OnSharpLensMcp_ReportsCleanBuildAcrossAllSections()
    {
        var data = await CallAndGetDataAsync("roslyn:get_project_health", new
        {
            projectName = "SharpLensMcp",
            includeAnalyzers = false,
            topN = 3
        });
        data["projectName"]?.Value<string>().Should().Be("SharpLensMcp");

        // All four aggregate sections must be present and have expected substructure.
        var diag = data["diagnostics"]!;
        diag["errorCount"]?.Value<int>().Should().Be(0,
            "the codebase compiles clean (asserted independently by Phase 5.1 build gate)");
        diag["warningCount"]?.Value<int>().Should().Be(0,
            "the codebase has no compiler warnings");

        data["unusedCode"]!["count"].Should().NotBeNull();
        data["coupling"]!["godObjectCandidates"].Should().NotBeNull();
        data["coverage"]!["uncoveredPublicSurface"].Should().NotBeNull();

        // Summary string must contain all four dimensions.
        var summary = data["summary"]!.Value<string>()!;
        summary.Should().Contain("error");
        summary.Should().Contain("warning");
        summary.Should().Contain("god-object");
        summary.Should().Contain("uncovered");
        summary.Should().Contain("unused");
    }

    [Fact]
    public async Task GetProjectHealth_UnknownProject_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:get_project_health",
            new { projectName = "DoesNotExist_12345" },
            codeContains: ErrorCodes.InvalidParameter);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.InvalidParameter,
            "unknown-project must use the standard INVALID_PARAMETER code");
    }
}
