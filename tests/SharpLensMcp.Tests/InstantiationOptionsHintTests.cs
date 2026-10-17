using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_instantiation_options: the hint names what actually prevents or shapes
// instantiation — an interface (which the symbol model also marks abstract), an abstract
// class, a static class, System.IDisposable (by its identity, never a same-named
// interface of another namespace) — and every external factory is listed, in canonical
// order, with no silent cap.
public class InstantiationOptionsHintTests
{
    internal const string Code = """
        namespace Lib
        {
            public interface IShape { }
            public abstract class ShapeBase { }
            public static class Tools { }
            public sealed class Resource : System.IDisposable { public void Dispose() { } }
            public interface IDisposable { }
            public class LookAlike : IDisposable { }
            public class Widget { }

            public static class WidgetFactory
            {
                public static Widget CreateA() => new Widget();
                public static Widget CreateB() => new Widget();
                public static Widget CreateC() => new Widget();
                public static Widget CreateD() => new Widget();
                public static Widget CreateE() => new Widget();
                public static Widget CreateF() => new Widget();
            }

            public static class MoreWidgetFactories
            {
                public static Widget BuildA() => new Widget();
                public static Widget BuildB() => new Widget();
                public static Widget BuildC() => new Widget();
                public static Widget NewA() => new Widget();
                public static Widget NewB() => new Widget();
                public static Widget NewC() => new Widget();
            }
        }
        """;

    [Theory]
    [InlineData("Lib.IShape", "Type is an interface - cannot instantiate directly", false)]
    [InlineData("Lib.ShapeBase", "Type is abstract - cannot instantiate directly, use derived type", false)]
    [InlineData("Lib.Tools", "Type is static - it cannot be instantiated", false)]
    [InlineData("Lib.Resource", "Type implements IDisposable - consider using 'using' statement", true)]
    [InlineData("Lib.LookAlike", null, false)]
    public async Task Hint_NamesWhatShapesInstantiation(string typeName, string? hint, bool implementsIDisposable)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Lib.cs");
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.GetInstantiationOptionsAsync(typeName));

        if (hint == null) data["hint"]!.Type.Should().Be(JTokenType.Null);
        else data["hint"]!.Value<string>().Should().Be(hint);
        data["implementsIDisposable"]!.Value<bool>().Should().Be(implementsIDisposable);
    }

    [Fact]
    public async Task ExternalFactories_AllListed_InCanonicalOrder()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Lib.cs");
        var service = ToolResult.Load(workspace);

        var response = await service.GetInstantiationOptionsAsync("Lib.Widget");

        ((JArray)ToolResult.Data(response)["externalFactories"]!).Select(f => $"{f["containingType"]}.{f["name"]}").Should().Equal(
            "Lib.MoreWidgetFactories.BuildA", "Lib.MoreWidgetFactories.BuildB", "Lib.MoreWidgetFactories.BuildC",
            "Lib.MoreWidgetFactories.NewA", "Lib.MoreWidgetFactories.NewB", "Lib.MoreWidgetFactories.NewC",
            "Lib.WidgetFactory.CreateA", "Lib.WidgetFactory.CreateB", "Lib.WidgetFactory.CreateC",
            "Lib.WidgetFactory.CreateD", "Lib.WidgetFactory.CreateE", "Lib.WidgetFactory.CreateF");
        ToolResult.Meta(response)["Truncated"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Hint_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Lib.cs");
        var host = new InMemoryToolHost(workspace);

        var shape = await host.DataAsync("get_instantiation_options", new { typeName = "Lib.IShape" });
        var widget = await host.DataAsync("get_instantiation_options", new { typeName = "Lib.Widget" });

        shape["hint"]!.Value<string>().Should().Be("Type is an interface - cannot instantiate directly");
        ((JArray)widget["externalFactories"]!).Count.Should().Be(12);
    }
}
