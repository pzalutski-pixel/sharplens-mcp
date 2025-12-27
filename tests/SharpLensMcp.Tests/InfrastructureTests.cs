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
        // Act
        var result = await Service.GetHealthCheckAsync();

        // Assert - health check has different structure (status, solution.loaded, etc.)
        var json = JObject.FromObject(result);
        json["status"]?.Value<string>().Should().Be("Ready");
        json["solution"]?["loaded"]?.Value<bool>().Should().BeTrue();
        json["solution"]?["path"]?.Value<string>().Should().EndWith("SharpLensMcp.sln");
    }

    [Fact]
    public async Task LoadSolution_WithValidPath_Succeeds()
    {
        // Already loaded in InitializeAsync, but we can verify the result
        var result = await Service.LoadSolutionAsync(SolutionPath);

        AssertSuccess(result);
        var data = GetData(result);
        data["projectCount"]?.Value<int>().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task LoadSolution_WithInvalidPath_ReturnsError()
    {
        // Arrange
        var invalidPath = @"C:\NonExistent\Fake.sln";

        // Act
        var result = await Service.LoadSolutionAsync(invalidPath);

        // Assert
        AssertError(result, "not_found");
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
    public async Task GetProjectStructure_WithSummaryOnly_ReturnsMinimalInfo()
    {
        // Act
        var result = await Service.GetProjectStructureAsync(
            includeReferences: false,
            includeDocuments: false,
            summaryOnly: true);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var projects = data["projects"] as JArray;
        projects.Should().NotBeNull();
    }

    [Fact]
    public async Task DependencyGraph_ReturnsJsonFormat()
    {
        // Act
        var result = await Service.GetDependencyGraphAsync(format: "json");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["dependencies"].Should().NotBeNull();
        data["hasCycles"].Should().NotBeNull();
    }

    [Fact]
    public async Task DependencyGraph_ReturnsMermaidFormat()
    {
        // Act
        var result = await Service.GetDependencyGraphAsync(format: "mermaid");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["mermaid"]?.Value<string>().Should().Contain("graph");
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
}
