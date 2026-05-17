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
    public async Task FindAttributeUsages_WithJsonConverterAttribute_FindsRequestId()
    {
        var result = await Service.FindAttributeUsagesAsync("JsonConverter");
        AssertSuccess(result);
        var data = GetData(result);
        var usages = data["usages"] as JArray;
        usages.Should().NotBeNull();
        usages!.Any(u => u["symbolName"]?.Value<string>()?.Contains("RequestId") == true).Should().BeTrue();
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
    public async Task FindReflectionUsage_WithProjectFilter_ReturnsSameRoslynServiceUsages()
    {
        var result = await Service.FindReflectionUsageAsync(projectName: "SharpLensMcp");
        AssertSuccess(result);
        var usages = GetData(result)["usages"] as JArray;
        usages!.Any(u => u["reflectionApi"]?.Value<string>() == "Type.GetProperty")
            .Should().BeTrue(
                "the SharpLensMcp project contains the reflection helpers in RoslynService.cs");
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
        // Our solution has multiple namespaces (SharpLensMcp, SharpLensMcp.Tests,
        // SharpLensMcp.Tests.Mcp, SharpLensMcp.Tests.Fixtures, etc.).
        data["namespaceCount"]?.Value<int>().Should().BeGreaterOrEqualTo(4);
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
        packages.Should().NotBeNull();
        packages!.All(p => p["version"]?.Value<string>() != null).Should().BeTrue();
    }

    #endregion

    #region get_source_generators

    [Fact]
    public async Task GetSourceGenerators_OnSolution_OnlyIncludesProjectsWithGenerators()
    {
        var result = await Service.GetSourceGeneratorsAsync();
        AssertSuccess(result);
        var projects = GetData(result)["projects"] as JArray;
        projects.Should().NotBeNull();
        // Every entry must have at least one generator (impl skips empty projects).
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
