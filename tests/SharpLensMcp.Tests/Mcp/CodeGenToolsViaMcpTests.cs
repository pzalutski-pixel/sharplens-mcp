using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for the Code Generation category. Assertions are grounded
// in the actual generator output from src/RoslynService.CodeGeneration.cs:
//  - add_null_checks emits `ArgumentNullException.ThrowIfNull({param})` per
//    nullable reference-type parameter (CodeGeneration.cs:431)
//  - generate_equality_members emits override Equals(object?), Equals(T?),
//    GetHashCode, and optionally operator ==/!= using HashCode.Combine
//    (CodeGeneration.cs:587-617)
//
// Tightening rule: every accessor uses `!.Value<T>()`.
public class CodeGenToolsViaMcpTests : McpTestBase
{
    public CodeGenToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AddNullChecks_OnLoadSolution_GeneratesGuardForSolutionPathParam()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "LoadSolutionAsync", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("RoslynService") == true);

        var data = await CallAndGetDataAsync("roslyn:add_null_checks", new
        {
            filePath = file, line, column = col, preview = true
        });
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["methodName"]!.Value<string>().Should().Be("LoadSolutionAsync");

        var nullableParams = (data["parametersWithNullChecks"] as JArray)!;
        nullableParams.Select(p => p.Value<string>()).Should().BeEquivalentTo(new[] { "solutionPath" },
            "LoadSolutionAsync has exactly one reference-type parameter");

        // The generator uses the modern .NET 6+ static guard, not the old throw-new form.
        data["generatedCode"]!.Value<string>()!.Should().Contain(
            "ArgumentNullException.ThrowIfNull(solutionPath)");

        // changes[] shape (CodeGeneration.cs:463-469): filePath, insertAfterLine, newCode.
        var changes = (data["changes"] as JArray)!;
        changes.Count.Should().Be(1);
        changes[0]["filePath"]!.Value<string>().Should().NotBeNullOrEmpty();
        changes[0]["insertAfterLine"]!.Value<int>().Should().BeGreaterOrEqualTo(0,
            "insertAfterLine is the 0-indexed line of the method's opening brace");
        changes[0]["newCode"]!.Value<string>()!.Should().Contain("ArgumentNullException.ThrowIfNull");
    }

    [Fact]
    public async Task AddNullChecks_OnHealthCheck_ReportsNoNullableParamsWithDistinctShape()
    {
        // GetHealthCheckAsync takes zero parameters — the "no nullable parameters" path
        // (CodeGeneration.cs:416-425) returns a different shape: { message, methodName }
        // only; no `preview`, `parametersWithNullChecks`, `generatedCode`, or `changes`.
        var (file, line, col) = await LocateSymbolAsync(
            "GetHealthCheckAsync", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("RoslynService") == true);

        var data = await CallAndGetDataAsync("roslyn:add_null_checks", new
        {
            filePath = file, line, column = col, preview = true
        });
        data["methodName"]!.Value<string>().Should().Be("GetHealthCheckAsync");
        data["message"]!.Value<string>()!.Should().Contain("No nullable parameters",
            "GetHealthCheckAsync has zero parameters, so no guards can be generated");

        // Lock the absence of the with-params response fields — a regression that
        // unified the two branches into one shape would silently let consumers
        // process a stale `parametersWithNullChecks: []` as "has nullable params".
        data["preview"].Should().BeNull(
            "the no-params branch omits preview (CodeGeneration.cs:418-424)");
        data["parametersWithNullChecks"].Should().BeNull();
        data["generatedCode"].Should().BeNull();
        data["changes"].Should().BeNull();
    }

    [Fact]
    public async Task AddNullChecks_OnExpressionBodiedMethodWithRefParam_ReturnsAnalysisFailed()
    {
        // GetResponseData in RoslynService.cs is expression-bodied with a reference
        // parameter (object? response). The nullable-params filter (CodeGeneration.cs:
        // 402-414) includes it, so the count==0 early-return is skipped, and the
        // body-null check (CodeGeneration.cs:434-444) fires with "Method has no body".
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var methodLine = Array.FindIndex(lines, l =>
            l.Contains("GetResponseData(object? response)"));
        methodLine.Should().BeGreaterThan(-1);

        var error = await CallAndGetErrorAsync("roslyn:add_null_checks", new
        {
            filePath = Fixture.RoslynServicePath,
            line = methodLine,
            column = 30,
            preview = true
        }, codeContains: ErrorCodes.AnalysisFailed);
        error["message"]!.Value<string>()!.Should().Contain("no body");
    }

    [Fact]
    public async Task GenerateEqualityMembers_OnTypeWithNoFieldsOrProperties_ReturnsNoMembersMessage()
    {
        // CodeGeneration.cs:575-583 returns a distinct shape ({message, typeName})
        // when the type has no instance fields or properties to compare. We need a
        // type that contains methods only. The MatchesGlobPattern host class
        // RoslynService — wait, RoslynService has fields. Looking for a clean
        // empty case: a static class in the test assembly with no fields.
        var (file, line, col) = await LocateSymbolAsync(
            "TestHelpers", kind: "Class");

        var data = await CallAndGetDataAsync("roslyn:generate_equality_members", new
        {
            filePath = file, line, column = col,
            preview = true
        });
        data["typeName"]!.Value<string>().Should().Contain("TestHelpers");
        data["message"]!.Value<string>()!.Should().Contain("No instance fields or properties",
            "the empty-members branch returns a distinct message-bearing response");
        // The with-members response fields must be absent on this branch.
        data["preview"].Should().BeNull();
        data["generatedCode"].Should().BeNull();
        data["membersCompared"].Should().BeNull();
    }

    [Fact]
    public async Task GetComplexityMetrics_OnLoadSolutionAsyncMethod_ReturnsMethodScopeResponse()
    {
        // CodeGeneration.cs:59-126: when line/column point to a method, scope="method"
        // and the response carries name, containingType, metrics. Differs from the
        // file-scope response shape (which has methods[] + fileTotals).
        var (file, line, col) = await LocateSymbolAsync(
            "LoadSolutionAsync", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("RoslynService") == true);

        var data = await CallAndGetDataAsync("roslyn:get_complexity_metrics", new
        {
            filePath = file,
            line,
            column = col
        });
        data["scope"]!.Value<string>().Should().Be("method");
        data["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        data["containingType"]!.Value<string>().Should().Be("RoslynService");
        // metrics dict carries all 5 default-requested metrics (CodeGeneration.cs:54-56).
        var metrics = data["metrics"]!;
        metrics["cyclomatic"]!.Value<int>().Should().BeGreaterOrEqualTo(1);
        metrics["nesting"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        metrics["loc"]!.Value<int>().Should().BeGreaterThan(0);
        metrics["parameters"]!.Value<int>().Should().Be(1,
            "LoadSolutionAsync takes one parameter (solutionPath)");
        metrics["cognitive"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task AddNullChecks_OnNonMethodPosition_ReturnsNotAMethod()
    {
        // Position cursor inside a class declaration but not on any method (line 0
        // is the file's first line — typically a `using` directive, definitely not
        // a method). CodeGeneration.cs:391-399 returns NotAMethod when no enclosing
        // MethodDeclarationSyntax is found.
        var error = await CallAndGetErrorAsync("roslyn:add_null_checks", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 0,
            column = 0,
            preview = true
        }, codeContains: ErrorCodes.NotAMethod);
        error["message"]!.Value<string>().Should().Contain("No method found",
            "the impl's documented message at CodeGeneration.cs:395");
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithOperators_EmitsAllExpectedSignatures()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "RefactoringTarget", kind: "Class");

        var data = await CallAndGetDataAsync("roslyn:generate_equality_members", new
        {
            filePath = file, line, column = col,
            includeOperators = true, preview = true
        });
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["typeName"]!.Value<string>().Should().Be("RefactoringTarget");
        data["includeOperators"]!.Value<bool>().Should().BeTrue();

        // RefactoringTarget has exactly one instance field: BareCounter. No
        // settable instance properties. So membersCompared must be exactly that.
        var members = (data["membersCompared"] as JArray)!;
        members.Select(m => m.Value<string>()).Should().BeEquivalentTo(new[] { "BareCounter" },
            "RefactoringTarget exposes only the BareCounter public field");

        var code = data["generatedCode"]!.Value<string>()!;
        code.Should().Contain("public override bool Equals(object? obj)");
        code.Should().Contain("public bool Equals(RefactoringTarget? other)");
        code.Should().Contain("public override int GetHashCode()");
        code.Should().Contain("HashCode.Combine(BareCounter)");
        code.Should().Contain("BareCounter == other.BareCounter",
            "the specific equality comparison the generator emits at CodeGeneration.cs:598");
        code.Should().Contain("public static bool operator ==(RefactoringTarget? left, RefactoringTarget? right)");
        code.Should().Contain("public static bool operator !=(RefactoringTarget? left, RefactoringTarget? right)");
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithoutOperators_OmitsOperatorOverloadsOnly()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "RefactoringTarget", kind: "Class");

        var data = await CallAndGetDataAsync("roslyn:generate_equality_members", new
        {
            filePath = file, line, column = col,
            includeOperators = false, preview = true
        });
        data["includeOperators"]!.Value<bool>().Should().BeFalse();

        var code = data["generatedCode"]!.Value<string>()!;
        code.Should().Contain("public override bool Equals(object? obj)");
        code.Should().Contain("public override int GetHashCode()");
        code.Should().NotContain("operator ==");
        code.Should().NotContain("operator !=");
    }
}
