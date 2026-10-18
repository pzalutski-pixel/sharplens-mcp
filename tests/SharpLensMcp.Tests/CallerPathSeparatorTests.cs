using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// A caller-supplied file path written with Windows separators ('\') resolves on every
// platform: on Windows '\' is native, elsewhere it is converted to '/' before the path is
// resolved (RoslynService.WithNativeSeparators). The path below is the fixture document's
// absolute path with every separator spelled '\'.
public class CallerPathSeparatorTests
{
    private const string LibSource = "namespace Lib { public class Calculator { public int Add(int a, int b) => a + b; } }";
    private const string TestSource = "namespace Lib.Tests { public class Uses { } }";

    private static string WindowsStyle(string path) => path.Replace('/', '\\');

    [Fact]
    public void TryFindDocument_ResolvesAPathWrittenWithBackslashes()
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, TestSource);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);

        var document = service.TryFindDocument(WindowsStyle(fixture.LibFilePath));

        document.Should().NotBeNull();
        document!.FilePath.Should().Be(fixture.LibFilePath);
    }

    [Fact]
    public async Task ViaDispatcher_AToolGivenABackslashPath_ReadsTheDocument()
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, TestSource);
        var server = new McpServer(TimeSpan.FromMinutes(5));
        server.LoadFromWorkspaceForTesting(fixture.Workspace);

        var inner = await new McpHarnessOps(server).CallToolAsync(
            "get_file_overview", new { filePath = WindowsStyle(fixture.LibFilePath) });

        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        inner["data"]!["projectName"]!.Value<string>().Should().Be(RetargetedSolutionFixture.LibProjectName);
        ((JArray)inner["data"]!["typeDeclarations"]!).Select(t => t["name"]!.Value<string>())
            .Should().Equal("Calculator");
    }
}
