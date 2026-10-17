using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// The hierarchy tools treat interfaces and abstract classes as bases like any other:
//   - get_derived_types / get_type_hierarchy on an interface list the interfaces deriving
//     from it and the types implementing it (directly, or transitively when asked);
//   - find_implementations on a class lists the classes deriving from it, on an interface
//     every type implementing it (never an interface);
//   - get_type_hierarchy caps derivedTypes alone with maxDerivedTypes, its counts describe
//     derivedTypes, and baseTypes/interfaces are always complete.
public class TypeHierarchyInterfaceTests
{
    internal const string Code = """
        namespace Lib
        {
            public interface IShape { }
            public interface IRound : IShape { }
            public abstract class ShapeBase : IShape { }
            public class Square : ShapeBase { }
            public class Circle : IRound { }
            public sealed class Dot : Circle { }
            public interface IA { }
            public interface IB { }
            public interface IC { }
            public class Many : IA, IB, IC { }
        }
        """;

    private static (RoslynService service, string path) Load()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        return (ToolResult.Load(workspace), document.FilePath!);
    }

    [Theory]
    [InlineData(true, new[] { "Lib.Circle", "Lib.Dot", "Lib.IRound", "Lib.ShapeBase", "Lib.Square" })]
    [InlineData(false, new[] { "Lib.IRound", "Lib.ShapeBase" })]
    public async Task GetDerivedTypes_OnAnInterface_ListsDerivedInterfacesAndImplementers(bool includeTransitive, string[] expected)
    {
        var (service, _) = Load();

        var data = ToolResult.Data(await service.GetDerivedTypesAsync("Lib.IShape", includeTransitive));

        ((JArray)data["derivedTypes"]!).Select(d => d["fullName"]!.Value<string>()).Should().Equal(expected);
        data["totalDerived"]!.Value<int>().Should().Be(expected.Length);
    }

    [Theory]
    [InlineData("public abstract class ShapeBase", "ShapeBase", new[] { "Lib.Square" })]
    [InlineData("public class Circle", "Circle", new[] { "Lib.Dot" })]
    [InlineData("public interface IShape", "IShape", new[] { "Lib.Circle", "Lib.Dot", "Lib.ShapeBase", "Lib.Square" })]
    public async Task FindImplementations_CoversAbstractClassesAndInterfaces(string lineMarker, string token, string[] expected)
    {
        var (service, path) = Load();
        var (line, column) = Pos(Code, lineMarker, token);

        var data = ToolResult.Data(await service.FindImplementationsAsync(path, line, column));

        ((JArray)data["implementations"]!).Select(i => i["name"]!.Value<string>()).Should().Equal(expected);
        data["totalImplementations"]!.Value<int>().Should().Be(expected.Length);
    }

    [Fact]
    public async Task GetTypeHierarchy_OnAnInterface_ListsDirectDerivedTypes_CappedWithHonestCounts()
    {
        var (service, path) = Load();
        var (line, column) = Pos(Code, "public interface IShape", "IShape");

        var full = await service.GetTypeHierarchyAsync(path, line, column);
        var capped = await service.GetTypeHierarchyAsync(path, line, column, maxDerivedTypes: 1);

        ((JArray)ToolResult.Data(full)["derivedTypes"]!).Select(d => d["name"]!.Value<string>())
            .Should().Equal("Lib.IRound", "Lib.ShapeBase");
        ((JArray)ToolResult.Data(capped)["derivedTypes"]!).Select(d => d["name"]!.Value<string>())
            .Should().Equal("Lib.IRound");
        ToolResult.Data(capped)["totalDerivedTypes"]!.Value<int>().Should().Be(2);
        var meta = ToolResult.Meta(capped);
        meta["TotalCount"]!.Value<int>().Should().Be(2);
        meta["ReturnedCount"]!.Value<int>().Should().Be(1);
        meta["Truncated"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task GetTypeHierarchy_InterfacesAreNeverCapped()
    {
        var (service, path) = Load();
        var (line, column) = Pos(Code, "public class Many", "Many");

        var response = await service.GetTypeHierarchyAsync(path, line, column, maxDerivedTypes: 1);

        var data = ToolResult.Data(response);
        ((JArray)data["interfaces"]!).Select(i => i["name"]!.Value<string>()).Should().Equal("Lib.IA", "Lib.IB", "Lib.IC");
        data["totalInterfaces"]!.Value<int>().Should().Be(3);
        var meta = ToolResult.Meta(response);
        meta["TotalCount"]!.Value<int>().Should().Be(0);
        meta["Truncated"]!.Value<bool>().Should().BeFalse("no derived type was cut");
    }

    [Fact]
    public async Task Hierarchy_ViaMcp()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        var host = new InMemoryToolHost(workspace);
        var (line, column) = Pos(Code, "public abstract class ShapeBase", "ShapeBase");

        var derived = await host.DataAsync("get_derived_types", new { baseTypeName = "Lib.IShape", includeTransitive = false });
        var implementations = await host.DataAsync("find_implementations", new { filePath = document.FilePath, line, column });
        await host.ErrorAsync("get_type_hierarchy", new { filePath = document.FilePath, line, column, maxDerivedTypes = 0 }, ErrorCodes.InvalidParameter);

        ((JArray)derived["derivedTypes"]!).Select(d => d["fullName"]!.Value<string>()).Should().Equal("Lib.IRound", "Lib.ShapeBase");
        ((JArray)implementations["implementations"]!).Select(i => i["name"]!.Value<string>()).Should().Equal("Lib.Square");
    }
}
