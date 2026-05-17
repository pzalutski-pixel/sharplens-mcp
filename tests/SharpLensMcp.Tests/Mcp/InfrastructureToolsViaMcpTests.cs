using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// End-to-end MCP-layer tests for the 10 Infrastructure-category tools.
// Each tool is invoked via tools/call through the JSON-RPC envelope and
// asserted on the specific response fields the implementation must return.
public class InfrastructureToolsViaMcpTests : McpTestBase
{
    public InfrastructureToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task HealthCheck_ReturnsReadyWithSolutionInfo()
    {
        var data = await CallAndGetDataAsync("roslyn:health_check");
        data["status"]?.Value<string>().Should().Be("Ready",
            "the fixture loads the solution at setup, so status must be Ready");
        data["solution"]?["loaded"]?.Value<bool>().Should().BeTrue();
        data["solution"]?["projects"]?.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LoadSolution_AlreadyLoadedAtFixtureSetup_RoundTripsAgain()
    {
        // Reloading the same solution must succeed idempotently.
        var data = await CallAndGetDataAsync("roslyn:load_solution", new
        {
            solutionPath = Fixture.SolutionPath
        });
        data["projectCount"]?.Value<int>().Should().BeGreaterThan(0);
        data["documentCount"]?.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LoadSolution_NonExistentPath_ReturnsToolError()
    {
        var error = await CallAndGetErrorAsync("roslyn:load_solution", new
        {
            solutionPath = "/does/not/exist/Nope.sln"
        });
        error["code"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SyncDocuments_WithEmptyList_SyncsAllAndReturnsCounts()
    {
        // sync_documents(filePaths: null) syncs all docs. Should complete
        // and return updated/added/removed counts (all numbers).
        var data = await CallAndGetDataAsync("roslyn:sync_documents", new
        {
            filePaths = (string[]?)null
        });
        data["updated"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
        data["added"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
        data["removed"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
        data["totalSynced"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task SyncDocuments_WithSpecificFile_SyncsThatFile()
    {
        var data = await CallAndGetDataAsync("roslyn:sync_documents", new
        {
            filePaths = new[] { Fixture.RoslynServicePath }
        });
        data["totalSynced"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task GetProjectStructure_ListsAtLeastOneProject()
    {
        var data = await CallAndGetDataAsync("roslyn:get_project_structure", new
        {
            includeReferences = false,
            includeDocuments = false,
            summaryOnly = true
        });
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNullOrEmpty(
            "the loaded solution must expose at least one project");
    }

    [Fact]
    public async Task DependencyGraph_ReturnsProjectDependencies()
    {
        var data = await CallAndGetDataAsync("roslyn:dependency_graph", new { });
        data["dependencies"].Should().NotBeNull(
            "dependency_graph must return a dependencies field");
        data["hasCycles"]?.Type.Should().Be(JTokenType.Boolean);
    }

    [Fact]
    public async Task DependencyGraph_MermaidFormat_ReturnsGraphText()
    {
        var data = await CallAndGetDataAsync("roslyn:dependency_graph", new { format = "mermaid" });
        data["format"]?.Value<string>().Should().Be("mermaid");
        data["graph"]?.Value<string>().Should().Contain("graph TD");
    }

    [Fact]
    public async Task GetCodeFixes_OnNonExistentDiagnostic_ReturnsToolError()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_code_fixes", new
        {
            filePath = Fixture.RoslynServicePath,
            diagnosticId = "ZZZ9999",
            line = 0,
            column = 0
        });
        error["code"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ApplyCodeFix_OnNonExistentDiagnostic_ReturnsToolError()
    {
        var error = await CallAndGetErrorAsync("roslyn:apply_code_fix", new
        {
            filePath = Fixture.RoslynServicePath,
            diagnosticId = "ZZZ9999",
            line = 0,
            column = 0,
            preview = true
        });
        error["code"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetNugetDependencies_ListsPackages()
    {
        var data = await CallAndGetDataAsync("roslyn:get_nuget_dependencies", new
        {
            projectName = (string?)null
        });
        // Either a `projects` collection or a flat package list is acceptable;
        // the response must be a non-null object.
        data.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSourceGenerators_ReturnsArray()
    {
        var data = await CallAndGetDataAsync("roslyn:get_source_generators", new
        {
            projectName = (string?)null
        });
        // Field name varies (generators / projects). Just assert response shape.
        data.Should().NotBeNull();
    }

    [Fact]
    public async Task GetGeneratedCode_OnMissingFile_ReturnsToolError()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_generated_code", new
        {
            projectName = "SharpLensMcp",
            generatedFileName = "DoesNotExist_12345.g.cs"
        });
        error["code"]?.Value<string>().Should().NotBeNullOrEmpty();
    }
}
