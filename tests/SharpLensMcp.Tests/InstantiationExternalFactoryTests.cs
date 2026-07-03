using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_instantiation_options must surface a Create*/Build*/New* factory in another type
// that returns the target — including a GENERIC target via a factory returning a
// constructed form (the old GetTypeByMetadataName/exact-equality path missed both).
public class InstantiationExternalFactoryTests
{
    [Fact]
    public async Task GenericTarget_FactoryReturningConstructedForm_IsFound()
    {
        const string code = @"
namespace Demo
{
    public class Widget<T> { }

    public static class WidgetFactory
    {
        public static Widget<int> CreateWidget() => new Widget<int>();
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Widget.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.GetInstantiationOptionsAsync("Widget"))["data"]!;
        var factories = (data["externalFactories"] as JArray)!
            .Select(f => f["name"]!.Value<string>())
            .ToList();

        factories.Should().Contain("CreateWidget",
            "a factory returning Widget<int> is an external factory for the generic Widget<T>");
    }
}
