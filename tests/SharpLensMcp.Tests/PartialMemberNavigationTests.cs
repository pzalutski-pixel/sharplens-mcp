using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// A partial method and a partial property declared in Partial.cs and implemented in
// PartialImpl.cs. Every navigation tool reports the part that carries the body (the
// implementation) as the member's location, and the tools that list a symbol's
// declarations report every part, in path order (Partial.cs before PartialImpl.cs);
// get_method_source returns the implementation's source and body.
public class PartialMemberNavigationTests
{
    internal const string Declaration = """
        namespace Lib
        {
            public partial class Gen
            {
                /// <summary>Runs.</summary>
                public partial void Run(int x);
                public partial int Size { get; }
            }
        }
        """;

    internal const string Implementation = """
        namespace Lib
        {
            public partial class Gen
            {
                public partial void Run(int x)
                {
                    System.Console.WriteLine(x);
                }
                public partial int Size { get => 3; }
            }

            public class Caller
            {
                public void X(Gen g) { g.Run(1); _ = g.Size; }
            }
        }
        """;

    private sealed record Paths(string Declaration, string Implementation);

    private static (Microsoft.CodeAnalysis.AdhocWorkspace workspace, Paths paths) Create()
    {
        var (workspace, documents) = TestHelpers.CreateWorkspaceWithMultipleDocuments(
            (Implementation, "PartialImpl.cs"), (Declaration, "Partial.cs"));
        return (workspace, new Paths(documents[1].FilePath!, documents[0].FilePath!));
    }

    private static string At(string path, (int line, int column) pos) => $"{ToolResult.Reported(path)}:{pos.line}:{pos.column}";

    private static string LocationOf(JToken location) => $"{location["filePath"]}:{location["line"]}:{location["column"]}";

    [Theory]
    [InlineData("Run", "public partial void Run(int x)")]
    [InlineData("Size", "public partial int Size")]
    public async Task GoToDefinition_ReportsTheImplementationPart_AndEveryPart(string token, string declarationLine)
    {
        var (workspace, paths) = Create();
        var service = ToolResult.Load(workspace);
        var (line, column) = Pos(Implementation, "public void X(Gen g)", $"g.{token}");

        var data = ToolResult.Data(await service.GoToDefinitionAsync(paths.Implementation, line, column + 2));

        LocationOf(data["definition"]!).Should().Be(At(paths.Implementation, Pos(Implementation, declarationLine, token)));
        ((JArray)data["locations"]!).Select(LocationOf).Should().Equal(
            At(paths.Declaration, Pos(Declaration, declarationLine, token)),
            At(paths.Implementation, Pos(Implementation, declarationLine, token)));
    }

    [Fact]
    public async Task GetSymbolInfo_ReportsTheImplementationPart_AndEveryPart()
    {
        var (workspace, paths) = Create();
        var service = ToolResult.Load(workspace);
        var (line, column) = Pos(Implementation, "public void X(Gen g)", "Run");

        var data = ToolResult.Data(await service.GetSymbolInfoAsync(paths.Implementation, line, column));

        LocationOf(data["location"]!).Should().Be(At(paths.Implementation, Pos(Implementation, "public partial void Run", "Run")));
        ((JArray)data["locations"]!).Select(LocationOf).Should().Equal(
            At(paths.Declaration, Pos(Declaration, "public partial void Run", "Run")),
            At(paths.Implementation, Pos(Implementation, "public partial void Run", "Run")));
    }

    [Fact]
    public async Task GetMethodSource_ReturnsTheImplementationBody()
    {
        var (workspace, paths) = Create();
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.GetMethodSourceAsync("Lib.Gen", "Run"));

        data["bodySource"]!.Value<string>()!.Should().Contain("System.Console.WriteLine(x);");
        data["fullSource"]!.Value<string>()!.Trim().Should().StartWith("public partial void Run(int x)");
        LocationOf(data["location"]!).Should().Be(At(paths.Implementation, Pos(Implementation, "public partial void Run", "Run")));
        data["span"]!["startLine"]!.Value<int>().Should().Be(Pos(Implementation, "public partial void Run", "public").line);
        data["span"]!["endLine"]!.Value<int>().Should().Be(Pos(Implementation, "public partial void Run", "public").line + 3);
    }

    [Fact]
    public async Task GetMethodSignature_And_GetMethodOverloads_ReportTheImplementationPart()
    {
        var (workspace, paths) = Create();
        var service = ToolResult.Load(workspace);
        var (line, column) = Pos(Implementation, "public void X(Gen g)", "Run");
        var expected = At(paths.Implementation, Pos(Implementation, "public partial void Run", "Run"));

        var signature = ToolResult.Data(await service.GetMethodSignatureAsync("Lib.Gen", "Run"));
        var overloads = ToolResult.Data(await service.GetMethodOverloadsAsync(paths.Implementation, line, column));

        LocationOf(signature["location"]!).Should().Be(expected);
        ((JArray)overloads["overloads"]!).Select(o => LocationOf(o["location"]!)).Should().Equal(expected);
    }

    [Fact]
    public async Task PartialMembers_ReportTheImplementationPart_ViaMcp()
    {
        var (workspace, paths) = Create();
        var host = new InMemoryToolHost(workspace);
        var (line, column) = Pos(Implementation, "public void X(Gen g)", "Run");
        var expected = At(paths.Implementation, Pos(Implementation, "public partial void Run", "Run"));

        var definition = await host.DataAsync("go_to_definition", new { filePath = paths.Implementation, line, column });
        var info = await host.DataAsync("get_symbol_info", new { filePath = paths.Implementation, line, column });
        var source = await host.DataAsync("get_method_source", new { typeName = "Lib.Gen", methodName = "Run" });
        var signature = await host.DataAsync("get_method_signature", new { typeName = "Lib.Gen", methodName = "Run" });
        var overloads = await host.DataAsync("get_method_overloads", new { filePath = paths.Implementation, line, column });

        LocationOf(definition["definition"]!).Should().Be(expected);
        ((JArray)definition["locations"]!).Count.Should().Be(2);
        LocationOf(info["location"]!).Should().Be(expected);
        ((JArray)info["locations"]!).Count.Should().Be(2);
        LocationOf(source["location"]!).Should().Be(expected);
        source["bodySource"]!.Value<string>()!.Should().Contain("System.Console.WriteLine(x);");
        LocationOf(signature["location"]!).Should().Be(expected);
        LocationOf(((JArray)overloads["overloads"]!)[0]["location"]!).Should().Be(expected);
    }
}
