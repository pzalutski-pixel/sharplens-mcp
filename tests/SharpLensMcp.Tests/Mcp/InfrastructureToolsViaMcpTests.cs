using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// End-to-end MCP-layer tests for the Infrastructure-category tools. Every
// assertion is grounded in concrete facts about the loaded SharpLens solution:
// 3 projects (SharpLensMcp + SharpLensMcp.Tests + SharpLensMcp.Tests.TestAnalyzers);
// zero project cycles; specific NuGet packages per project (verified against
// the .csproj files).
//
// Response shapes:
//  - health_check:              RoslynService.cs:513-546
//  - load_solution:             RoslynService.cs:291-304
//  - sync_documents:            RoslynService.cs:426-443
//  - get_project_structure:     Analysis.cs:859-872 (summary) / 921-934 (full)
//  - dependency_graph:          Inspection.cs:637-666
//  - get_code_fixes:            Analysis.cs:315-336
//  - apply_code_fix:            Analysis.cs (error path tested here)
//  - get_nuget_dependencies:    Discovery.cs:378-383
//  - get_source_generators:     Discovery.cs:438-443
//  - get_generated_code:        Discovery.cs:475-484
//
// Tightening rule: every accessor uses `!.Value<T>()` (NRE on missing).
public class InfrastructureToolsViaMcpTests : McpTestBase
{
    public InfrastructureToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task HealthCheck_ReturnsReadyWithSpecificSolutionMetadata()
    {
        var data = await CallAndGetDataAsync("roslyn:health_check");
        data["status"]!.Value<string>().Should().Be("Ready");

        var solution = data["solution"]!;
        solution["loaded"]!.Value<bool>().Should().BeTrue();
        solution["path"]!.Value<string>()!.Should().EndWith("SharpLensMcp.sln");
        solution["projects"]!.Value<int>().Should().BeGreaterOrEqualTo(3,
            "the solution has SharpLensMcp + SharpLensMcp.Tests + SharpLensMcp.Tests.TestAnalyzers");
        solution["documents"]!.Value<int>().Should().BeGreaterThan(50);
        // Errors/warnings counts come from MSBuildWorkspace compilation, which
        // doesn't see the same world as `dotnet build` (metadata-resolution noise).
        // The health_check view of these counts is inherently noisy; the strict
        // clean-build invariant lives in get_diagnostics tests, not here. Just
        // assert the fields exist with Integer type — bang first to NRE on missing.
        solution["errors"]!.Type.Should().Be(JTokenType.Integer);
        solution["warnings"]!.Type.Should().Be(JTokenType.Integer);
        solution["loadedAt"]!.Value<string>().Should().NotBeNullOrEmpty();

        var workspace = data["workspace"]!;
        workspace["indexed"]!.Value<bool>().Should().BeTrue();
        workspace["cacheSize"]!.Value<int>().Should().BeGreaterOrEqualTo(0,
            "cacheSize tracks the document cache (RoslynService.cs:530)");

        var config = data["configuration"]!;
        config["maxDiagnostics"]!.Value<int>().Should().Be(100,
            "the default ROSLYN_MAX_DIAGNOSTICS is 100");
        config["timeoutSeconds"]!.Value<int>().Should().BeGreaterThan(0,
            "timeoutSeconds is always emitted (RoslynService.cs:543)");
        config["semanticCacheEnabled"]!.Value<bool>().Should().BeTrue(
            "default semantic-cache state when env var not set to 'false'");

        var capabilities = data["capabilities"]!;
        capabilities["findReferences"]!.Value<bool>().Should().BeTrue();
        capabilities["findImplementations"]!.Value<bool>().Should().BeTrue();
        capabilities["codeFixProvider"]!.Value<bool>().Should().BeTrue();
        capabilities["symbolSearch"]!.Value<bool>().Should().BeTrue();
        capabilities["diagnostics"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task LoadSolution_AlreadyLoaded_ReturnsExpectedCounts()
    {
        var data = await CallAndGetDataAsync("roslyn:load_solution", new
        {
            solutionPath = Fixture.SolutionPath
        });
        data["solutionPath"]!.Value<string>().Should().Be(Fixture.SolutionPath,
            "solutionPath echoes the input (RoslynService.cs:294)");
        data["projectCount"]!.Value<int>().Should().BeGreaterOrEqualTo(3);
        data["documentCount"]!.Value<int>().Should().BeGreaterThan(50,
            "the test project alone has well over 30 files; total is much larger");
    }

    [Fact]
    public async Task LoadSolution_NonExistentPath_ReturnsFileNotFoundEchoingPath()
    {
        // RoslynService.cs:255-263: File.Exists(false) → FileNotFound with the
        // path echoed in the message.
        var error = await CallAndGetErrorAsync(
            "roslyn:load_solution",
            new { solutionPath = "/does/not/exist/Nope.sln" },
            codeContains: ErrorCodes.FileNotFound);
        // The helper already enforced codeContains; replaced the prior redundant
        // silent-pass code re-check with a useful message lock.
        error["message"]!.Value<string>()!.Should().Contain("Nope.sln",
            "the error message must echo the bad path for caller correlation");
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
            "totalSynced is defined as the sum of the three buckets (RoslynService.cs:432)");
        total.Should().BeGreaterThan(0,
            "the solution has many .cs files; syncing all must surface at least one");
    }

    [Fact]
    public async Task SyncDocuments_SpecificFile_UpdatesExactlyThatFile()
    {
        var data = await CallAndGetDataAsync("roslyn:sync_documents", new
        {
            filePaths = new[] { Fixture.RoslynServicePath }
        });
        data["totalSynced"]!.Value<int>().Should().Be(1, "exactly one file was requested");
        data["updated"]!.Value<int>().Should().Be(1, "the file already exists on disk and in the solution");
        data["added"]!.Value<int>().Should().Be(0);
        data["removed"]!.Value<int>().Should().Be(0);

        var updatedFiles = (data["updatedFiles"] as JArray)!;
        updatedFiles.Count.Should().Be(1);
        updatedFiles[0]!.Value<string>()!.Should().EndWith("RoslynService.cs");
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
        var projects = (data["projects"] as JArray)!;
        projects.Should().NotBeEmpty();

        // Each summary-mode entry has name, documentCount, projectReferenceCount,
        // language (Analysis.cs:851-857). The predicate `?.` returning false-on-null
        // is safe for filtering — but lock per-entry name field presence first.
        foreach (var p in projects)
        {
            p["name"]!.Value<string>().Should().NotBeNullOrEmpty();
        }
        var names = projects.Select(p => p["name"]!.Value<string>()!).ToList();
        names.Should().Contain("SharpLensMcp");
        names.Should().Contain("SharpLensMcp.Tests");
        names.Should().Contain("SharpLensMcp.Tests.TestAnalyzers");
    }

    [Fact]
    public async Task DependencyGraph_Json_ReportsKnownDependencies()
    {
        var data = await CallAndGetDataAsync("roslyn:dependency_graph", new { });
        // JSON form (Inspection.cs:654-660): dependencies (per-project map),
        // hasCycles, cycles.
        data["hasCycles"]!.Value<bool>().Should().BeFalse(
            "the SharpLens solution has no project cycles");
        var cycles = (data["cycles"] as JArray)!;
        cycles.Count.Should().Be(0);

        var deps = (data["dependencies"] as JObject)!;
        var testDeps = (deps["SharpLensMcp.Tests"] as JArray)!;
        testDeps.Should().NotBeNull("SharpLensMcp.Tests must appear in the dependency map");
        testDeps.Select(t => t.Value<string>()).Should().Contain("SharpLensMcp",
            "the test project references the main project");
    }

    [Fact]
    public async Task DependencyGraph_Mermaid_ContainsGraphHeaderAndProjectNodes()
    {
        var data = await CallAndGetDataAsync("roslyn:dependency_graph", new { format = "mermaid" });
        // Mermaid form (Inspection.cs:637-644): format, graph, hasCycles, cycles.
        data["format"]!.Value<string>().Should().Be("mermaid");
        data["hasCycles"]!.Value<bool>().Should().BeFalse();
        // Lock cycles array presence — impl emits it in both formats.
        (data["cycles"] as JArray)!.Count.Should().Be(0);

        var graph = data["graph"]!.Value<string>()!;
        graph.Should().StartWith("graph TD");
        graph.Should().Contain("SharpLensMcp",
            "the main project must appear in the rendered graph");
        graph.Should().Contain("SharpLensMcp.Tests",
            "the test project must appear in the rendered graph");
    }

    [Fact]
    public async Task GetCodeFixes_NonExistentDiagnostic_ReturnsSymbolNotFoundWithNearbyHint()
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
        // Helper already enforced the code; lock the message contract instead
        // (Analysis.cs:301).
        error["message"]!.Value<string>()!.Should().Contain("ZZZ9999",
            "the error message must echo the diagnostic ID that wasn't found");
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
        error["message"]!.Value<string>()!.Should().Contain("ZZZ9999");
    }

