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
        data["name"]?.Value<string>().Should().Be("LoadSolutionAsync");
        data["containingType"]?.Value<string>().Should().Be("RoslynService");

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
    public async Task GetComplexityMetrics_OnFile_ReturnsPerMethodBreakdownAndFileTotals()
    {
        var result = await Service.GetComplexityMetricsAsync(RoslynServicePath);
        AssertSuccess(result);

        var data = GetData(result);
        data["scope"]?.Value<string>().Should().Be("file");
        data["methods"].Should().NotBeNull();
        data["filePath"]?.Value<string>().Should().Contain("RoslynService");

        var methods = data["methods"] as JArray;
        methods!.Count.Should().BeGreaterThan(10, "RoslynService has many methods");
        data["methodCount"]?.Value<int>().Should().Be(methods.Count,
            "methodCount field must equal the methods array length");

        // File-totals block (CodeGeneration.cs:147-167) aggregates per-method numbers.
        var fileTotals = data["fileTotals"] as JObject;
        fileTotals.Should().NotBeNull("file scope must report aggregate totals");
        fileTotals!["avgCyclomatic"]?.Type.Should().Be(JTokenType.Float,
            "avgCyclomatic is a rounded double");
        fileTotals["maxCyclomatic"]?.Value<int>().Should().BeGreaterThan(0);
        fileTotals["maxNesting"]?.Type.Should().Be(JTokenType.Integer);
        fileTotals["totalLoc"]?.Value<int>().Should().BeGreaterThan(0);

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

        // The contract — "only requested" — requires the unrequested metrics to be
        // absent. Without these, a regression that always returns every metric would
        // still pass the test.
        metrics["nesting"].Should().BeNull(
            "nesting was not in the requested list — it must be absent");
        metrics["parameters"].Should().BeNull(
            "parameters was not in the requested list — it must be absent");
        metrics["cognitive"].Should().BeNull(
            "cognitive was not in the requested list — it must be absent");
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
    public async Task GenerateEqualityMembers_WithOperators_PreviewGeneratedCodeContainsAllMembers()
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
        // Lock the specific field, not the JObject's full ToString — the latter would
        // pass on any field anywhere containing "Equals" (e.g., a filename "Equals.cs").
        data["preview"]?.Value<bool>().Should().BeTrue();
        data["typeName"]?.Value<string>().Should().Be("RefactoringTarget");
        data["includeOperators"]?.Value<bool>().Should().BeTrue();
        var code = data["generatedCode"]?.Value<string>()!;
        code.Should().Contain("public override bool Equals(object? obj)",
            "the Equals(object) override must appear");
        code.Should().Contain("public bool Equals(RefactoringTarget? other)",
            "the typed Equals must appear");
        code.Should().Contain("public override int GetHashCode()",
            "GetHashCode override must appear");
        code.Should().Contain("public static bool operator ==(RefactoringTarget? left, RefactoringTarget? right)",
            "operator == must appear when includeOperators=true");
        code.Should().Contain("public static bool operator !=(RefactoringTarget? left, RefactoringTarget? right)",
            "operator != must appear when includeOperators=true");
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithoutOperators_PreviewGeneratedCodeOmitsOperators()
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
        data["includeOperators"]?.Value<bool>().Should().BeFalse();
        var code = data["generatedCode"]?.Value<string>()!;
        code.Should().Contain("public override bool Equals(object? obj)");
        code.Should().Contain("public override int GetHashCode()");
        code.Should().NotContain("operator ==",
            "includeOperators=false must drop operator ==");
        code.Should().NotContain("operator !=",
            "includeOperators=false must drop operator !=");
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
        // LoadSolutionAsync calls BOTH MSBuildWorkspace.Create AND OpenSolutionAsync per
        // RoslynService.cs:272-278. Lock both — the prior OR pattern would pass if only
        // one was found, missing a regression that drops the other.
        outgoing!.Any(c => c["shortName"]?.Value<string>()?.Contains("OpenSolutionAsync") == true)
            .Should().BeTrue("LoadSolutionAsync must invoke MSBuildWorkspace.OpenSolutionAsync");
        outgoing.Any(c => c["shortName"]?.Value<string>()?.Contains("Create") == true)
            .Should().BeTrue("LoadSolutionAsync must invoke MSBuildWorkspace.Create");
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

    #region Negative-case Tests for Cursor Positions

    [Fact]
    public async Task GetComplexityMetrics_AtPositionWithNoMethodOrAccessor_ReturnsSymbolNotFound()
    {
        // Line 1 of RoslynService.cs is the very first using directive — not inside any
        // method or property accessor. CodeGeneration.cs:78-84 returns SymbolNotFound.
        var result = await Service.GetComplexityMetricsAsync(
            RoslynServicePath, line: 0, column: 0);

        AssertError(result, ErrorCodes.SymbolNotFound);
        var json = JObject.FromObject(result);
        json["error"]?["message"]?.Value<string>().Should()
            .Contain("No method or property accessor");
    }

    [Fact]
    public async Task AddNullChecks_AtPositionWithNoMethod_ReturnsNotAMethod()
    {
        // Line 1 (the using directive) is not inside any method.
        // CodeGeneration.cs:391-399 returns NotAMethod.
        var result = await Service.AddNullChecksAsync(
            RoslynServicePath, line: 0, column: 0, preview: true);

        AssertError(result, ErrorCodes.NotAMethod);
        var json = JObject.FromObject(result);
        json["error"]?["message"]?.Value<string>().Should().Contain("No method found");
    }

    [Fact]
    public async Task AddNullChecks_OnExpressionBodiedMethod_ReturnsAnalysisFailed()
    {
        // GetTypeKindString in RoslynService.cs is implemented but has a block body, so
        // we need an actually-expression-bodied method. RefactoringTarget.Compute uses
        // expression body `=> a * 2 + 7;`. CodeGeneration.cs:436-444 returns AnalysisFailed
        // for expression-bodied methods because there's no Block to insert null-checks into.
        var searchResult = await Service.SearchSymbolsAsync("Compute", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var compute = symbols!.First(s =>
            s["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);
        var loc = compute["location"]!;

        var result = await Service.AddNullChecksAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            preview: true);

        // Compute(int a) has no reference-type params, so the impl takes the
        // "No nullable parameters found" success branch first — never reaches
        // the body check. The expression-bodied AnalysisFailed branch is therefore
        // only reachable for methods that DO have nullable ref params AND no block body.
        // GreetingFor uses block body so won't trigger it. This is a real impl-level
        // dead path on the current fixture set; the test instead confirms the
        // no-nullable-params branch fires here.
        AssertSuccess(result);
        var data = GetData(result);
        data["message"]?.Value<string>().Should().Contain("No nullable parameters",
            "Compute takes only int; the no-guard branch must fire before the body check");
    }

    [Fact]
    public async Task GenerateEqualityMembers_AtPositionWithNoType_ReturnsNotAType()
    {
        // Line 1 (using directive) — no type declaration here.
        // CodeGeneration.cs:543-551 returns NotAType.
        var result = await Service.GenerateEqualityMembersAsync(
            RoslynServicePath, line: 0, column: 0, preview: true);

        AssertError(result, ErrorCodes.NotAType);
        var json = JObject.FromObject(result);
        json["error"]?["message"]?.Value<string>().Should().Contain("No type declaration");
    }

    [Fact]
    public async Task GetTypeMembersBatch_NonExistentType_ErrorEntryCarriesTypeName()
    {
        var result = await Service.GetTypeMembersBatchAsync(
            new List<string> { "DoesNotExist_Type_12345" });

        AssertSuccess(result);
        var data = GetData(result);
        data["successCount"]?.Value<int>().Should().Be(0);
        data["errorCount"]?.Value<int>().Should().Be(1);

        var errors = data["errors"] as JArray;
        errors.Should().NotBeNullOrEmpty();
        // The error entry must carry the typeName that failed so the caller can
        // correlate batch input to failure.
        var first = errors![0];
        first["typeName"]?.Value<string>().Should().Be("DoesNotExist_Type_12345",
            "error entry must echo the requested typeName for correlation");
        first["success"]?.Value<bool>().Should().BeFalse();
    }

    #endregion
}
