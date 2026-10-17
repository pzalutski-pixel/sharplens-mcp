using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// get_method_source, get_method_signature and get_method_overloads address members
// through the one canonical overload set (OverloadOrdering): constructors by the type's
// simple name or ".ctor", an explicit interface implementation by the implemented
// member's name, inherited overloads after the declared ones, and a local function as
// a set of its own. A name that denotes a non-method is NOT_A_METHOD, never a silent
// miss.
public class MethodOverloadAddressingTests
{
    internal const string Code = """
        namespace Lib
        {
            public interface IShape { void Draw(); }

            public class Widget
            {
                public Widget() { }
                public Widget(int size) { Size = size; }
                public int Size { get; set; }
            }

            public class Square : IShape
            {
                void IShape.Draw() { }
            }

            public class Base2 { public int Calc(int x) => x; }
            public class Derived2 : Base2 { public string Calc(string s) => s; }

            public class LocalHost
            {
                public int Outer()
                {
                    int Helper(int v) => v + 1;
                    return Helper(1);
                }
            }
        }
        """;

    private static (RoslynService service, string path) Load()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        return (ToolResult.Load(workspace), document.FilePath!);
    }

    [Theory]
    [InlineData("Widget", 0, "Lib.Widget.Widget()", "public Widget() { }")]
    [InlineData("Widget", 1, "Lib.Widget.Widget(int)", "public Widget(int size) { Size = size; }")]
    [InlineData(".ctor", 1, "Lib.Widget.Widget(int)", "public Widget(int size) { Size = size; }")]
    public async Task GetMethodSource_AddressesConstructors(string methodName, int overloadIndex, string signature, string source)
    {
        var (service, _) = Load();

        var data = ToolResult.Data(await service.GetMethodSourceAsync("Lib.Widget", methodName, overloadIndex));

        data["signature"]!.Value<string>().Should().Be(signature);
        data["fullSource"]!.Value<string>()!.Trim().Should().Be(source);
        data["totalOverloads"]!.Value<int>().Should().Be(2);
    }

    [Fact]
    public async Task GetMethodSource_AddressesAnExplicitImplementation_ByTheImplementedName()
    {
        var (service, path) = Load();

        var data = ToolResult.Data(await service.GetMethodSourceAsync("Lib.Square", "Draw"));

        data["signature"]!.Value<string>().Should().Be("Lib.Square.Lib.IShape.Draw()");
        data["fullSource"]!.Value<string>()!.Trim().Should().Be("void IShape.Draw() { }");
        var (line, column) = Pos(Code, "void IShape.Draw()", "Draw");
        data["location"]!["filePath"]!.Value<string>().Should().Be(ToolResult.Reported(path));
        data["location"]!["line"]!.Value<int>().Should().Be(line);
        data["location"]!["column"]!.Value<int>().Should().Be(column);
    }

    [Fact]
    public async Task GetMethodSource_AddressesAnInheritedOverload_AfterTheDeclaredOne()
    {
        var (service, _) = Load();

        var data = ToolResult.Data(await service.GetMethodSourceAsync("Lib.Derived2", "Calc", 1));

        data["signature"]!.Value<string>().Should().Be("Lib.Base2.Calc(int)");
        data["isInherited"]!.Value<bool>().Should().BeTrue();
        data["totalOverloads"]!.Value<int>().Should().Be(2);
        data["fullSource"]!.Value<string>()!.Trim().Should().Be("public int Calc(int x) => x;");
    }

    [Fact]
    public async Task GetMethodSignature_AddressesAConstructor_AndAnInheritedOverload()
    {
        var (service, _) = Load();

        var constructor = ToolResult.Data(await service.GetMethodSignatureAsync("Lib.Widget", "Widget", 1));
        var inherited = ToolResult.Data(await service.GetMethodSignatureAsync("Lib.Derived2", "Calc", 1));

        constructor["fullSignature"]!.Value<string>().Should().Be("Lib.Widget.Widget(int)");
        constructor["overloadCount"]!.Value<int>().Should().Be(2);
        inherited["fullSignature"]!.Value<string>().Should().Be("Lib.Base2.Calc(int)");
        inherited["containingType"]!.Value<string>().Should().Be("Lib.Base2");
        inherited["isInherited"]!.Value<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData("public Widget(int size)", "Widget", new[] { "0|Lib.Widget.Widget()|Constructor|False", "1|Lib.Widget.Widget(int)|Constructor|False" })]
    [InlineData("void IShape.Draw()", "Draw", new[] { "0|Lib.Square.Lib.IShape.Draw()|Method|False" })]
    [InlineData("int Helper(int v)", "Helper", new[] { "0|Helper(int)|LocalFunction|False" })]
    [InlineData("public class Derived2", "Calc", new[] { "0|Lib.Derived2.Calc(string)|Method|False", "1|Lib.Base2.Calc(int)|Method|True" })]
    public async Task GetMethodOverloads_ListsTheCanonicalSet(string lineMarker, string token, string[] expected)
    {
        var (service, path) = Load();
        var (line, column) = Pos(Code, lineMarker, token);

        var data = ToolResult.Data(await service.GetMethodOverloadsAsync(path, line, column));

        ((JArray)data["overloads"]!)
            .Select(o => $"{o["overloadIndex"]}|{o["signature"]}|{o["kind"]}|{o["isInherited"]}")
            .Should().Equal(expected);
    }

    [Fact]
    public async Task MethodTools_OnANonMethodName_AreNotAMethod()
    {
        var (service, path) = Load();
        var (line, column) = Pos(Code, "public int Size", "Size");

        ToolResult.Error(await service.GetMethodSourceAsync("Lib.Widget", "Size"), ErrorCodes.NotAMethod);
        ToolResult.Error(await service.GetMethodSignatureAsync("Lib.Widget", "Size"), ErrorCodes.NotAMethod);
        ToolResult.Error(await service.GetMethodOverloadsAsync(path, line, column), ErrorCodes.NotAMethod);
    }

    [Fact]
    public async Task GetMethodSourceBatch_PartialFailure_IsNotTruncated()
    {
        var (service, _) = Load();

        var response = await service.GetMethodSourceBatchAsync(new List<Dictionary<string, object>>
        {
            new() { ["typeName"] = "Lib.Derived2", ["methodName"] = "Calc" },
            new() { ["typeName"] = "Lib.Derived2", ["methodName"] = "Nope" }
        });

        var data = ToolResult.Data(response);
        data["successCount"]!.Value<int>().Should().Be(1);
        data["errorCount"]!.Value<int>().Should().Be(1);
        var meta = ToolResult.Meta(response);
        meta["TotalCount"]!.Value<int>().Should().Be(2);
        meta["ReturnedCount"]!.Value<int>().Should().Be(1);
        meta["Truncated"]!.Value<bool>().Should().BeFalse("a method that failed to resolve is not a cut-off result");
    }

    [Fact]
    public async Task GetMethodSourceBatch_PartialFailure_IsNotTruncated_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        var host = new InMemoryToolHost(workspace);

        var inner = await host.CallAsync("get_method_source_batch", new
        {
            methods = new object[]
            {
                new { typeName = "Lib.Derived2", methodName = "Calc" },
                new { typeName = "Lib.Derived2", methodName = "Nope" }
            }
        });

        inner["meta"]!["totalCount"]!.Value<int>().Should().Be(2);
        inner["meta"]!["returnedCount"]!.Value<int>().Should().Be(1);
        inner["meta"]!["truncated"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task MethodTools_AddressTheCanonicalSet_ViaMcp()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        var host = new InMemoryToolHost(workspace);
        var (line, column) = Pos(Code, "public Widget(int size)", "Widget");

        var source = await host.DataAsync("get_method_source", new { typeName = "Lib.Square", methodName = "Draw" });
        var signature = await host.DataAsync("get_method_signature", new { typeName = "Lib.Widget", methodName = ".ctor", overloadIndex = 1 });
        var overloads = await host.DataAsync("get_method_overloads", new { filePath = document.FilePath, line, column });
        await host.ErrorAsync("get_method_source", new { typeName = "Lib.Widget", methodName = "Size" }, ErrorCodes.NotAMethod);

        source["signature"]!.Value<string>().Should().Be("Lib.Square.Lib.IShape.Draw()");
        signature["fullSignature"]!.Value<string>().Should().Be("Lib.Widget.Widget(int)");
        ((JArray)overloads["overloads"]!).Select(o => o["signature"]!.Value<string>())
            .Should().Equal("Lib.Widget.Widget()", "Lib.Widget.Widget(int)");
    }
}
