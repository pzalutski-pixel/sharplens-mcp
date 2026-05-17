using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Code Generation category (2 tools).
public class CodeGenToolsViaMcpTests : McpTestBase
{
    public CodeGenToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task AddNullChecks_OnLoadSolution_GeneratesArgumentNullExceptionGuard()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "LoadSolutionAsync", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("RoslynService") == true);

        var data = await CallAndGetDataAsync("roslyn:add_null_checks", new
        {
            filePath = file, line, column = col, preview = true
        });
        data.ToString().Should().Contain("ArgumentNullException",
            "the preview must include the standard ArgumentNullException guard");
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithOperators_IncludesEqualsAndOperatorEq()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "RefactoringTarget", kind: "Class");

        var data = await CallAndGetDataAsync("roslyn:generate_equality_members", new
        {
            filePath = file, line, column = col,
            includeOperators = true, preview = true
        });
        var text = data.ToString();
        text.Should().Contain("Equals");
        text.Should().Contain("GetHashCode");
        text.Should().Contain("operator ==");
    }

    [Fact]
    public async Task GenerateEqualityMembers_WithoutOperators_OmitsOperatorEq()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "RefactoringTarget", kind: "Class");

        var data = await CallAndGetDataAsync("roslyn:generate_equality_members", new
        {
            filePath = file, line, column = col,
            includeOperators = false, preview = true
        });
        var text = data.ToString();
        text.Should().Contain("Equals");
        text.Should().NotContain("operator ==");
    }
}
