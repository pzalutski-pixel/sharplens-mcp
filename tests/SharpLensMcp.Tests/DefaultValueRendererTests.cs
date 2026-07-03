using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_method_overloads and get_instantiation_options must render parameter default
// values through the shared FormatConstantLiteral — enum member names, not boxed
// integers, INCLUDING nullable enums — rather than raw ExplicitDefaultValue.ToString().
public class DefaultValueRendererTests
{
    private const string Code = @"
namespace Demo;

public enum Mode { Off, On }

public class Widget
{
    public Widget(Mode mode = Mode.On, Mode? fallback = Mode.Off) { }
    public int Configure(Mode mode = Mode.On) => 0;
    public int Configure(Mode mode, int count) => count;
}
";

    private static RoslynService Loaded()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Widget.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task GetInstantiationOptions_RendersEnumAndNullableEnumDefaultsByName()
    {
        var service = Loaded();

        var result = await service.GetInstantiationOptionsAsync("Widget");
        var data = JObject.FromObject(result)["data"]!;
        var ctor = (data["constructors"] as JArray)!.Single();
        var ps = (ctor["parameters"] as JArray)!.ToDictionary(p => p["name"]!.Value<string>()!);

        ps["mode"]!["defaultValue"]!.Value<string>().Should().Be("Mode.On",
            "an enum default renders as the member name, not the boxed int 1");
        ps["fallback"]!["defaultValue"]!.Value<string>().Should().Be("Mode.Off",
            "a NULLABLE enum default unwraps Nullable<T> and renders the member name, not 0");
    }

    [Fact]
    public async Task GetMethodOverloads_RendersEnumDefaultByName()
    {
        var service = Loaded();

        var search = await service.SearchSymbolsAsync("Configure", kind: "Method", maxResults: 10);
        var loc = JObject.FromObject(search)["data"]!["results"]!.First()!["location"]!;
        var result = await service.GetMethodOverloadsAsync(
            loc["filePath"]!.Value<string>()!, loc["line"]!.Value<int>(), loc["column"]!.Value<int>());

        var overloads = (JObject.FromObject(result)["data"]!["overloads"] as JArray)!;
        var defaulted = overloads.First(o =>
            (o["parameters"] as JArray)!.Any(p => p["name"]!.Value<string>() == "mode"
                && p["isOptional"]!.Value<bool>()));
        var mode = (defaulted["parameters"] as JArray)!
            .First(p => p["name"]!.Value<string>() == "mode");
        mode["defaultValue"]!.Value<string>().Should().Be("Mode.On",
            "the enum default must render as Mode.On, not 1");
    }
}
