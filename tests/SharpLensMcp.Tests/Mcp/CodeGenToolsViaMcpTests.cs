using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Code Generation category (2 tools). Assertions are
// grounded in the actual generator output from RoslynService.CodeGeneration.cs:
//  - add_null_checks emits `ArgumentNullException.ThrowIfNull({param})` for
//    each nullable reference-type parameter
//  - generate_equality_members emits override Equals(object?), Equals(T?),
//    GetHashCode, and optionally operator ==/!= using HashCode.Combine
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
        data["preview"]?.Value<bool>().Should().BeTrue();
        data["methodName"]?.Value<string>().Should().Be("LoadSolutionAsync");

        var nullableParams = data["parametersWithNullChecks"] as JArray;
        nullableParams.Should().NotBeNull();
        nullableParams!.Select(p => p.Value<string>()).Should().BeEquivalentTo(new[] { "solutionPath" },
            "LoadSolutionAsync has exactly one reference-type parameter");

        // The generator uses the modern .NET 6+ static guard, not the old throw-new form.
        data["generatedCode"]?.Value<string>().Should().Contain(
            "ArgumentNullException.ThrowIfNull(solutionPath)");

        var changes = data["changes"] as JArray;
        changes.Should().NotBeNull();
        changes!.Count.Should().Be(1);
        changes[0]["filePath"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AddNullChecks_OnHealthCheck_ReportsNoNullableParams()
    {
        // GetHealthCheckAsync takes zero parameters — the "no nullable parameters" path
        // (CodeGeneration.cs:417) returns a different shape with a `message` field
        // and no `parametersWithNullChecks`/`changes` fields.
        var (file, line, col) = await LocateSymbolAsync(
            "GetHealthCheckAsync", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("RoslynService") == true);

        var data = await CallAndGetDataAsync("roslyn:add_null_checks", new
        {
            filePath = file, line, column = col, preview = true
        });
        data["methodName"]?.Value<string>().Should().Be("GetHealthCheckAsync");
        data["message"]?.Value<string>().Should().Contain("No nullable parameters",
            "GetHealthCheckAsync has zero parameters, so no guards can be generated");
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
        data["preview"]?.Value<bool>().Should().BeTrue();
        data["typeName"]?.Value<string>().Should().Be("RefactoringTarget");
        data["includeOperators"]?.Value<bool>().Should().BeTrue();

        var members = data["membersCompared"] as JArray;
        members.Should().NotBeNull();
        members!.Select(m => m.Value<string>()).Should().Contain("BareCounter",
            "RefactoringTarget exposes the BareCounter public field");

        var code = data["generatedCode"]!.Value<string>()!;
        code.Should().Contain("public override bool Equals(object? obj)");
        code.Should().Contain("public bool Equals(RefactoringTarget? other)");
        code.Should().Contain("public override int GetHashCode()");
        code.Should().Contain("HashCode.Combine(BareCounter)");
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
        data["includeOperators"]?.Value<bool>().Should().BeFalse();

        var code = data["generatedCode"]!.Value<string>()!;
        code.Should().Contain("public override bool Equals(object? obj)");
        code.Should().Contain("public override int GetHashCode()");
        code.Should().NotContain("operator ==");
        code.Should().NotContain("operator !=");
    }
}
