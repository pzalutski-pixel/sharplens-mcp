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
        // LoadSolutionAsync has exactly one parameter (solutionPath) per RoslynService.cs:253.
        metrics!["parameters"]?.Value<int>().Should().Be(1);
        // Body contains an if-guard and a registration callback — cyclomatic is at least 2.
        metrics["cyclomatic"]?.Value<int>().Should().BeGreaterOrEqualTo(2);
        // Method body spans roughly 45 lines (253-298); loc must be well above the noise floor.
        metrics["loc"]?.Value<int>().Should().BeGreaterThan(10);
        // The remaining metrics must at least be present as integers.
        metrics["nesting"]?.Type.Should().Be(JTokenType.Integer);
        metrics["cognitive"]?.Type.Should().Be(JTokenType.Integer);
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

        var first = methods[0];
        first["name"]?.Value<string>().Should().NotBeNullOrEmpty(
            "every per-method entry must report its method name");
        var firstMetrics = first["metrics"] as JObject;
        firstMetrics.Should().NotBeNull(
            "every per-method entry must carry its own metrics block");
        firstMetrics!["cyclomatic"]?.Type.Should().Be(JTokenType.Integer,
            "cyclomatic is always reported as an integer");
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
        // LoadSolutionAsync takes one reference-type parameter, `string solutionPath`.
        data["methodName"]?.Value<string>().Should().Be("LoadSolutionAsync");
        var paramsWithChecks = data["parametersWithNullChecks"] as JArray;
        paramsWithChecks.Should().NotBeNullOrEmpty(
            "solutionPath is a reference-type param and must appear in the guard list");
        paramsWithChecks!.Select(p => p.Value<string>()).Should().Contain("solutionPath");
        data["generatedCode"]?.Value<string>().Should()
            .Contain("ArgumentNullException.ThrowIfNull(solutionPath)",
                "the standard guard line for solutionPath must appear verbatim in generated code");
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
        // GetHealthCheckAsync has no nullable reference params. The impl returns a
        // success with `message = "No nullable parameters found..."` and the method
        // name, but no `parametersWithNullChecks` array — see CodeGeneration.cs:418-424.
        data["methodName"]?.Value<string>().Should().Be("GetHealthCheckAsync");
        data["message"]?.Value<string>().Should().Contain("No nullable parameters",
            "a parameter-less method must trigger the 'nothing to guard' branch");
        data["parametersWithNullChecks"].Should().BeNull(
            "the no-guards branch must not emit a parametersWithNullChecks array");
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
    public async Task GetTypeMembersBatch_WithMultipleTypes_BothTypesReturnedByName()
    {
        var result = await Service.GetTypeMembersBatchAsync(
            new List<string> { "RoslynService", "McpServer" });

        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().Be(2);

        var typeNames = results.Select(r => r["typeName"]?.Value<string>()).ToList();
        typeNames.Should().Contain("RoslynService");
        typeNames.Should().Contain("McpServer");
        foreach (var typeResult in results)
        {
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

        AssertError(result, ErrorCodes.InvalidParameter);
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
    public async Task GetMethodSourceBatch_WithMultipleMethods_EachSourceContainsItsMethodName()
    {
        var result = await Service.GetMethodSourceBatchAsync(
            new List<Dictionary<string, object>>
            {
                new() { ["typeName"] = "RoslynService", ["methodName"] = "LoadSolutionAsync" },
                new() { ["typeName"] = "RoslynService", ["methodName"] = "GetHealthCheckAsync" }
            });

        AssertSuccess(result);
        var data = GetData(result);
        data["totalRequested"]?.Value<int>().Should().Be(2);
        data["successCount"]?.Value<int>().Should().Be(2);

        var results = data["results"] as JArray;
        results!.Count.Should().Be(2);
        // Each result's source must contain its specific method name.
        results[0]["data"]?["fullSource"]?.Value<string>().Should().Contain("LoadSolutionAsync");
        results[1]["data"]?["fullSource"]?.Value<string>().Should().Contain("GetHealthCheckAsync");
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
    public async Task GetMethodSourceBatch_WithEmptyList_ReturnsInvalidParameter()
    {
        var result = await Service.GetMethodSourceBatchAsync(
            new List<Dictionary<string, object>>());
        AssertError(result, ErrorCodes.InvalidParameter);
    }

    #endregion

    #region Analyze Method with Outgoing Calls Tests

    [Fact]
    public async Task AnalyzeMethod_OnLoadSolution_OutgoingCallsIncludeMSBuildWorkspace()
    {
        var result = await Service.AnalyzeMethodAsync(
            typeName: "RoslynService",
            methodName: "LoadSolutionAsync",
            includeCallers: true,
            includeOutgoingCalls: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["signature"]!["name"]?.Value<string>().Should().Be("LoadSolutionAsync");
        var outgoing = data["outgoingCalls"] as JArray;
        outgoing.Should().NotBeNullOrEmpty();
        // LoadSolutionAsync calls MSBuildWorkspace.Create and OpenSolutionAsync per RoslynService.cs:272-278.
        outgoing!.Any(c => c["shortName"]?.Value<string>()?.Contains("OpenSolutionAsync") == true
                       || c["shortName"]?.Value<string>()?.Contains("Create") == true)
            .Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeMethod_WithoutOutgoingCalls_OmitsThem()
    {
        var result = await Service.AnalyzeMethodAsync(
            typeName: "RoslynService",
            methodName: "GetHealthCheckAsync",
            includeCallers: true,
            includeOutgoingCalls: false);

        AssertSuccess(result);
        var data = GetData(result);
        data["signature"]!["name"]?.Value<string>().Should().Be("GetHealthCheckAsync");
        // outgoingCalls should be null when not requested
        data["outgoingCalls"]?.Type.Should().Be(JTokenType.Null);
        data["outgoingCallsShown"]?.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task AnalyzeMethod_WithBothCallersAndOutgoing_RespectsMaxCaps()
    {
        var result = await Service.AnalyzeMethodAsync(
            typeName: "RoslynService",
            methodName: "FindTypeByNameAsync",
            includeCallers: true,
            includeOutgoingCalls: true,
            maxCallers: 5,
            maxOutgoingCalls: 10);

        AssertSuccess(result);
        var data = GetData(result);
        data["signature"]!["name"]?.Value<string>().Should().Be("FindTypeByNameAsync");
        var callers = data["callers"] as JArray;
        callers!.Count.Should().BeLessThanOrEqualTo(5);
        var outgoing = data["outgoingCalls"] as JArray;
        outgoing!.Count.Should().BeLessThanOrEqualTo(10);
        data["callersShown"]?.Value<int>().Should().Be(callers.Count);
        data["outgoingCallsShown"]?.Value<int>().Should().Be(outgoing.Count);
    }

    #endregion
}
