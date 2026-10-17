using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// get_extension_methods reports each applicable extension once:
//   - for a generic type definition (List`1), an extension whose receiver is one of its
//     constructions (this List<int>) applies, as does one generic over it;
//   - a C# 14 extension block member is reported as the block member, never again as
//     the classic method the compiler lowers it to;
//   - a generic extension block applies when the type satisfies its receiver's
//     constraints (extension<T>(T value) where T : IShape);
// each entry carries its declared receiver type and a { filePath, line, column } location.
public class ExtensionApplicabilityTests
{
    internal const string Code = """
        using System.Collections.Generic;

        namespace Lib
        {
            public interface IShape { double Area { get; } }
            public class Square : IShape { public double Area => 1; }
            public class Widget { }

            public static class Exts
            {
                public static int CountInts(this List<int> l) => l.Count;
                public static int CountAny<T>(this IEnumerable<T> e) => 0;
                public static void Hello(this Widget w) { }
                public static double Half<T>(this T s) where T : IShape => s.Area / 2;
            }

            public static class BlockExts
            {
                extension<T>(T value) where T : IShape
                {
                    public double DoubleArea() => value.Area * 2;
                }

                extension(Widget w)
                {
                    public void Wave() { }
                    public int Size => 1;
                }
            }
        }
        """;

    private static List<string> Members(JToken data) =>
        ((JArray)data["groups"]!).SelectMany(g => ((JArray)g["members"]!)
            .Select(m => $"{g["containingClass"]}.{m["name"]}|{m["style"]}|{m["kind"]}")).ToList();

    [Theory]
    [InlineData("System.Collections.Generic.List`1", new[] { "Lib.Exts.CountInts|classic|Method", "Lib.Exts.CountAny|classic|Method" })]
    [InlineData("Lib.Square", new[] { "Lib.BlockExts.DoubleArea|extensionBlock|Method", "Lib.Exts.Half|classic|Method" })]
    [InlineData("Lib.Widget", new[] { "Lib.BlockExts.Wave|extensionBlock|Method", "Lib.BlockExts.Size|extensionBlock|Property", "Lib.Exts.Hello|classic|Method" })]
    public async Task ReportsEachApplicableExtensionOnce(string typeName, string[] expected)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Exts.cs");
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.GetExtensionMethodsAsync(typeName));

        Members(data).Should().Equal(expected);
    }

    [Fact]
    public async Task Entries_CarryTheReceiverType_AndAFullLocation()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Exts.cs");
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.GetExtensionMethodsAsync("System.Collections.Generic.List`1"));

        var countInts = ((JArray)data["groups"]![0]!["members"]!)[0];
        countInts["receiverType"]!.Value<string>().Should().Be("System.Collections.Generic.List<int>");
        var (line, column) = Pos(Code, "CountInts(this List<int> l)", "CountInts");
        countInts["location"]!["filePath"]!.Value<string>().Should().Be(ToolResult.Reported(document.FilePath!));
        countInts["location"]!["line"]!.Value<int>().Should().Be(line);
        countInts["location"]!["column"]!.Value<int>().Should().Be(column);
    }

    [Fact]
    public async Task ReportsEachApplicableExtensionOnce_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Exts.cs");
        var host = new InMemoryToolHost(workspace);

        var data = await host.DataAsync("get_extension_methods", new { typeName = "Lib.Square" });

        Members(data).Should().Equal("Lib.BlockExts.DoubleArea|extensionBlock|Method", "Lib.Exts.Half|classic|Method");
        ((JArray)data["groups"]![0]!["members"]!)[0]["location"]!["column"]!.Value<int>()
            .Should().Be(Pos(Code, "public double DoubleArea()", "DoubleArea").column);
    }
}
