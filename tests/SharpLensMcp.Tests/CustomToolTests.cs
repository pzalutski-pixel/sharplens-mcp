using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for Phase 3 custom tools:
/// get_complexity_metrics, add_null_checks, generate_equality_members, get_type_members_batch
/// </summary>
public class CustomToolTests : RoslynServiceTestBase
{
    #region Complexity Metrics Tests

    [Fact]
    public async Task GetComplexityMetrics_OnMethod_ReturnsAllMetrics()
    {
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.GetComplexityMetricsAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        data["scope"]?.Value<string>().Should().Be("method");

        var metrics = data["metrics"] as JObject;
        metrics.Should().NotBeNull();
        metrics!["cyclomatic"]?.Value<int>().Should().BeGreaterThan(0);
        metrics["loc"]?.Value<int>().Should().BeGreaterThan(0);
        metrics["nesting"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
        metrics["parameters"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
        metrics["cognitive"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task GetComplexityMetrics_OnFile_ReturnsPerMethodBreakdown()
    {
        var result = await Service.GetComplexityMetricsAsync(RoslynServicePath);
        AssertSuccess(result);

        var data = GetData(result);
        data["scope"]?.Value<string>().Should().Be("file");
        data["methods"].Should().NotBeNull();

        var methods = data["methods"] as JArray;
        methods!.Count.Should().BeGreaterThan(10, "RoslynService has many methods");

        if (methods.Count > 0)
        {
            var first = methods[0];
            first["name"].Should().NotBeNull();
            first["metrics"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task GetComplexityMetrics_WithSpecificMetrics_ReturnsOnlyRequested()
    {
        var searchResult = await Service.SearchSymbolsAsync("GetHealthCheckAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.GetComplexityMetricsAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            metrics: new List<string> { "cyclomatic", "loc" });

        AssertSuccess(result);

        var data = GetData(result);
        var metrics = data["metrics"] as JObject;
        metrics!["cyclomatic"]?.Value<int>().Should().BeGreaterThan(0);
        metrics["loc"]?.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetComplexityMetrics_OnComplexMethod_ReturnsHighCyclomatic()
    {
        var searchResult = await Service.SearchSymbolsAsync("SemanticQueryAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.GetComplexityMetricsAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        var metrics = data["metrics"] as JObject;
        var cyclomatic = metrics!["cyclomatic"]?.Value<int>() ?? 0;
        cyclomatic.Should().BeGreaterThan(5,
            "SemanticQueryAsync has many conditional branches — cyclomatic must reflect that");
    }

    #endregion

    #region Null Checks Tests

    [Fact]
    public async Task AddNullChecks_OnLoadSolution_GeneratesGuardForStringPath()
    {
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.AddNullChecksAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        // LoadSolutionAsync takes `string solutionPath` — a null-check preview must
        // mention ArgumentNullException (the standard guard pattern).
        data.ToString().Should().Contain("ArgumentNullException");
    }

    [Fact]
    public async Task AddNullChecks_OnMethodWithNoParameters_ReturnsEmptyOrSuccess()
    {
        var searchResult = await Service.SearchSymbolsAsync("GetHealthCheckAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.AddNullChecksAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        // GetHealthCheckAsync has no nullable reference params, so the preview
        // should report zero generated guards.
        var guardsAdded = data["guardsAdded"]?.Value<int>() ?? data["changesCount"]?.Value<int>() ?? 0;
        guardsAdded.Should().Be(0,
            "a method with no nullable reference parameters needs no guards");
    }

    #endregion

    #region Equality Members Tests

    [Fact]
    public async Task GenerateEqualityMembers_WithOperators_PreviewIncludesEqualsAndOperators()
    {
        var searchResult = await Service.SearchSymbolsAsync("RefactoringTarget", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.GenerateEqualityMembersAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            includeOperators: true,
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        var text = data.ToString();
        text.Should().Contain("Equals");
        text.Should().Contain("GetHashCode");
        text.Should().Contain("operator ==");
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithoutOperators_PreviewExcludesOperators()
    {
        var searchResult = await Service.SearchSymbolsAsync("RefactoringTarget", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.GenerateEqualityMembersAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            includeOperators: false,
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        var text = data.ToString();
        text.Should().Contain("Equals");
        text.Should().Contain("GetHashCode");
        text.Should().NotContain("operator ==");
    }

    #endregion

    #region Batch Lookups Tests

    [Fact]
    public async Task GetTypeMembersBatch_WithMultipleTypes_ReturnsAllResults()
    {
        // Act - correct signature uses List<string>
        var result = await Service.GetTypeMembersBatchAsync(
            new List<string> { "RoslynService", "McpServer" });

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().Be(2);

        foreach (var typeResult in results)
        {
            typeResult["typeName"].Should().NotBeNull();
            typeResult["success"]?.Value<bool>().Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetTypeMembersBatch_WithNonExistentType_ReturnsPartialResults()
    {
        // Act
        var result = await Service.GetTypeMembersBatchAsync(
            new List<string> { "RoslynService", "NonExistentType12345" });

        // Assert
        AssertSuccess(result);
        var data = GetData(result);

        // Batch response has separate results and errors arrays
        data["successCount"]?.Value<int>().Should().Be(1);
        data["errorCount"]?.Value<int>().Should().Be(1);

        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().Be(1);
        results[0]["success"]?.Value<bool>().Should().BeTrue();

        var errors = data["errors"] as JArray;
        errors.Should().NotBeNull();
        errors!.Count.Should().Be(1);
        errors[0]["success"]?.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetTypeMembersBatch_WithEmptyList_ReturnsInvalidParameterError()
    {
        var result = await Service.GetTypeMembersBatchAsync(new List<string>());

        AssertError(result, "INVALID");
    }

    [Fact]
    public async Task GetTypeMembersBatch_WithSingleType_ReturnsResult()
    {
        // Act
        var result = await Service.GetTypeMembersBatchAsync(
            new List<string> { "RoslynService" });

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results!.Count.Should().Be(1);

        var first = results[0];
        first["typeName"]?.Value<string>().Should().Contain("RoslynService");
    }

    #endregion

    #region Method Source Batch Tests

    [Fact]
    public async Task GetMethodSourceBatch_WithMultipleMethods_ReturnsAllSources()
    {
        // Act
        var result = await Service.GetMethodSourceBatchAsync(
            new List<Dictionary<string, object>>
            {
                new() { ["typeName"] = "RoslynService", ["methodName"] = "LoadSolutionAsync" },
                new() { ["typeName"] = "RoslynService", ["methodName"] = "GetHealthCheckAsync" }
            });

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["totalRequested"]?.Value<int>().Should().Be(2);
        data["successCount"]?.Value<int>().Should().Be(2);

        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().Be(2);

        foreach (var methodResult in results)
        {
            methodResult["success"]?.Value<bool>().Should().BeTrue();
            methodResult["data"]?["fullSource"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task GetMethodSourceBatch_WithNonExistentMethod_ReturnsPartialResults()
    {
        // Act
        var result = await Service.GetMethodSourceBatchAsync(
            new List<Dictionary<string, object>>
            {
                new() { ["typeName"] = "RoslynService", ["methodName"] = "LoadSolutionAsync" },
                new() { ["typeName"] = "RoslynService", ["methodName"] = "NonExistentMethod12345" }
            });

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["successCount"]?.Value<int>().Should().Be(1);
        data["errorCount"]?.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task GetMethodSourceBatch_WithEmptyList_ReturnsError()
    {
        // Act
        var result = await Service.GetMethodSourceBatchAsync(
            new List<Dictionary<string, object>>());

        // Assert - should return error for empty list
        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeFalse();
    }

    #endregion

    #region Analyze Method with Outgoing Calls Tests

    [Fact]
    public async Task AnalyzeMethod_WithOutgoingCalls_ReturnsCallsInfo()
    {
        // Act
        var result = await Service.AnalyzeMethodAsync(
            typeName: "RoslynService",
            methodName: "LoadSolutionAsync",
            includeCallers: true,
            includeOutgoingCalls: true);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["signature"].Should().NotBeNull();
        data["outgoingCalls"].Should().NotBeNull();
        data["totalOutgoingCalls"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task AnalyzeMethod_WithoutOutgoingCalls_DoesNotIncludeThem()
    {
        // Act
        var result = await Service.AnalyzeMethodAsync(
            typeName: "RoslynService",
            methodName: "GetHealthCheckAsync",
            includeCallers: true,
            includeOutgoingCalls: false);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["signature"].Should().NotBeNull();
        // outgoingCalls should be null when not requested
        data["outgoingCalls"]?.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task AnalyzeMethod_WithBothCallersAndOutgoing_ReturnsComplete()
    {
        // Act
        var result = await Service.AnalyzeMethodAsync(
            typeName: "RoslynService",
            methodName: "FindTypeByNameAsync",
            includeCallers: true,
            includeOutgoingCalls: true,
            maxCallers: 5,
            maxOutgoingCalls: 10);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["signature"].Should().NotBeNull();
        data["callers"].Should().NotBeNull();
        data["outgoingCalls"].Should().NotBeNull();
        data["callersShown"]?.Value<int>().Should().BeLessThanOrEqualTo(5);
        data["outgoingCallsShown"]?.Value<int>().Should().BeLessThanOrEqualTo(10);
    }

    #endregion
}