    [Fact]
    public async Task GetNugetDependencies_SharpLensMcpProject_ContainsKnownPackages()
    {
        var data = await CallAndGetDataAsync("roslyn:get_nuget_dependencies", new
        {
            projectName = "SharpLensMcp"
        });
        var projects = (data["projects"] as JArray)!;
        projects.Should().NotBeEmpty();
        var sharpLens = projects.FirstOrDefault(p =>
            p["projectName"]?.Value<string>() == "SharpLensMcp");
        sharpLens.Should().NotBeNull();

        var packages = (sharpLens!["packages"] as JArray)!;
        packages.Should().NotBeEmpty();
        // Each package entry has packageName, version, privateAssets, excludeAssets
        // (Discovery.cs:354-360).
        foreach (var pkg in packages)
        {
            pkg["packageName"]!.Value<string>().Should().NotBeNullOrEmpty();
            pkg["version"]!.Value<string>().Should().NotBeNullOrEmpty(
                "version defaults to 'unknown' if missing (Discovery.cs:357)");
        }
        var packageNames = packages.Select(p => p["packageName"]!.Value<string>()!).ToList();
        packageNames.Should().Contain("Microsoft.CodeAnalysis.Workspaces.MSBuild",
            "verified against src/SharpLensMcp.csproj");
        packageNames.Should().Contain("Microsoft.Build.Locator");
        packageNames.Should().Contain("Microsoft.SourceLink.GitHub");

        // Lock privateAssets/excludeAssets parsing (Discovery.cs:358-359).
        // SharpLensMcp.csproj sets PrivateAssets="all" on SourceLink.GitHub and
        // PrivateAssets="all" + ExcludeAssets="runtime" on Microsoft.Build.Framework.
        var sourceLink = packages.First(p =>
            p["packageName"]!.Value<string>() == "Microsoft.SourceLink.GitHub");
        sourceLink["privateAssets"]!.Value<string>()!.Should().BeOneOf("all", "All",
            "PrivateAssets is parsed verbatim from the csproj");

        var buildFramework = packages.First(p =>
            p["packageName"]!.Value<string>() == "Microsoft.Build.Framework");
        buildFramework["excludeAssets"]!.Value<string>().Should().Be("runtime");
        buildFramework["privateAssets"]!.Value<string>()!.Should().BeOneOf("all", "All");
    }

