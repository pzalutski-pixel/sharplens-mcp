using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// GeneratedPartAddressingTests through the JSON-RPC dispatcher: name-addressed tools
// resolve a member with a generated body and a type with a generated declaration to
// the user-authored rx.cs, never FILE_NOT_IN_SOLUTION on the generated file.
public class GeneratedPartAddressingViaMcpTests
{
    private static McpHarnessOps Load()
    {
        var (workspace, _) = GeneratedPartAddressingFixture.Create();
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        return new McpHarnessOps(server);
    }

    [Fact]
    public async Task GetSuperMethod_GeneratedBodyMember()
    {
        var inner = await Load().CallToolAsync("get_super_method",
            new { typeName = "GenPartialHost", memberName = "Compute" });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["symbol"]!.Value<string>().Should().Be("Demo.GenPartialHost.Compute(int)");
    }

    [Fact]
    public async Task RenameSymbol_TypeWithGeneratedDeclaration()
    {
        var inner = await Load().CallToolAsync("rename_symbol",
            new { typeName = "GenPartialHost", newName = "GenPartialHost2", preview = true });
        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        ((JArray)inner["data"]!["changes"]!).Select(c => Path.GetFileName(c["filePath"]!.Value<string>()))
            .Should().Equal("rx.cs");
    }

    [Fact]
    public async Task GetSuperMethod_UnknownMember_ReturnsSymbolNotFound()
    {
        var inner = await Load().CallToolAsync("get_super_method",
            new { typeName = "GenPartialHost", memberName = "Missing" });
        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
    }
}
