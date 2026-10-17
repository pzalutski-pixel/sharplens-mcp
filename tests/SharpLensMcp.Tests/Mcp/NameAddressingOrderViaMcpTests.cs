using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// NameAddressingOrderTests through the JSON-RPC dispatcher: rename_symbol's
// overloadIndex 0 targets the canonical first overload (a.cs's Foo(int)), an explicit
// interface implementation is addressable by its simple name, and an inherited-only
// member fails with SYMBOL_NOT_FOUND naming its declaring type (camelCase on the wire).
public class NameAddressingOrderViaMcpTests
{
    private static McpHarnessOps Load()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithMultipleDocuments(NameAddressingOrderTests.Files);
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        return new McpHarnessOps(server);
    }

    [Fact]
    public async Task RenameSymbol_OverloadIndexZero_ChangesOnlyADotCs()
    {
        var inner = await Load().CallToolAsync("rename_symbol", new
        {
            typeName = "P", memberName = "Foo", overloadIndex = 0, newName = "Bar", preview = true
        });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        ((JArray)inner["data"]!["changes"]!).Select(c => Path.GetFileName(c["filePath"]!.Value<string>()))
            .Should().Equal("a.cs");
    }

    [Fact]
    public async Task GetSuperMethod_ExplicitImplementationBySimpleName()
    {
        var inner = await Load().CallToolAsync("get_super_method", new { typeName = "Square", memberName = "Draw" });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["symbol"]!.Value<string>().Should().Be("Lib.Square.Lib.IShape.Draw()");
        ((JArray)inner["data"]!["implements"]!).Select(i => i["signature"]!.Value<string>())
            .Should().Equal("Lib.IShape.Draw()");
    }

    [Fact]
    public async Task GetDocumentation_InheritedOnlyMember_NamesTheDeclaringType()
    {
        var inner = await Load().CallToolAsync("get_documentation", new { typeName = "Square", memberName = "Describe" });
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        ((JArray)inner["error"]!["context"]!["declaringTypes"]!).Select(t => t.Value<string>())
            .Should().Equal("Lib.ShapeBase");
    }
}
