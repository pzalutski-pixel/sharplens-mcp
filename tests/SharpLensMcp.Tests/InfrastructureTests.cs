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
    public async Task GetHealthCheck_ReturnsSolutionInfoWithProjectCountAndDiagnosticTotals()
    {
        var result = await Service.GetHealthCheckAsync();

        AssertSuccess(result);
        var data = GetData(result);
        data["status"]?.Value<string>().Should().Be("Ready");
        data["solution"]?["loaded"]?.Value<bool>().Should().BeTrue();
        data["solution"]?["path"]?.Value<string>().Should().EndWith("SharpLensMcp.sln");
        // The solution sub-object also reports projectCount.
        data["solution"]?["projectCount"]?.Value<int>().Should().Be(3,
            "the SharpLensMcp solution has 3 csproj projects");
        // health_check samples the first 5 projects for diagnostic counts (RoslynService.cs:488).
        // The solution should be clean — zero errors AND zero warnings on the sample.
        data["errorCount"]?.Value<int>().Should().Be(0,
            "the sampled projects must compile cleanly");
        data["warningCount"]?.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task LoadSolution_WithValidPath_ReturnsProjectAndDocumentCounts()
    {
        // Already loaded in InitializeAsync, but we can verify the result
        var result = await Service.LoadSolutionAsync(SolutionPath);

        AssertSuccess(result);
        var data = GetData(result);
        // The SharpLensMcp solution contains exactly three csproj projects:
        // SharpLensMcp, SharpLensMcp.Tests, SharpLensMcp.Tests.TestAnalyzers.
        data["projectCount"]?.Value<int>().Should().Be(3);
        // documentCount aggregates source documents across the three projects — at least
        // the count of files we maintain in src/ + tests/ (well over 30 combined).
        data["documentCount"]?.Value<int>().Should().BeGreaterThan(20,
            "the solution declares many source documents across its three projects");
        // The response also round-trips the loaded solutionPath.
        data["solutionPath"]?.Value<string>().Should().EndWith("SharpLensMcp.sln");
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
    public async Task GetProjectStructure_WithIncludesOn_ReturnsDocumentsAndReferencesArrays()
    {
        var result = await Service.GetProjectStructureAsync(
            includeReferences: true,
            includeDocuments: true);

        AssertSuccess(result);
        var data = GetData(result);
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNull();
        projects!.Count.Should().Be(3, "the solution has exactly three csproj projects");

        // First project shape — name + language locked. Plus the full mode must emit
        // the documents AND references arrays (opposite of summaryOnly).
        var firstProject = projects[0];
        firstProject["name"]?.Value<string>().Should().Be("SharpLensMcp");
        firstProject["language"]?.Value<string>().Should().Be("C#");
        firstProject["documents"].Should().NotBeNull(
            "includeDocuments=true must emit the documents array");
        firstProject["references"].Should().NotBeNull(
            "includeReferences=true must emit the references array");
        (firstProject["documents"] as JArray)!.Count.Should().BeGreaterThan(0,
            "SharpLensMcp has multiple .cs files");
        (firstProject["references"] as JArray)!.Count.Should().BeGreaterThan(0,
            "SharpLensMcp has multiple metadata references (Roslyn packages, BCL)");
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
        var result = await Service.SyncDocumentsAsync(new List<string> { "src/RoslynService.cs" });

        AssertSuccess(result);
        var data = GetData(result);
        // The updated count + the updatedFiles list must agree, and the named file
        // must appear in the list.
        data["totalSynced"]?.Value<int>().Should().Be(1);
        data["updated"]?.Value<int>().Should().Be(1);
        data["added"]?.Value<int>().Should().Be(0);
        data["removed"]?.Value<int>().Should().Be(0);
        var updatedFiles = data["updatedFiles"] as JArray;
        updatedFiles.Should().NotBeNullOrEmpty();
        updatedFiles!.Any(f => f.Value<string>()?.EndsWith("RoslynService.cs") == true)
            .Should().BeTrue("the requested file must appear in the updatedFiles list");
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
        data["updated"]?.Value<int>().Should().Be(0);
        data["added"]?.Value<int>().Should().Be(0);
        data["removed"]?.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetProjectStructure_WithProjectNamePattern_RestrictsToMatching()
    {
        // Pattern "SharpLensMcp.Tests" matches the test project AND the analyzers
        // project (which starts with the same prefix); pure "SharpLensMcp" matches
        // only the main project because the pattern is anchored ^…$ (Analysis.cs:849).
        var result = await Service.GetProjectStructureAsync(
            includeReferences: false,
            includeDocuments: false,
            projectNamePattern: "SharpLensMcp",
            summaryOnly: true);

        AssertSuccess(result);
        var projects = (GetData(result)["projects"] as JArray)!;
        projects.Count.Should().Be(1,
            "anchored pattern 'SharpLensMcp' matches exactly the main project");
        projects[0]["name"]?.Value<string>().Should().Be("SharpLensMcp");
    }

    [Fact]
    public async Task GetProjectStructure_WithProjectNamePatternWildcard_MatchesPrefix()
    {
        // Wildcard pattern: '*Tests*' matches SharpLensMcp.Tests AND
        // SharpLensMcp.Tests.TestAnalyzers (both contain "Tests").
        var result = await Service.GetProjectStructureAsync(
            includeReferences: false,
            includeDocuments: false,
            projectNamePattern: "*Tests*",
            summaryOnly: true);

        AssertSuccess(result);
        var projects = (GetData(result)["projects"] as JArray)!;
        var names = projects.Select(p => p["name"]?.Value<string>()).ToList();
        names.Should().Contain("SharpLensMcp.Tests");
        names.Should().Contain("SharpLensMcp.Tests.TestAnalyzers");
        names.Should().NotContain("SharpLensMcp",
            "the main project doesn't match '*Tests*'");
    }

    [Fact]
    public async Task GetProjectStructure_WithMaxProjects_CapsResults()
    {
        // maxProjects=1 must return exactly one project, even though the solution
        // has three (Analysis.cs:857-860).
        var result = await Service.GetProjectStructureAsync(
            includeReferences: false,
            includeDocuments: false,
            maxProjects: 1,
            summaryOnly: true);

        AssertSuccess(result);
        var projects = (GetData(result)["projects"] as JArray)!;
        projects.Count.Should().Be(1, "maxProjects=1 must cap the response to one entry");
    }

    [Fact]
    public async Task DependencyGraph_WithNullFormat_DefaultsToJsonDictionary()
    {
        // Inspection.cs:634 routes anything that isn't "mermaid" through the json
        // (dictionary) branch. Calling with format=null must therefore behave like
        // format="json" — emit dependencies + hasCycles + cycles, NOT a graph string.
        var result = await Service.GetDependencyGraphAsync(format: null);

        AssertSuccess(result);
        var data = GetData(result);
        (data["dependencies"] as JObject).Should().NotBeNull(
            "null format defaults to the dictionary branch");
        data["graph"].Should().BeNull(
            "the mermaid `graph` string must not appear in the default response");
        data["hasCycles"]?.Type.Should().Be(JTokenType.Boolean);
    }
}