    [Fact]
    public async Task GetNugetDependencies_TestsProject_ContainsXunitAndFluentAssertions()
    {
        var data = await CallAndGetDataAsync("roslyn:get_nuget_dependencies", new
        {
            projectName = "SharpLensMcp.Tests"
        });
        var projects = (data["projects"] as JArray)!;
        var tests = projects.First(p =>
            p["projectName"]?.Value<string>() == "SharpLensMcp.Tests");

        var packageNames = (tests["packages"] as JArray)!
            .Select(p => p["packageName"]!.Value<string>()!)
            .ToList();
        packageNames.Should().Contain("xunit");
        packageNames.Should().Contain("FluentAssertions");
    }

    [Fact]
    public async Task GetSourceGenerators_OnlyIncludesProjectsWithGenerators()
    {
        // GetSourceGeneratorsAsync (Discovery.cs:401) skips projects with zero
        // generators. The .NET 8 SDK ships implicit generators (Regex,
        // LoggerMessage, etc.) via AnalyzerReferences, so net8.0 projects in our
        // solution surface them. Per-project contract: every returned entry MUST
        // have a non-empty generators array (the skip condition guarantees this)
        // and a generatedFiles array.
        var data = await CallAndGetDataAsync("roslyn:get_source_generators", new
        {
            projectName = (string?)null
        });
        var projects = (data["projects"] as JArray)!;

        foreach (var p in projects)
        {
            p["projectName"]!.Value<string>().Should().NotBeNullOrEmpty();
            var gens = (p["generators"] as JArray)!;
            gens.Should().NotBeEmpty(
                "Discovery.cs:401 skips projects with zero generators; returned entries must have at least one");
            // Each generator declares typeName and assemblyName (Discovery.cs:429-433).
            foreach (var g in gens)
            {
                g["typeName"]!.Value<string>().Should().NotBeNullOrEmpty();
                g["assemblyName"]!.Value<string>().Should().NotBeNullOrEmpty();
            }
            // generatedFiles must be an array (possibly empty).
            (p["generatedFiles"] as JArray).Should().NotBeNull(
                "Discovery.cs:412 always emits a generatedFiles array per project entry");
        }
    }

