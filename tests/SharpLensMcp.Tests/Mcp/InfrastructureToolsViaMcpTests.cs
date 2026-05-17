using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// End-to-end MCP-layer tests for the 10 Infrastructure-category tools.
// Every assertion is grounded in concrete facts about the loaded SharpLens
// solution: 3 projects (SharpLensMcp + SharpLensMcp.Tests +
// SharpLensMcp.Tests.TestAnalyzers); zero project cycles; specific NuGet
// packages per project (verified against the .csproj files).
public class InfrastructureToolsViaMcpTests : McpTestBase
{
    public InfrastructureToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task HealthCheck_ReturnsReadyWithSpecificSolutionMetadata()
    {
        var data = await CallAndGetDataAsync("roslyn:health_check");
        data["status"]?.Value<string>().Should().Be("Ready");

        var solution = data["solution"]!;
        solution["loaded"]?.Value<bool>().Should().BeTrue();
        solution["path"]?.Value<string>().Should().EndWith("SharpLensMcp.sln");
        solution["projects"]?.Value<int>().Should().BeGreaterOrEqualTo(3,
            "the solution has SharpLensMcp + SharpLensMcp.Tests + SharpLensMcp.Tests.TestAnalyzers");
        solution["documents"]?.Value<int>().Should().BeGreaterThan(50);
        // Errors/warnings counts come from MSBuildWorkspace compilation, which doesn't
        // see the same world as `dotnet build` (metadata-resolution noise). The
        // health_check view of these counts is inherently noisy; the strict
        // clean-build invariant lives in get_diagnostics tests, not here. Just
        // assert the fields exist with the expected types.
        solution["errors"]?.Type.Should().Be(JTokenType.Integer);
        solution["warnings"]?.Type.Should().Be(JTokenType.Integer);
        solution["loadedAt"]?.Value<string>().Should().NotBeNullOrEmpty();

        data["workspace"]!["indexed"]?.Value<bool>().Should().BeTrue();
        data["configuration"]!["maxDiagnostics"]?.Value<int>().Should().Be(100,
            "the default ROSLYN_MAX_DIAGNOSTICS is 100 (RoslynService.cs:36)");
        data["capabilities"]!["findReferences"]?.Value<bool>().Should().BeTrue();
        data["capabilities"]!["diagnostics"]?.Value<bool>().Should().BeTrue();
        data["capabilities"]!["codeFixProvider"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task LoadSolution_AlreadyLoaded_ReturnsExpectedCounts()
    {
        var data = await CallAndGetDataAsync("roslyn:load_solution", new
        {
            solutionPath = Fixture.SolutionPath
        });
        data["solutionPath"]?.Value<string>().Should().Be(Fixture.SolutionPath,
            "solutionPath echoes the input");
        data["projectCount"]?.Value<int>().Should().BeGreaterOrEqualTo(3);
        data["documentCount"]?.Value<int>().Should().BeGreaterThan(50,
            "the test project alone has well over 30 files; total is much larger");
    }

    [Fact]
    public async Task LoadSolution_NonExistentPath_ReturnsFileNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:load_solution",
            new { solutionPath = "/does/not/exist/Nope.sln" },
            codeContains: ErrorCodes.FileNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task SyncDocuments_AllDocuments_ReportsTotalAsSumOfBuckets()
    {
        var data = await CallAndGetDataAsync("roslyn:sync_documents", new
        {
            filePaths = (string[]?)null
        });
        var updated = data["updated"]!.Value<int>();
        var added = data["added"]!.Value<int>();
        var removed = data["removed"]!.Value<int>();
        var total = data["totalSynced"]!.Value<int>();
        total.Should().Be(updated + added + removed,
            "totalSynced is defined as the sum of the three buckets");
        // The solution has many .cs files; syncing all must surface at least one.
        total.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SyncDocuments_SpecificFile_UpdatesExactlyThatFile()
    {
        var data = await CallAndGetDataAsync("roslyn:sync_documents", new
        {
            filePaths = new[] { Fixture.RoslynServicePath }
        });
        data["totalSynced"]?.Value<int>().Should().Be(1, "exactly one file was requested");
        data["updated"]?.Value<int>().Should().Be(1, "the file already exists on disk and in the solution");
        data["added"]?.Value<int>().Should().Be(0);
        data["removed"]?.Value<int>().Should().Be(0);

        var updatedFiles = data["updatedFiles"] as JArray;
        updatedFiles.Should().NotBeNull();
        updatedFiles!.Count.Should().Be(1);
        updatedFiles[0]!.Value<string>().Should().EndWith("RoslynService.cs");
    }

    [Fact]
    public async Task GetProjectStructure_ListsAllThreeKnownProjects()
    {
        var data = await CallAndGetDataAsync("roslyn:get_project_structure", new
        {
            includeReferences = false,
            includeDocuments = false,
            summaryOnly = true
        });
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNull();

        var names = projects!.Select(p => p["name"]?.Value<string>()).ToList();
        names.Should().Contain("SharpLensMcp");
        names.Should().Contain("SharpLensMcp.Tests");
        names.Should().Contain("SharpLensMcp.Tests.TestAnalyzers");
    }

    [Fact]
    public async Task DependencyGraph_Json_ReportsKnownDependencies()
    {
        var data = await CallAndGetDataAsync("roslyn:dependency_graph", new { });
        // The implementation in RoslynService.Inspection.cs returns the JSON form with
        // `dependencies` (a per-project map) plus `hasCycles` and `cycles`.
        data["hasCycles"]?.Value<bool>().Should().BeFalse(
            "the SharpLens solution has no project cycles");
        var cycles = data["cycles"] as JArray;
        cycles!.Count.Should().Be(0);

        var deps = data["dependencies"] as JObject;
        deps.Should().NotBeNull();
        var testDeps = deps!["SharpLensMcp.Tests"] as JArray;
        testDeps.Should().NotBeNull("SharpLensMcp.Tests must appear in the dependency map");
        testDeps!.Select(t => t.Value<string>()).Should().Contain("SharpLensMcp",
            "the test project references the main project");
    }

    [Fact]
    public async Task DependencyGraph_Mermaid_ContainsGraphHeaderAndProjectNodes()
    {
        var data = await CallAndGetDataAsync("roslyn:dependency_graph", new { format = "mermaid" });
        data["format"]?.Value<string>().Should().Be("mermaid");
        data["hasCycles"]?.Value<bool>().Should().BeFalse();
        var graph = data["graph"]!.Value<string>()!;
        graph.Should().StartWith("graph TD");
        graph.Should().Contain("SharpLensMcp",
            "the main project must appear in the rendered graph");
        graph.Should().Contain("SharpLensMcp.Tests",
            "the test project must appear in the rendered graph");
    }

    [Fact]
    public async Task GetCodeFixes_NonExistentDiagnostic_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:get_code_fixes",
            new
            {
                filePath = Fixture.RoslynServicePath,
                diagnosticId = "ZZZ9999",
                line = 0,
                column = 0
            },
            codeContains: ErrorCodes.SymbolNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task ApplyCodeFix_NonExistentDiagnostic_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:apply_code_fix",
            new
            {
                filePath = Fixture.RoslynServicePath,
                diagnosticId = "ZZZ9999",
                line = 0,
                column = 0,
                preview = true
            },
            codeContains: ErrorCodes.SymbolNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task GetNugetDependencies_SharpLensMcpProject_ContainsKnownPackages()
    {
        var data = await CallAndGetDataAsync("roslyn:get_nuget_dependencies", new
        {
            projectName = "SharpLensMcp"
        });
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNullOrEmpty();
        var sharpLens = projects!.First(p => p["projectName"]?.Value<string>() == "SharpLensMcp");

        var packages = sharpLens["packages"] as JArray;
        packages.Should().NotBeNullOrEmpty();
        var packageNames = packages!.Select(p => p["packageName"]?.Value<string>()).ToList();
        packageNames.Should().Contain("Microsoft.CodeAnalysis.Workspaces.MSBuild",
            "verified against src/SharpLensMcp.csproj line 59");
        packageNames.Should().Contain("Microsoft.Build.Locator",
            "verified against src/SharpLensMcp.csproj line 62");
        packageNames.Should().Contain("Microsoft.SourceLink.GitHub",
            "verified against src/SharpLensMcp.csproj line 63");
    }

    [Fact]
    public async Task GetNugetDependencies_TestsProject_ContainsXunitAndFluentAssertions()
    {
        var data = await CallAndGetDataAsync("roslyn:get_nuget_dependencies", new
        {
            projectName = "SharpLensMcp.Tests"
        });
        var projects = data["projects"] as JArray;
        var tests = projects!.First(p => p["projectName"]?.Value<string>() == "SharpLensMcp.Tests");

        var packageNames = (tests["packages"] as JArray)!
            .Select(p => p["packageName"]?.Value<string>())
            .ToList();
        packageNames.Should().Contain("xunit");
        packageNames.Should().Contain("FluentAssertions");
    }

    [Fact]
    public async Task GetSourceGenerators_OnlyIncludesProjectsWithGenerators()
    {
        // GetSourceGeneratorsAsync (Discovery.cs:385) skips projects with zero generators.
        // The .NET 8 SDK ships implicit generators (Regex, LoggerMessage, etc.) via
        // AnalyzerReferences, so net8.0 projects in our solution surface them. Per-project
        // contract: every returned entry MUST have a non-empty generators array (the skip
        // condition guarantees this) and a generatedFiles array.
        var data = await CallAndGetDataAsync("roslyn:get_source_generators", new
        {
            projectName = (string?)null
        });
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNull();

        foreach (var p in projects!)
        {
            p["projectName"]?.Value<string>().Should().NotBeNullOrEmpty();
            var gens = p["generators"] as JArray;
            gens.Should().NotBeNullOrEmpty(
                "Discovery.cs:385 skips projects with zero generators; returned entries must have at least one");
            // Each generator must declare its typeName and assemblyName.
            foreach (var g in gens!)
            {
                g["typeName"]?.Value<string>().Should().NotBeNullOrEmpty();
                g["assemblyName"]?.Value<string>().Should().NotBeNullOrEmpty();
            }
            // generatedFiles must be an array (possibly empty: not every generator
            // run in this context has produced files yet).
            (p["generatedFiles"] as JArray).Should().NotBeNull(
                "Discovery.cs:418 always emits a generatedFiles array per project entry");
        }
    }

    [Fact]
    public async Task GetSourceGenerators_FilteredToTestAnalyzers_ReturnsEmpty()
    {
        // SharpLensMcp.Tests.TestAnalyzers targets netstandard2.0 and hosts an analyzer
        // (AlwaysFiresAnalyzer) but no [Generator] types. It must have zero generator
        // entries; the projects array stays empty (Discovery.cs:385 skip).
        var data = await CallAndGetDataAsync("roslyn:get_source_generators", new
        {
            projectName = "SharpLensMcp.Tests.TestAnalyzers"
        });
        var projects = data["projects"] as JArray;
        projects!.Count.Should().Be(0,
            "the TestAnalyzers project hosts an analyzer, not a generator");
    }

    [Fact]
    public async Task GetGeneratedCode_MissingFile_ReturnsFileNotFound()
    {
        // GetGeneratedCodeAsync (Discovery.cs:472) uses ErrorCodes.FileNotFound
        // for both unknown projects and unknown generated files.
        var error = await CallAndGetErrorAsync(
            "roslyn:get_generated_code",
            new
            {
                projectName = "SharpLensMcp",
                generatedFileName = "DoesNotExist_12345.g.cs"
            },
            codeContains: ErrorCodes.FileNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.FileNotFound);
    }
}
