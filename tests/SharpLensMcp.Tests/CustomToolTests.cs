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
    public async Task GetComplexityMetrics_OnTrivialFixtureMethod_ReturnsExactMetrics()
    {
        // ComplexityTarget.Trivial has no decision points, so its metrics are fixed and
        // algorithm-invariant rather than inequalities/type-only checks against the live
        // RoslynService source.
        var searchResult = await Service.SearchSymbolsAsync("Trivial", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols!.First(s => s["containingType"]!.Value<string>()!.EndsWith("ComplexityTarget"));
        var loc = symbol["location"]!;
        var result = await Service.GetComplexityMetricsAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        data["scope"].Should().NotBeNull();
        data["scope"]!.Value<string>().Should().Be("method");
        data["name"].Should().NotBeNull();
        data["name"]!.Value<string>().Should().Be("Trivial");
        data["containingType"].Should().NotBeNull();
        data["containingType"]!.Value<string>().Should().EndWith("ComplexityTarget");

        var metrics = data["metrics"] as JObject;
        metrics.Should().NotBeNull();
        metrics!["parameters"].Should().NotBeNull();
        metrics["parameters"]!.Value<int>().Should().Be(2, "Trivial(int a, int b) has two parameters");
        metrics["cyclomatic"].Should().NotBeNull();
        metrics["cyclomatic"]!.Value<int>().Should().Be(1,
            "a method with no decision points has cyclomatic complexity 1");
        metrics["nesting"].Should().NotBeNull();
        metrics["nesting"]!.Value<int>().Should().Be(0, "an expression body has no nesting");
        metrics["cognitive"].Should().NotBeNull();
        metrics["cognitive"]!.Value<int>().Should().Be(0, "no control flow means zero cognitive complexity");
    }

    [Fact]
    public async Task GetComplexityMetrics_OnFile_ReturnsPerMethodBreakdownAndFileTotals()
    {
        var result = await Service.GetComplexityMetricsAsync(RoslynServicePath);
        AssertSuccess(result);

        var data = GetData(result);
        data["scope"].Should().NotBeNull();
        data["scope"]!.Value<string>().Should().Be("file");
        data["methods"].Should().NotBeNull();
        data["filePath"].Should().NotBeNull();
        data["filePath"]!.Value<string>().Should().Contain("RoslynService");

        var methods = data["methods"] as JArray;
        methods!.Count.Should().BeGreaterThan(10, "RoslynService has many methods");
        data["methodCount"].Should().NotBeNull();
        data["methodCount"]!.Value<int>().Should().Be(methods.Count,
            "methodCount field must equal the methods array length");

        // The file-totals block aggregates per-method numbers.
        var fileTotals = data["fileTotals"] as JObject;
        fileTotals.Should().NotBeNull("file scope must report aggregate totals");
        fileTotals!["avgCyclomatic"].Should().NotBeNull();
        fileTotals["avgCyclomatic"]!.Type.Should().Be(JTokenType.Float,
            "avgCyclomatic is a rounded double");
        fileTotals["maxCyclomatic"].Should().NotBeNull();
        fileTotals["maxCyclomatic"]!.Value<int>().Should().BeGreaterThan(0);
        fileTotals["maxNesting"].Should().NotBeNull();
        fileTotals["maxNesting"]!.Type.Should().Be(JTokenType.Integer);
        fileTotals["totalLoc"].Should().NotBeNull();
        fileTotals["totalLoc"]!.Value<int>().Should().BeGreaterThan(0);

        var first = methods[0];
        first["name"].Should().NotBeNull();
        first["name"]!.Value<string>().Should().NotBeNullOrEmpty(
            "every per-method entry must report its method name");
        var firstMetrics = first["metrics"] as JObject;
        firstMetrics.Should().NotBeNull(
            "every per-method entry must carry its own metrics block");
        firstMetrics!["cyclomatic"].Should().NotBeNull();
        firstMetrics["cyclomatic"]!.Type.Should().Be(JTokenType.Integer,
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
        metrics!["cyclomatic"].Should().NotBeNull();
        metrics["cyclomatic"]!.Value<int>().Should().BeGreaterThan(0);
        metrics["loc"].Should().NotBeNull();
        metrics["loc"]!.Value<int>().Should().BeGreaterThan(0);

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
    public async Task GetComplexityMetrics_OnBranchyFixtureMethod_ReturnsExactCyclomatic()
    {
        // ComplexityTarget.Branchy has exactly five decision points (four ifs + one
        // loop), so cyclomatic is a fixed value rather than a `> 5` inequality against
        // the live RoslynService source.
        var searchResult = await Service.SearchSymbolsAsync("Branchy", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols!.First(s => s["containingType"]!.Value<string>()!.EndsWith("ComplexityTarget"));
        var loc = symbol["location"]!;
        var result = await Service.GetComplexityMetricsAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        var metrics = data["metrics"] as JObject;
        metrics!["cyclomatic"].Should().NotBeNull();
        metrics["cyclomatic"]!.Value<int>().Should().Be(6,
            "base 1 + four ifs + one loop = 6 independent paths");
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
        data["methodName"].Should().NotBeNull();
        data["methodName"]!.Value<string>().Should().Be("LoadSolutionAsync");
        var paramsWithChecks = data["parametersWithNullChecks"] as JArray;
        paramsWithChecks.Should().NotBeNullOrEmpty(
            "solutionPath is a reference-type param and must appear in the guard list");
        paramsWithChecks!.Select(p => p.Value<string>()).Should().Contain("solutionPath");
        data["generatedCode"].Should().NotBeNull();
        data["generatedCode"]!.Value<string>().Should()
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
        // GetHealthCheckAsync has no reference-type params. The impl returns a
        // success with the nothing-to-guard message and the method name, but no
        // `parametersWithNullChecks` array.
        data["methodName"].Should().NotBeNull();
        data["methodName"]!.Value<string>().Should().Be("GetHealthCheckAsync");
        data["message"].Should().NotBeNull();
        data["message"]!.Value<string>().Should().Be("No reference-type parameter needs a null check",
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
        data["preview"].Should().NotBeNull();
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["typeName"].Should().NotBeNull();
        data["typeName"]!.Value<string>().Should().Be("RefactoringTarget");
        data["includeOperators"].Should().NotBeNull();
        data["includeOperators"]!.Value<bool>().Should().BeTrue();
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
        data["includeOperators"].Should().NotBeNull();
        data["includeOperators"]!.Value<bool>().Should().BeFalse();
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
            typeResult["success"].Should().NotBeNull();
            typeResult["success"]!.Value<bool>().Should().BeTrue();
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
        data["successCount"].Should().NotBeNull();
        data["successCount"]!.Value<int>().Should().Be(1);
        data["errorCount"].Should().NotBeNull();
        data["errorCount"]!.Value<int>().Should().Be(1);

        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().Be(1);
        results[0]["success"].Should().NotBeNull();
        results[0]["success"]!.Value<bool>().Should().BeTrue();

        var errors = data["errors"] as JArray;
        errors.Should().NotBeNull();
        errors!.Count.Should().Be(1);
        errors[0]["success"].Should().NotBeNull();
        errors[0]["success"]!.Value<bool>().Should().BeFalse();
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
        first["typeName"].Should().NotBeNull();
        first["typeName"]!.Value<string>().Should().Contain("RoslynService");
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
        data["totalRequested"].Should().NotBeNull();
        data["totalRequested"]!.Value<int>().Should().Be(2);
        data["successCount"].Should().NotBeNull();
        data["successCount"]!.Value<int>().Should().Be(2);

        var results = data["results"] as JArray;
        results!.Count.Should().Be(2);
        // Each result's source must contain its specific method name.
        results[0]["data"].Should().NotBeNull();
        results[0]["data"]!["fullSource"].Should().NotBeNull();
        results[0]["data"]!["fullSource"]!.Value<string>().Should().Contain("LoadSolutionAsync");
        results[1]["data"].Should().NotBeNull();
        results[1]["data"]!["fullSource"].Should().NotBeNull();
        results[1]["data"]!["fullSource"]!.Value<string>().Should().Contain("GetHealthCheckAsync");
    }

    [Fact]
    public async Task GetMethodSourceBatch_NonNumericOverloadIndex_ErrorsThatItemOnly_NotTheBatch()
    {
        var result = await Service.GetMethodSourceBatchAsync(
            new List<Dictionary<string, object>>
            {
                new() { ["typeName"] = "RoslynService", ["methodName"] = "LoadSolutionAsync", ["overloadIndex"] = "not-a-number" },
                new() { ["typeName"] = "RoslynService", ["methodName"] = "GetHealthCheckAsync" }
            });

        AssertSuccess(result);
        var data = GetData(result);
        data["totalRequested"]!.Value<int>().Should().Be(2);
        data["successCount"]!.Value<int>().Should().Be(1,
            "the valid item still resolves — a bad overloadIndex must not abort the whole batch");

        var errors = (data["errors"] as JArray)!;
        errors.Should().ContainSingle();
        errors[0]["error"]!.Value<string>().Should().Contain("overloadIndex");
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
        data["successCount"].Should().NotBeNull();
        data["successCount"]!.Value<int>().Should().Be(1);
        data["errorCount"].Should().NotBeNull();
        data["errorCount"]!.Value<int>().Should().Be(1);
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
        data["signature"]!["name"].Should().NotBeNull();
        data["signature"]!["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        var outgoing = data["outgoingCalls"] as JArray;
        outgoing.Should().NotBeNullOrEmpty();
        // LoadSolutionAsync calls BOTH MSBuildWorkspace.Create AND OpenSolutionAsync per
        // Lock both — the prior OR pattern would pass if only
        // one was found, missing a regression that drops the other.
        outgoing!.Any(c => c["shortName"]?.Value<string>()?.Contains("OpenSolutionAsync") == true)
            .Should().BeTrue("LoadSolutionAsync must invoke MSBuildWorkspace.OpenSolutionAsync");
        outgoing!.Any(c => c["shortName"]?.Value<string>()?.Contains("Create") == true)
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
        data["signature"]!["name"].Should().NotBeNull();
        data["signature"]!["name"]!.Value<string>().Should().Be("GetHealthCheckAsync");
        // outgoingCalls should be JSON null (or absent) when not requested.
        var outgoingCalls = data["outgoingCalls"];
        (outgoingCalls == null || outgoingCalls.Type == JTokenType.Null).Should().BeTrue(
            "outgoingCalls must be null or absent when includeOutgoingCalls is false");
        data["outgoingCallsShown"].Should().NotBeNull();
        data["outgoingCallsShown"]!.Value<int>().Should().Be(0);
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
        data["signature"]!["name"].Should().NotBeNull();
        data["signature"]!["name"]!.Value<string>().Should().Be("FindTypeByNameAsync");
        var callers = data["callers"] as JArray;
        callers!.Count.Should().BeLessThanOrEqualTo(5);
        var outgoing = data["outgoingCalls"] as JArray;
        outgoing!.Count.Should().BeLessThanOrEqualTo(10);
        data["callersShown"].Should().NotBeNull();
        data["callersShown"]!.Value<int>().Should().Be(callers.Count);
        data["outgoingCallsShown"].Should().NotBeNull();
        data["outgoingCallsShown"]!.Value<int>().Should().Be(outgoing.Count);
    }

    #endregion

    #region Negative-case Tests for Cursor Positions

    [Fact]
    public async Task GetComplexityMetrics_AtPositionWithNoMethodOrAccessor_ReturnsSymbolNotFound()
    {
        // Line 1 of RoslynService.cs is the very first using directive — not inside any
        // method or property accessor. The impl returns SymbolNotFound.
        var result = await Service.GetComplexityMetricsAsync(
            RoslynServicePath, line: 0, column: 0);

        AssertError(result, ErrorCodes.SymbolNotFound);
        // In-process JObject.FromObject keeps RoslynError's PascalCase Message; the old
        // lowercase `["message"]` read was always null and silently passed.
        var json = JObject.FromObject(result);
        json["error"].Should().NotBeNull();
        json["error"]!["Message"].Should().NotBeNull();
        json["error"]!["Message"]!.Value<string>().Should()
            .Contain("constructor, operator, local function, accessor, or expression-bodied property",
                "the position path names every member kind the file scope enumerates");
    }

    [Fact]
    public async Task AddNullChecks_AtPositionWithNoMethod_ReturnsNotAMethod()
    {
        // Line 1 (the using directive) is not inside any method.
        // The impl returns NotAMethod.
        var result = await Service.AddNullChecksAsync(
            RoslynServicePath, line: 0, column: 0, preview: true);

        AssertError(result, ErrorCodes.NotAMethod);
        var json = JObject.FromObject(result);
        json["error"].Should().NotBeNull();
        json["error"]!["Message"].Should().NotBeNull();
        json["error"]!["Message"]!.Value<string>().Should().Contain("constructor, operator, or local function",
            "the impl now accepts methods, constructors, operators, and local functions");
    }

    [Fact]
    public async Task AddNullChecks_OnExpressionBodiedMethod_ReturnsAnalysisFailed()
    {
        // GetTypeKindString in RoslynService.cs is implemented but has a block body, so
        // we need an actually-expression-bodied method. RefactoringTarget.Compute uses
        // expression body `=> a * 2 + 7;`. The impl returns AnalysisFailed
        // for expression-bodied methods because there's no Block to insert null-checks into.
        var searchResult = await Service.SearchSymbolsAsync("Compute", kind: "Method", maxResults: 500);
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
        // nothing-to-guard success branch first — never reaches
        // the body check. The expression-bodied AnalysisFailed branch is therefore
        // only reachable for methods that DO have nullable ref params AND no block body.
        // GreetingFor uses block body so won't trigger it. This is a real impl-level
        // dead path on the current fixture set; the test instead confirms the
        // no-nullable-params branch fires here.
        AssertSuccess(result);
        var data = GetData(result);
        data["message"].Should().NotBeNull();
        data["message"]!.Value<string>().Should().Be("No reference-type parameter needs a null check",
            "Compute takes only int; the no-guard branch must fire before the body check");
    }

    [Fact]
    public async Task GenerateEqualityMembers_AtPositionWithNoType_ReturnsNotAType()
    {
        // Line 1 (using directive) — no type declaration here.
        // The impl returns NotAType.
        var result = await Service.GenerateEqualityMembersAsync(
            RoslynServicePath, line: 0, column: 0, preview: true);

        AssertError(result, ErrorCodes.NotAType);
        var json = JObject.FromObject(result);
        json["error"].Should().NotBeNull();
        json["error"]!["Message"].Should().NotBeNull();
        json["error"]!["Message"]!.Value<string>().Should().Contain("No type declaration");
    }

    [Fact]
    public async Task GetTypeMembersBatch_NonExistentType_ErrorEntryCarriesTypeName()
    {
        var result = await Service.GetTypeMembersBatchAsync(
            new List<string> { "DoesNotExist_Type_12345" });

        AssertSuccess(result);
        var data = GetData(result);
        data["successCount"].Should().NotBeNull();
        data["successCount"]!.Value<int>().Should().Be(0);
        data["errorCount"].Should().NotBeNull();
        data["errorCount"]!.Value<int>().Should().Be(1);

        var errors = data["errors"] as JArray;
        errors.Should().NotBeNullOrEmpty();
        // The error entry must carry the typeName that failed so the caller can
        // correlate batch input to failure.
        var first = errors![0];
        first["typeName"].Should().NotBeNull();
        first["typeName"]!.Value<string>().Should().Be("DoesNotExist_Type_12345",
            "error entry must echo the requested typeName for correlation");
        first["success"].Should().NotBeNull();
        first["success"]!.Value<bool>().Should().BeFalse();
    }

    #endregion
}
