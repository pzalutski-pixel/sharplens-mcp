using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Compound Tools category (7 tools).
public class CompoundToolsViaMcpTests : McpTestBase
{
    public CompoundToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetTypeOverview_OnRoslynService_ReturnsExpectedFields()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_overview", new
        {
            typeName = "RoslynService"
        });
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        data["memberSummary"].Should().NotBeNull();
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
    public async Task AnalyzeMethod_ReturnsSignatureAndCallers()
    {
        var data = await CallAndGetDataAsync("roslyn:analyze_method", new
        {
            typeName = "RoslynService",
            methodName = "LoadSolutionAsync",
            includeCallers = true,
            includeOutgoingCalls = false
        });
        data["methodName"]?.Value<string>().Should().Be("LoadSolutionAsync");
        data["signature"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetFileOverview_OnRoslynService_ReturnsLineCountAndTypes()
    {
        var data = await CallAndGetDataAsync("roslyn:get_file_overview", new
        {
            filePath = Fixture.RoslynServicePath
        });
        data["filePath"]?.Value<string>().Should().Contain("RoslynService");
        data["lineCount"]?.Value<int>().Should().BeGreaterThan(100);
        data["typeDeclarations"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetMethodSource_ReturnsSourceContainingMethodName()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_source", new
        {
            typeName = "RoslynService",
            methodName = "GetHealthCheckAsync"
        });
        data["fullSource"]?.Value<string>().Should().Contain("GetHealthCheckAsync");
    }

    [Fact]
    public async Task GetMethodSourceBatch_ReturnsAllRequestedMethods()
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
    }

    [Fact]
    public async Task GetInstantiationOptions_OnRoslynService_ListsConstructors()
    {
        var data = await CallAndGetDataAsync("roslyn:get_instantiation_options", new
        {
            typeName = "RoslynService"
        });
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["constructors"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetProjectHealth_OnSharpLensMcp_ReturnsAllFourSections()
    {
        var data = await CallAndGetDataAsync("roslyn:get_project_health", new
        {
            projectName = "SharpLensMcp",
            includeAnalyzers = false,
            topN = 3
        });
        data["projectName"]?.Value<string>().Should().Be("SharpLensMcp");
        data["diagnostics"].Should().NotBeNull("composite must include diagnostics section");
        data["unusedCode"].Should().NotBeNull("composite must include unusedCode section");
        data["coupling"].Should().NotBeNull("composite must include coupling section");
        data["coverage"].Should().NotBeNull("composite must include coverage section");
        data["summary"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetProjectHealth_UnknownProject_ReturnsToolError()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_project_health", new
        {
            projectName = "DoesNotExist_12345"
        });
        error["code"]?.Value<string>().Should().NotBeNullOrEmpty();
    }
}
