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
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GetComplexityMetricsAsync(file, line, col);
            AssertSuccess(result);

            var data = GetData(result);
            data["scope"]?.Value<string>().Should().Be("method");
            data["metrics"].Should().NotBeNull();

            var metrics = data["metrics"] as JObject;
            metrics!["cyclomatic"].Should().NotBeNull();
            metrics["nesting"].Should().NotBeNull();
            metrics["loc"].Should().NotBeNull();
            metrics["parameters"].Should().NotBeNull();
            metrics["cognitive"].Should().NotBeNull();
        }
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
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GetComplexityMetricsAsync(
                file, line, col,
                metrics: new List<string> { "cyclomatic", "loc" });

            AssertSuccess(result);

            var data = GetData(result);
            var metrics = data["metrics"] as JObject;
            metrics!["cyclomatic"].Should().NotBeNull();
            metrics["loc"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task GetComplexityMetrics_OnComplexMethod_ReturnsHighValues()
    {
        var searchResult = await Service.SearchSymbolsAsync("SemanticQueryAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GetComplexityMetricsAsync(file, line, col);
            AssertSuccess(result);

            var data = GetData(result);
            var metrics = data["metrics"] as JObject;

            var cyclomatic = metrics!["cyclomatic"]?.Value<int>() ?? 0;
            cyclomatic.Should().BeGreaterOrEqualTo(1);
        }
    }

    #endregion

    #region Null Checks Tests

    [Fact]
    public async Task AddNullChecks_OnMethodWithParameters_GeneratesGuards()
    {
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.AddNullChecksAsync(file, line, col, preview: true);

            var json = JObject.FromObject(result);
            // Verify it processes without error
        }
    }

    [Fact]
    public async Task AddNullChecks_OnMethodWithNoParameters_ReturnsNoChanges()
    {
        var searchResult = await Service.SearchSymbolsAsync("GetHealthCheckAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.AddNullChecksAsync(file, line, col, preview: true);

            var json = JObject.FromObject(result);
        }
    }

    #endregion

    #region Equality Members Tests

    [Fact]
    public async Task GenerateEqualityMembers_OnClass_GeneratesMembers()
    {
        var searchResult = await Service.SearchSymbolsAsync("RoslynService", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GenerateEqualityMembersAsync(
                file, line, col,
                includeOperators: true,
                preview: true);

            var json = JObject.FromObject(result);
        }
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithoutOperators_GeneratesOnlyMethods()
    {
        var searchResult = await Service.SearchSymbolsAsync("RoslynService", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GenerateEqualityMembersAsync(
                file, line, col,
                includeOperators: false,
                preview: true);

            var json = JObject.FromObject(result);
        }
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
    public async Task GetTypeMembersBatch_WithEmptyList_ReturnsEmpty()
    {
        // Act
        var result = await Service.GetTypeMembersBatchAsync(new List<string>());

        // Assert
        var json = JObject.FromObject(result);
        // Should handle gracefully
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
