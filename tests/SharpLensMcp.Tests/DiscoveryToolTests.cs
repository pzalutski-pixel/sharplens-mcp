using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for discovery tools: find_attribute_usages, get_di_registrations,
/// find_reflection_usage, find_circular_dependencies, get_nuget_dependencies,
/// get_source_generators, get_generated_code.
/// </summary>
public class DiscoveryToolTests : RoslynServiceTestBase
{
    #region find_attribute_usages

    [Fact]
    public async Task FindAttributeUsages_WithJsonConverterAttribute_FindsRequestIdWithFullEntryShape()
    {
        var result = await Service.FindAttributeUsagesAsync("JsonConverter");
        AssertSuccess(result);
        var data = GetData(result);
        var usages = data["usages"] as JArray;
        usages.Should().NotBeNullOrEmpty(
            "RequestId in SharpLensMcp is decorated with [JsonConverter]");

        // Lock the entry shape on the matched RequestId usage so a regression that
        // drops attribute/location fields would fail.
        var requestIdUsage = usages!.First(u =>
            u["symbolName"]?.Value<string>()?.Contains("RequestId") == true);
        requestIdUsage["attribute"]?["name"]?.Value<string>().Should()
            .Be("JsonConverterAttribute", "the matched attribute's full type name must appear");
        requestIdUsage["location"]?["filePath"]?.Value<string>().Should()
            .EndWith(".cs", "every usage must carry a source filePath");
        requestIdUsage["location"]?["line"]?.Type.Should().Be(JTokenType.Integer);

        // totalCount is the unbounded counter; returnedCount might equal it or be capped.
        var meta = JObject.FromObject(result)["meta"];
        meta?["totalCount"]?.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FindAttributeUsages_WithNonExistentAttribute_ReturnsEmpty()
    {
        var result = await Service.FindAttributeUsagesAsync("NonExistentAttribute12345");
        AssertSuccess(result);
        var data = GetData(result);
        var usages = data["usages"] as JArray;
        usages.Should().NotBeNull();
        usages!.Count.Should().Be(0);
    }

    [Fact]
    public async Task FindAttributeUsages_WithProjectFilter_OnlyMainProjectMatches()
    {
        var result = await Service.FindAttributeUsagesAsync("JsonConverter", projectName: "SharpLensMcp");
        AssertSuccess(result);
        var data = GetData(result);
        var usages = data["usages"] as JArray;
        usages.Should().NotBeNullOrEmpty(
            "RequestId in SharpLensMcp is decorated with [JsonConverter]");
        usages!.Any(u => u["symbolName"]?.Value<string>()?.Contains("RequestId") == true)
            .Should().BeTrue();

        // The filter contract: NO usage from outside the SharpLensMcp project may appear.
        // Without this, "only main project matches" is unproven — the filter could be
        // a no-op and the test would still pass.
        foreach (var usage in usages)
        {
            var path = usage["location"]?["filePath"]?.Value<string>() ?? "";
            path.Should().NotContain("/tests/",
                "filter=SharpLensMcp must exclude test-project files");
            path.Should().NotContain("\\tests\\",
                "filter=SharpLensMcp must exclude test-project files (Windows path)");
        }
    }

    #endregion

    #region get_di_registrations

    [Fact]
    public async Task GetDiRegistrations_OnSolutionWithoutDI_ReturnsEmpty()
    {
        var result = await Service.GetDiRegistrationsAsync();
        AssertSuccess(result);
        var data = GetData(result);
        var registrations = data["registrations"] as JArray;
        registrations.Should().NotBeNull();
        registrations!.Count.Should().Be(0);
    }

    [Fact]
    public async Task GetDiRegistrations_FilteredToMainProject_IsEmpty()
    {
        var result = await Service.GetDiRegistrationsAsync(projectName: "SharpLensMcp");
        AssertSuccess(result);
        var registrations = GetData(result)["registrations"] as JArray;
        registrations!.Count.Should().Be(0,
            "SharpLensMcp is a stdio MCP server — no IServiceCollection registrations");
    }

    #endregion

    #region find_reflection_usage

    [Fact]
    public async Task FindReflectionUsage_OnSolution_FindsRoslynServiceHelpers()
    {
        // RoslynService's IsSuccessResponse/GetResponseData/GetResponseError each call
        // Type.GetProperty(...) and PropertyInfo.GetValue(...). At least 3 of each.
        var result = await Service.FindReflectionUsageAsync();
        AssertSuccess(result);
        var usages = GetData(result)["usages"] as JArray;
        usages.Should().NotBeNullOrEmpty();
        usages!.Count(u => u["reflectionApi"]?.Value<string>() == "Type.GetProperty"
                       && u["location"]?["filePath"]?.Value<string>()?.EndsWith("RoslynService.cs") == true)
            .Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public async Task FindReflectionUsage_WithProjectFilter_RestrictsToFilteredProject()
    {
        var result = await Service.FindReflectionUsageAsync(projectName: "SharpLensMcp");
        AssertSuccess(result);
        var usages = GetData(result)["usages"] as JArray;
        usages!.Any(u => u["reflectionApi"]?.Value<string>() == "Type.GetProperty")
            .Should().BeTrue(
                "the SharpLensMcp project contains the reflection helpers in RoslynService.cs");

        // Prove the filter — every reported usage must be in the SharpLensMcp project,
        // not in a test file.
        foreach (var usage in usages)
        {
            var path = usage["location"]?["filePath"]?.Value<string>() ?? "";
            path.Should().NotContain("/tests/",
                "filter=SharpLensMcp must exclude test-project reflection usages");
            path.Should().NotContain("\\tests\\");
        }
    }

    #endregion

    #region find_circular_dependencies

    [Fact]
    public async Task FindCircularDependencies_ProjectLevel_GraphIncludesTestProjectDep()
    {
        var result = await Service.FindCircularDependenciesAsync("project");
        AssertSuccess(result);
        var data = GetData(result);
        data["level"]?.Value<string>().Should().Be("project");
        // Graph is keyed by project name; SharpLensMcp.Tests references SharpLensMcp.
        var graph = data["graph"] as JObject;
        graph.Should().NotBeNull();
        var testDeps = graph!["SharpLensMcp.Tests"] as JArray;
        testDeps.Should().NotBeNull();
        testDeps!.Select(t => t.Value<string>()).Should().Contain("SharpLensMcp");
    }

    [Fact]
    public async Task FindCircularDependencies_NamespaceLevel_ReportsKnownNamespaceCount()
    {
        var result = await Service.FindCircularDependenciesAsync("namespace");
        AssertSuccess(result);
        var data = GetData(result);
        data["level"]?.Value<string>().Should().Be("namespace");
        // Our solution declares at least SharpLensMcp, SharpLensMcp.Tests,
        // SharpLensMcp.Tests.Mcp, SharpLensMcp.Tests.Fixtures, SharpLensMcp.Tests.TestAnalyzers.
        data["namespaceCount"]?.Value<int>().Should().BeGreaterOrEqualTo(5);
        // The graph keys must include the well-known namespaces.
        var graph = data["graph"] as JObject;
        graph.Should().NotBeNull();
        graph!.ContainsKey("SharpLensMcp").Should().BeTrue();
        graph.ContainsKey("SharpLensMcp.Tests").Should().BeTrue();
        graph.ContainsKey("SharpLensMcp.Tests.Fixtures").Should().BeTrue();
    }

    [Fact]
    public async Task FindCircularDependencies_WithInvalidLevel_ReturnsInvalidParameter()
    {
        var result = await Service.FindCircularDependenciesAsync("filesystem");
        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task FindCircularDependencies_OnCleanSolution_NoCycles()
    {
        var result = await Service.FindCircularDependenciesAsync("project");
        AssertSuccess(result);
        var data = GetData(result);
        data["hasCycles"]?.Value<bool>().Should().BeFalse();
    }

    #endregion

    #region get_nuget_dependencies

    [Fact]
    public async Task GetNuGetDependencies_ReturnsPackages()
    {
        var result = await Service.GetNuGetDependenciesAsync();
        AssertSuccess(result);
        var data = GetData(result);
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNull();
        projects!.Count.Should().BeGreaterThan(0);

        var mainProject = projects!.FirstOrDefault(p => p["projectName"]?.Value<string>() == "SharpLensMcp");
        mainProject.Should().NotBeNull();
        var packages = mainProject!["packages"] as JArray;
        packages.Should().NotBeNull();
        packages!.Any(p => p["packageName"]?.Value<string>() == "Microsoft.Build.Locator").Should().BeTrue();
    }

    [Fact]
    public async Task GetNuGetDependencies_WithProjectFilter_FiltersResults()
    {
        var result = await Service.GetNuGetDependenciesAsync(projectName: "SharpLensMcp");
        AssertSuccess(result);
        var data = GetData(result);
        var projects = data["projects"] as JArray;
        projects!.Count.Should().Be(1);
        projects![0]["projectName"]?.Value<string>().Should().Be("SharpLensMcp");
    }

    [Fact]
    public async Task GetNuGetDependencies_IncludesVersion()
    {
        var result = await Service.GetNuGetDependenciesAsync(projectName: "SharpLensMcp");
        AssertSuccess(result);
        var data = GetData(result);
        var packages = (data["projects"] as JArray)?[0]?["packages"] as JArray;
        // SharpLensMcp.csproj declares 6+ PackageReferences — packages must be non-empty,
        // or the .All() check silently passes regardless of impl behavior.
        packages.Should().NotBeNullOrEmpty("SharpLensMcp project has direct package references");
        // Per-entry shape: every package must carry packageName (non-empty) AND version (non-empty).
        foreach (var pkg in packages!)
        {
            pkg["packageName"]?.Value<string>().Should().NotBeNullOrEmpty(
                "every package entry must carry a non-empty packageName");
            pkg["version"]?.Value<string>().Should().NotBeNullOrEmpty(
                "every package entry must carry a non-empty resolved version string");
        }
    }

    #endregion

    #region get_source_generators

    [Fact]
    public async Task GetSourceGenerators_OnSolution_OnlyIncludesProjectsWithGenerators()
    {
        var result = await Service.GetSourceGeneratorsAsync();
        AssertSuccess(result);
        var projects = GetData(result)["projects"] as JArray;
        // SharpLensMcp.Tests uses [JsonSerializable] which triggers System.Text.Json's
        // source generator — at least one project must be reported, or the foreach
        // silently passes regardless of impl behavior.
        projects.Should().NotBeNullOrEmpty(
            "SharpLensMcp.Tests has a [JsonSerializable] consumer that triggers the JSON source generator");
        foreach (var p in projects!)
        {
            var gens = p["generators"] as JArray;
            gens.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetSourceGenerators_FilteredToTestAnalyzers_ReturnsEmpty()
    {
        // TestAnalyzers project hosts an analyzer, not a generator; it must be excluded.
        var result = await Service.GetSourceGeneratorsAsync(projectName: "SharpLensMcp.Tests.TestAnalyzers");
        AssertSuccess(result);
        var projects = GetData(result)["projects"] as JArray;
        projects!.Count.Should().Be(0);
    }

    #endregion

    #region get_generated_code

    [Fact]
    public async Task GetGeneratedCode_WithNonExistentFile_ReturnsFileNotFound()
    {
        var result = await Service.GetGeneratedCodeAsync("SharpLensMcp", "NonExistentFile.g.cs");
        AssertError(result, ErrorCodes.FileNotFound);
    }

    #endregion
}
