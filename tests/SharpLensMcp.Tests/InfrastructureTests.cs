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
    public async Task GetHealthCheck_WithIncludeDiagnostics_ReturnsSolutionInfoWithProjectCountAndDiagnosticTotals()
    {
        var result = await Service.GetHealthCheckAsync(includeDiagnostics: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["status"].Should().NotBeNull();
        data["status"]!.Value<string>().Should().Be("Ready");
        var solution = data["solution"];
        solution.Should().NotBeNull();
        solution!["loaded"].Should().NotBeNull();
        solution["loaded"]!.Value<bool>().Should().BeTrue();
        solution["path"].Should().NotBeNull();
        solution["path"]!.Value<string>().Should().EndWith("SharpLensMcp.sln");
        // The solution sub-object reports the project count under `projects`, and the
        // sampled diagnostic counts under `errors`/`warnings` — all nested, NOT at the
        // top level. The old top-level `errorCount`/`warningCount` and `projectCount`
        // reads never existed and silently passed.
        solution["projects"].Should().NotBeNull();
        solution["projects"]!.Value<int>().Should().Be(3,
            "the SharpLensMcp solution has 3 csproj projects");
        // includeDiagnostics=true compiles and samples the first 5 projects, so the
        // sweep must run and populate integer error/warning counts. The exact values
        // are NOT a stable lock: MSBuildWorkspace's in-process compilation carries
        // metadata-resolution noise that differs from `dotnet build`. The strict
        // clean-build invariant (0 errors) is pinned deterministically by the
        // get_diagnostics and SolutionHealthInvariant tests instead.
        solution["diagnosticsIncluded"].Should().NotBeNull();
        solution["diagnosticsIncluded"]!.Value<bool>().Should().BeTrue();
        solution["errors"]!.Type.Should().Be(JTokenType.Integer,
            "includeDiagnostics=true must populate an integer error count");
        solution["warnings"]!.Type.Should().Be(JTokenType.Integer,
            "includeDiagnostics=true must populate an integer warning count");
    }

    [Fact]
    public async Task GetHealthCheck_DefaultOmitsDiagnostics_ReturnsCountsWithNullDiagnosticTotals()
    {
        // The default is O(1): no compilation sweep, so errors/warnings are null and
        // diagnosticsIncluded is false, while the load-state and counts still report.
        var result = await Service.GetHealthCheckAsync();

        AssertSuccess(result);
        var solution = GetData(result)["solution"]!;
        solution["loaded"]!.Value<bool>().Should().BeTrue();
        solution["projects"]!.Value<int>().Should().Be(3,
            "project/document counts are O(1) and must report on the cheap path");
        solution["diagnosticsIncluded"].Should().NotBeNull();
        solution["diagnosticsIncluded"]!.Value<bool>().Should().BeFalse(
            "the default path must not run the diagnostics sweep");
        solution["errors"]!.Type.Should().Be(JTokenType.Null,
            "errors must be null when diagnostics are not computed");
        solution["warnings"]!.Type.Should().Be(JTokenType.Null,
            "warnings must be null when diagnostics are not computed");
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
        data["projectCount"].Should().NotBeNull();
        data["projectCount"]!.Value<int>().Should().Be(3);
        // documentCount aggregates source documents across the three projects — at least
        // the count of files we maintain in src/ + tests/ (well over 30 combined).
        data["documentCount"].Should().NotBeNull();
        data["documentCount"]!.Value<int>().Should().BeGreaterThan(20,
            "the solution declares many source documents across its three projects");
        // The response also round-trips the loaded solutionPath.
        data["solutionPath"].Should().NotBeNull();
        data["solutionPath"]!.Value<string>().Should().EndWith("SharpLensMcp.sln");
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
        firstProject["name"].Should().NotBeNull();
        firstProject["name"]!.Value<string>().Should().Be("SharpLensMcp");
        firstProject["language"].Should().NotBeNull();
        firstProject["language"]!.Value<string>().Should().Be("C#");
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
    public async Task GetProjectStructure_ReportsRealTargetFrameworkPerProject()
    {
        var result = await Service.GetProjectStructureAsync(
            includeReferences: false,
            includeDocuments: false);

        AssertSuccess(result);
        var projects = (GetData(result)["projects"] as JArray)!;
        var tfmByName = projects.ToDictionary(
            p => p["name"]!.Value<string>()!,
            p => p["targetFramework"]?.Value<string>());

        // Two projects with DIFFERENT real TFMs prove the value comes from each
        // project's own csproj, not a solution-wide constant or the CPU platform.
        tfmByName["SharpLensMcp"].Should().Be("net8.0");
        tfmByName["SharpLensMcp.Tests.TestAnalyzers"].Should().Be("netstandard2.0");
        tfmByName.Values.Should().NotContain("AnyCpu",
            "CompilationOptions.Platform is a CPU platform, not a target framework");
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

        // Summary entries carry name + documentCount + projectReferenceCount + language only.
        // They must NOT carry the full `documents` or `references` arrays.
        foreach (var project in projects)
        {
            project["name"].Should().NotBeNull();
            project["name"]!.Value<string>().Should().NotBeNullOrEmpty();
            project["language"].Should().NotBeNull();
            project["language"]!.Value<string>().Should().Be("C#");
            project["documentCount"].Should().NotBeNull();
            project["documentCount"]!.Type.Should().Be(JTokenType.Integer);
            project["projectReferenceCount"].Should().NotBeNull();
            project["projectReferenceCount"]!.Type.Should().Be(JTokenType.Integer);
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
        // The json format carries nodes and edges (by node id), not a graph string.
        var nameById = ((JArray)data["nodes"]!).ToDictionary(n => n["id"]!.Value<string>()!, n => n["name"]!.Value<string>()!);
        nameById.Should().ContainKey("SharpLensMcp_Tests").WhoseValue.Should().Be("SharpLensMcp.Tests",
            "the test project is a node, its id the Mermaid-valid form of its name");
        ((JArray)data["edges"]!)
            .Select(e => $"{nameById[e["from"]!.Value<string>()!]}>{nameById[e["to"]!.Value<string>()!]}")
            .Should().Contain("SharpLensMcp.Tests>SharpLensMcp",
                "SharpLensMcp.Tests project-references SharpLensMcp");

        data["hasCycles"].Should().NotBeNull();
        data["hasCycles"]!.Value<bool>().Should().BeFalse(
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
        data["format"].Should().NotBeNull();
        data["format"]!.Value<string>().Should().Be("mermaid");
        data["graph"].Should().NotBeNull();
        data["graph"]!.Value<string>().Should().Contain("graph TD");
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
    public async Task SyncDocuments_WithNoFiles_OverAnInSyncSolution_ChangesNothing()
    {
        var result = await Service.SyncDocumentsAsync(null);

        AssertSuccess(result);
        var data = GetData(result);
        // Sync-all checks every document against disk and every compile item the
        // projects evaluate to. The loaded solution matches disk, so nothing is updated,
        // added or removed, and every document path is reported unchanged.
        var documentPaths = Service.GetSolutionForTesting()!.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null)
            .Select(d => Path.GetFullPath(d.FilePath!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        data["updated"]!.Value<int>().Should().Be(0, "no file changed on disk since the load");
        data["added"]!.Value<int>().Should().Be(0, "every compile item is already a document");
        data["removed"]!.Value<int>().Should().Be(0, "every document's file still exists");
        data["unchanged"]!.Value<int>().Should().Be(documentPaths);
        data["totalSynced"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task SyncDocuments_WithSpecificUnchangedFile_ReportsItUnchanged()
    {
        var result = await Service.SyncDocumentsAsync(new List<string> { "src/RoslynService.cs" });

        AssertSuccess(result);
        var data = GetData(result);
        // The file on disk equals the loaded text: it is checked (unchanged 1) but not
        // updated, and nothing else is touched.
        data["unchanged"]!.Value<int>().Should().Be(1);
        data["totalSynced"]!.Value<int>().Should().Be(0);
        data["updated"]!.Value<int>().Should().Be(0);
        data["added"]!.Value<int>().Should().Be(0);
        data["removed"]!.Value<int>().Should().Be(0);
        ((JArray)data["updatedFiles"]!).Should().BeEmpty();
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
        data["totalSynced"].Should().NotBeNull();
        data["totalSynced"]!.Value<int>().Should().Be(0);
        data["updated"].Should().NotBeNull();
        data["updated"]!.Value<int>().Should().Be(0);
        data["added"].Should().NotBeNull();
        data["added"]!.Value<int>().Should().Be(0);
        data["removed"].Should().NotBeNull();
        data["removed"]!.Value<int>().Should().Be(0);
        var errors = (JArray)data["errors"]!;
        errors.Should().HaveCount(1, "a path that is neither on disk nor in the solution is reported, not ignored");
        errors[0]["path"]!.Value<string>().Should().Be("src/NonExistent.cs");
        errors[0]["error"]!.Value<string>().Should().Be("File not found on disk and not in the solution");
    }

    [Fact]
    public async Task GetProjectStructure_WithProjectNamePattern_RestrictsToMatching()
    {
        // Pattern "SharpLensMcp.Tests" matches the test project AND the analyzers
        // project (which starts with the same prefix); pure "SharpLensMcp" matches
        // only the main project because the pattern is anchored ^…$.
        var result = await Service.GetProjectStructureAsync(
            includeReferences: false,
            includeDocuments: false,
            projectNamePattern: "SharpLensMcp",
            summaryOnly: true);

        AssertSuccess(result);
        var projects = (GetData(result)["projects"] as JArray)!;
        projects.Count.Should().Be(1,
            "anchored pattern 'SharpLensMcp' matches exactly the main project");
        projects[0]["name"].Should().NotBeNull();
        projects[0]["name"]!.Value<string>().Should().Be("SharpLensMcp");
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
        // has three.
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
        // An absent format is json: nodes + edges + hasCycles + cycles, NOT a graph string.
        var result = await Service.GetDependencyGraphAsync(format: null);

        AssertSuccess(result);
        var data = GetData(result);
        ((JArray)data["nodes"]!).Select(n => n["name"]!.Value<string>())
            .Should().Contain(new[] { "SharpLensMcp", "SharpLensMcp.Tests" }, "null format is the json format");
        data["graph"].Should().BeNull(
            "the mermaid `graph` string must not appear in the default response");
        data["hasCycles"].Should().NotBeNull();
        data["hasCycles"]!.Type.Should().Be(JTokenType.Boolean);
    }
}
