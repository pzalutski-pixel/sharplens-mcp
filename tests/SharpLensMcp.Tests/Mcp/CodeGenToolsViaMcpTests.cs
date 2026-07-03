using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for the Code Generation category. Assertions are grounded
// in the actual generator output from src/RoslynService.CodeGeneration.cs:
//  - add_null_checks emits `ArgumentNullException.ThrowIfNull({param})` per
//    nullable reference-type parameter
//  - generate_equality_members emits override Equals(object?), Equals(T?),
//    GetHashCode, and optionally operator ==/!= using HashCode.Combine
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

        var data = await CallAndGetDataAsync("add_null_checks", new
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

        // changes[] shape: filePath, insertAfterLine, newCode.
        var changes = (data["changes"] as JArray)!;
        changes.Count.Should().Be(1);
        changes[0]["filePath"]!.Value<string>().Should().NotBeNullOrEmpty();
        // The guard is inserted after the method's opening brace, which (Allman style)
        // is the line immediately below the single-line declaration LocateSymbolAsync found.
        changes[0]["insertAfterLine"]!.Value<int>().Should().Be(line + 1,
            "the guard is inserted after the opening brace, one line below the declaration");
        changes[0]["newCode"]!.Value<string>()!.Should().Contain("ArgumentNullException.ThrowIfNull");
    }

    [Fact]
    public async Task AddNullChecks_OnHealthCheck_ReportsNoNullableParamsWithDistinctShape()
    {
        // GetHealthCheckAsync takes zero parameters — the "no nullable parameters" path
        // the no-params branch returns a different shape: { message, methodName }
        // only; no `preview`, `parametersWithNullChecks`, `generatedCode`, or `changes`.
        var (file, line, col) = await LocateSymbolAsync(
            "GetHealthCheckAsync", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("RoslynService") == true);

        var data = await CallAndGetDataAsync("add_null_checks", new
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
            "the no-params branch omits preview");
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
        // the body-null check fires with "Method has no body".
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var methodLine = Array.FindIndex(lines, l =>
            l.Contains("GetResponseData(object? response)"));
        methodLine.Should().BeGreaterThan(-1);

        var error = await CallAndGetErrorAsync("add_null_checks", new
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
        // the empty-type branch returns a distinct shape ({message, typeName})
        // when the type has no instance fields or properties to compare. We need a
        // type that contains methods only. The MatchesGlobPattern host class
        // RoslynService — wait, RoslynService has fields. Looking for a clean
        // empty case: a static class in the test assembly with no fields.
        var (file, line, col) = await LocateSymbolAsync(
            "TestHelpers", kind: "Class");

        var data = await CallAndGetDataAsync("generate_equality_members", new
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
        // When line/column point to a method, scope="method"
        // and the response carries name, containingType, metrics. Differs from the
        // file-scope response shape (which has methods[] + fileTotals).
        var (file, line, col) = await LocateSymbolAsync(
            "LoadSolutionAsync", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("RoslynService") == true);

        var data = await CallAndGetDataAsync("get_complexity_metrics", new
        {
            filePath = file,
            line,
            column = col
        });
        data["scope"]!.Value<string>().Should().Be("method");
        data["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        data["containingType"]!.Value<string>().Should().Be("RoslynService");
        // The metrics dict must carry all five default metrics. parameters and loc are
        // deterministic on this method; exact cyclomatic/nesting/cognitive values are
        // locked on a fixture (AnalysisToolsViaMcpTests.GetComplexityMetrics_OnBranchyFixtureMethod_*),
        // since this targets live, evolving source.
        var metrics = data["metrics"]!;
        metrics["cyclomatic"].Should().NotBeNull();
        metrics["nesting"].Should().NotBeNull();
        metrics["loc"]!.Value<int>().Should().BeGreaterThan(0, "a multi-line method has positive LOC");
        metrics["parameters"]!.Value<int>().Should().Be(2,
            "LoadSolutionAsync takes solutionPath plus the cancellationToken every tool method carries");
        metrics["cognitive"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetComplexityMetrics_OnExpressionBodiedProperty_ReturnsPropertyScope()
    {
        // A cursor on an expression-bodied property (no accessor list) resolves to the
        // property declaration itself and reports scope="property" with its branch count.
        var (file, line, col) = await LocateSymbolAsync(
            "Sign", kind: "Property",
            r => r["containingType"]?.Value<string>()?.EndsWith("ExpressionBodiedComplexityTarget") == true);

        var data = await CallAndGetDataAsync("get_complexity_metrics", new
        {
            filePath = file,
            line,
            column = col
        });
        data["scope"]!.Value<string>().Should().Be("property");
        data["name"]!.Value<string>().Should().Be("Sign");
        data["metrics"]!["cyclomatic"]!.Value<int>().Should().Be(2, "base 1 plus one ?: branch");
        data["metrics"]!["parameters"]!.Value<int>().Should().Be(0, "a property declares no parameters");
    }

    [Fact]
    public async Task AddNullChecks_OnNonMethodPosition_ReturnsNotAMethod()
    {
        // Position cursor inside a class declaration but not on any method (line 0
        // is the file's first line — typically a `using` directive, definitely not
        // a method). The impl returns NotAMethod when no enclosing
        // MethodDeclarationSyntax is found.
        var error = await CallAndGetErrorAsync("add_null_checks", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 0,
            column = 0,
            preview = true
        }, codeContains: ErrorCodes.NotAMethod);
        error["message"]!.Value<string>().Should().Contain("constructor, operator, or local function",
            "the impl now accepts methods, constructors, operators, and local functions");
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithOperators_EmitsAllExpectedSignatures()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "RefactoringTarget", kind: "Class");

        var data = await CallAndGetDataAsync("generate_equality_members", new
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
        code.Should().Contain("hash.Add(BareCounter);",
            "GetHashCode uses the HashCode builder so every compared member feeds the hash");
        code.Should().Contain("BareCounter == other.BareCounter",
            "the specific equality comparison the generator emits");
        code.Should().Contain("public static bool operator ==(RefactoringTarget? left, RefactoringTarget? right)");
        code.Should().Contain("public static bool operator !=(RefactoringTarget? left, RefactoringTarget? right)");
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithoutOperators_OmitsOperatorOverloadsOnly()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "RefactoringTarget", kind: "Class");

        var data = await CallAndGetDataAsync("generate_equality_members", new
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
