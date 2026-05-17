using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for infrastructure tools: health_check, load_solution, get_project_structure
/// </summary>
public class InfrastructureTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetHealthCheck_ReturnsSolutionInfo()
    {
        var result = await Service.GetHealthCheckAsync();

        AssertSuccess(result);
        var data = GetData(result);
        data["status"]?.Value<string>().Should().Be("Ready");
        data["solution"]?["loaded"]?.Value<bool>().Should().BeTrue();
        data["solution"]?["path"]?.Value<string>().Should().EndWith("SharpLensMcp.sln");
    }

    [Fact]
    public async Task LoadSolution_WithValidPath_Succeeds()
    {
        // Already loaded in InitializeAsync, but we can verify the result
        var result = await Service.LoadSolutionAsync(SolutionPath);

        AssertSuccess(result);
        var data = GetData(result);
        // The SharpLensMcp solution contains exactly three csproj projects:
        // SharpLensMcp, SharpLensMcp.Tests, SharpLensMcp.Tests.TestAnalyzers.
        data["projectCount"]?.Value<int>().Should().Be(3);
    }

    [Fact]
    public async Task LoadSolution_WithInvalidPath_ReturnsError()
    {
        // Arrange
        var invalidPath = @"C:\NonExistent\Fake.sln";

        // Act
        var result = await Service.LoadSolutionAsync(invalidPath);

        // Assert
        AssertError(result, ErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task GetProjectStructure_ReturnsSolutionInfo()
    {
        // Act - use correct signature: includeReferences, includeDocuments
        var result = await Service.GetProjectStructureAsync(
            includeReferences: true,
            includeDocuments: true);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNull();
        projects!.Count.Should().BeGreaterOrEqualTo(1);

        // Check first project has expected properties
        var firstProject = projects[0];
        firstProject["name"]?.Value<string>().Should().Be("SharpLensMcp");
        firstProject["language"]?.Value<string>().Should().Be("C#");
    }

    [Fact]
    public async Task GetProjectStructure_WithSummaryOnly_OmitsDocumentsAndReferenceArrays()
    {
        var result = await Service.GetProjectStructureAsync(
            includeReferences: false,
            includeDocuments: false,
            summaryOnly: true);

        AssertSuccess(result);
        var data = GetData(result);
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNullOrEmpty();
        projects!.Count.Should().Be(3, "the solution has three csproj projects");

        // Summary entries carry name + documentCount + projectReferenceCount + language only,
        // per Analysis.cs:867-873. They must NOT carry the full `documents` or `references` arrays.
        foreach (var project in projects)
        {
            project["name"]?.Value<string>().Should().NotBeNullOrEmpty();
            project["language"]?.Value<string>().Should().Be("C#");
            project["documentCount"]?.Type.Should().Be(JTokenType.Integer);
            project["projectReferenceCount"]?.Type.Should().Be(JTokenType.Integer);
            project["documents"].Should().BeNull("summaryOnly must omit the documents array");
            project["references"].Should().BeNull("summaryOnly must omit the references array");
        }
    }

    [Fact]
    public async Task DependencyGraph_JsonFormat_ListsTestProjectDependingOnSharpLensMcp()
    {
        var result = await Service.GetDependencyGraphAsync(format: "json");

        AssertSuccess(result);
        var data = GetData(result);
        // The default-format response carries a dependencies dictionary, not a graph string.
        var deps = data["dependencies"] as JObject;
        deps.Should().NotBeNull("json format returns a Dictionary<string, List<string>>");
        deps!["SharpLensMcp.Tests"].Should().NotBeNull(
            "the test project must appear as a key in the dependency graph");
        var testDeps = deps["SharpLensMcp.Tests"] as JArray;
        testDeps.Should().NotBeNullOrEmpty();
        testDeps!.Select(t => t.Value<string>())
            .Should().Contain("SharpLensMcp",
                "SharpLensMcp.Tests project-references SharpLensMcp");

        data["hasCycles"]?.Value<bool>().Should().BeFalse(
            "the SharpLensMcp solution has no circular project references");
    }

    [Fact]
    public async Task DependencyGraph_ReturnsMermaidFormat()
    {
        var result = await Service.GetDependencyGraphAsync(format: "mermaid");

        AssertSuccess(result);
        var data = GetData(result);
        // The response field is `graph` (not `mermaid`); previous test asserted on a
        // non-existent field via `?.` short-circuit, so it silently passed forever.
        data["format"]?.Value<string>().Should().Be("mermaid");
        data["graph"]?.Value<string>().Should().Contain("graph TD");
    }

    [Fact]
    public async Task Paths_AreRelativeByDefault()
    {
        // Act - search for a symbol to get file paths
        var result = await Service.SearchSymbolsAsync("RoslynService", kind: "Class", maxResults: 1);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var symbols = data["results"] as JArray;
        symbols.Should().NotBeNull();
        symbols!.Count.Should().BeGreaterThan(0);

        // Path is inside location object
        var location = symbols[0]["location"];
        location.Should().NotBeNull();
        var filePath = location!["filePath"]?.Value<string>();
        filePath.Should().NotBeNull();
        filePath.Should().NotStartWith("C:");
        filePath.Should().NotStartWith("/");
        // Should use forward slashes for cross-platform consistency
        filePath.Should().NotContain("\\");
        // Should be a relative path like "src/RoslynService.cs"
        filePath.Should().StartWith("src/");
    }

    [Fact]
    public async Task SyncDocuments_WithNoFiles_UpdatesAllExistingAddsNoneRemovesNone()
    {
        var result = await Service.SyncDocumentsAsync(null);

        AssertSuccess(result);
        var data = GetData(result);
        // Sync-all walks every document already in the solution; none of them are
        // missing from disk and none are unknown, so:
        //  - added must be 0 (nothing new appears via the existing-document path)
        //  - removed must be 0 (every file still exists on disk)
        //  - updated must be > 0 (every existing doc is rewritten with on-disk text per RoslynService.cs:359-366)
        //  - totalSynced must equal updated + added + removed
        var updated = data["updated"]!.Value<int>();
        var added = data["added"]!.Value<int>();
        var removed = data["removed"]!.Value<int>();
        added.Should().Be(0, "sync-all over an in-sync solution adds no new documents");
        removed.Should().Be(0, "sync-all over a solution with all files present removes nothing");
        updated.Should().BeGreaterThan(0, "every existing doc gets rewritten with disk text");
        data["totalSynced"]?.Value<int>().Should().Be(updated + added + removed);
    }

    [Fact]
    public async Task SyncDocuments_WithSpecificFile_SyncsOnlyThatFile()
    {
        // Act - sync a specific file
        var result = await Service.SyncDocumentsAsync(new List<string> { "src/RoslynService.cs" });

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        // File exists, should be in updated list
        var updatedFiles = data["updatedFiles"] as JArray;
        updatedFiles.Should().NotBeNull();

        // Should have synced exactly 1 file
        data["totalSynced"]?.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task SyncDocuments_WithNonExistentFile_HandlesGracefully()
    {
        // Act - sync a file that doesn't exist and isn't in solution
        var result = await Service.SyncDocumentsAsync(new List<string> { "src/NonExistent.cs" });

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        // Nothing to sync for non-existent file not in solution
        data["totalSynced"]?.Value<int>().Should().Be(0);
    }
}
