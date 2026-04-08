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
    public async Task FindAttributeUsages_WithProjectFilter_FiltersResults()
    {
        var result = await Service.FindAttributeUsagesAsync("JsonConverter", projectName: "SharpLensMcp");
        AssertSuccess(result);
        var data = GetData(result);
        var usages = data["usages"] as JArray;
        usages.Should().NotBeNull();
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
    public async Task GetDiRegistrations_ReturnsSuccessResponse()
    {
        var result = await Service.GetDiRegistrationsAsync(projectName: "SharpLensMcp");
        AssertSuccess(result);
    }

    #endregion

    #region find_reflection_usage

    [Fact]
    public async Task FindReflectionUsage_OnSolution_ReturnsSuccessResponse()
    {
        var result = await Service.FindReflectionUsageAsync();
        AssertSuccess(result);
        var data = GetData(result);
        data["usages"].Should().NotBeNull();
    }

    [Fact]
    public async Task FindReflectionUsage_WithProjectFilter_FiltersResults()
    {
        var result = await Service.FindReflectionUsageAsync(projectName: "SharpLensMcp");
        AssertSuccess(result);
    }

    #endregion

    #region find_circular_dependencies

    [Fact]
    public async Task FindCircularDependencies_ProjectLevel_ReturnsGraph()
    {
        var result = await Service.FindCircularDependenciesAsync("project");
        AssertSuccess(result);
        var data = GetData(result);
        data["level"]?.Value<string>().Should().Be("project");
        data["graph"].Should().NotBeNull();
    }

    [Fact]
    public async Task FindCircularDependencies_NamespaceLevel_ReturnsGraph()
    {
        var result = await Service.FindCircularDependenciesAsync("namespace");
        AssertSuccess(result);
        var data = GetData(result);
        data["level"]?.Value<string>().Should().Be("namespace");
        data["namespaceCount"].Should().NotBeNull();
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
    public async Task GetSourceGenerators_ReturnsSuccessResponse()
    {
        var result = await Service.GetSourceGeneratorsAsync();
        AssertSuccess(result);
        var data = GetData(result);
        data["projects"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetSourceGenerators_WithProjectFilter_FiltersResults()
    {
        var result = await Service.GetSourceGeneratorsAsync(projectName: "SharpLensMcp");
        AssertSuccess(result);
    }

    #endregion

    #region get_generated_code

    [Fact]
    public async Task GetGeneratedCode_WithNonExistentFile_ReturnsError()
    {
        var result = await Service.GetGeneratedCodeAsync("SharpLensMcp", "NonExistentFile.g.cs");
        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeFalse();
    }

    #endregion
}