    [Fact]
    public async Task GetSourceGenerators_FilteredToTestAnalyzers_ReturnsEmpty()
    {
        // SharpLensMcp.Tests.TestAnalyzers targets netstandard2.0 and hosts an
        // analyzer (AlwaysFiresAnalyzer) but no [Generator] types. It must have
        // zero generator entries; the projects array stays empty (Discovery.cs:401 skip).
        var data = await CallAndGetDataAsync("roslyn:get_source_generators", new
        {
            projectName = "SharpLensMcp.Tests.TestAnalyzers"
        });
        var projects = (data["projects"] as JArray)!;
        projects.Count.Should().Be(0,
            "the TestAnalyzers project hosts an analyzer, not a generator");
    }

    [Fact]
    public async Task GetGeneratedCode_MissingFile_ReturnsFileNotFoundEchoingFileName()
    {
        // GetGeneratedCodeAsync (Discovery.cs:488): unknown generated file name
        // in a known project → FileNotFound with the filename echoed.
        var error = await CallAndGetErrorAsync(
            "roslyn:get_generated_code",
            new
            {
                projectName = "SharpLensMcp",
                generatedFileName = "DoesNotExist_12345.g.cs"
            },
            codeContains: ErrorCodes.FileNotFound);
        error["message"]!.Value<string>()!.Should().Contain("DoesNotExist_12345",
            "the error message must echo the bad generated-file name");
    }

    [Fact]
    public async Task GetGeneratedCode_UnknownProject_ReturnsFileNotFoundEchoingProjectName()
    {
        // GetGeneratedCodeAsync (Discovery.cs:453): unknown projectName branch is
        // distinct from the unknown-generated-file branch (Discovery.cs:488).
        // Both use FileNotFound but the messages differ.
        var error = await CallAndGetErrorAsync(
            "roslyn:get_generated_code",
            new
            {
                projectName = "DoesNotExist_12345",
                generatedFileName = "Anything.g.cs"
            },
            codeContains: ErrorCodes.FileNotFound);
        error["message"]!.Value<string>()!.Should().Contain("DoesNotExist_12345",
            "the project-not-found branch must echo the bad project name");
    }
}
