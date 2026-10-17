using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// PositionResolutionTests through the JSON-RPC dispatcher: a field modifier resolves
// the field, a cref navigates to its target, a blank line is SYMBOL_NOT_FOUND rather
// than the enclosing type, and a NUL in the path is INVALID_PARAMETER rather than an
// INTERNAL_ERROR (camelCase error fields on the wire).
public class PositionResolutionViaMcpTests
{
    private static (McpHarnessOps ops, string filePath) Load()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(PositionResolutionTests.Code, "Refs.cs");
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        return (new McpHarnessOps(server), document.FilePath!);
    }

    [Fact]
    public async Task GetSymbolInfo_FieldModifier_ResolvesTheField()
    {
        var (ops, filePath) = Load();
        var (line, column) = PositionResolutionTests.At("private readonly int _field", "private");
        var inner = await ops.CallToolAsync("get_symbol_info", new { filePath, line, column });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["name"]!.Value<string>().Should().Be("_field");
        inner["data"]!["kind"]!.Value<string>().Should().Be("Field");
    }

    [Fact]
    public async Task GoToDefinition_Cref_NavigatesToTheTarget()
    {
        var (ops, filePath) = Load();
        var (line, column) = PositionResolutionTests.At("<see cref=", "Widget\"", offsetInToken: 2);
        var inner = await ops.CallToolAsync("go_to_definition", new { filePath, line, column });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["symbol"]!["name"]!.Value<string>().Should().Be("Widget");
        inner["data"]!["definition"]!["line"]!.Value<int>().Should().Be(
            PositionResolutionTests.At("public class Widget", "Widget").line);
    }

    [Fact]
    public async Task FindReferences_BlankLine_ReturnsSymbolNotFound()
    {
        var (ops, filePath) = Load();
        var inner = await ops.CallToolAsync("find_references",
            new { filePath, line = PositionResolutionTests.Lines.Length - 2, column = 0 });
        inner["success"]!.Value<bool>().Should().BeFalse(inner.ToString());
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task GetSymbolInfo_NulInPath_ReturnsInvalidParameter()
    {
        var (ops, _) = Load();
        var inner = await ops.CallToolAsync("get_symbol_info", new { filePath = "Re\0fs.cs", line = 0, column = 0 });
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }
}
