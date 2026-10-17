using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// Every by-name navigation tool tells the caller when the type name it resolved also
// matches framework types: data.notice carries TypeResolution.ShadowNotice ("'Timer'
// resolved to App.Timer; also matches System.Threading.Timer — ..."), and is null when the
// name is unambiguous (a fully-qualified App.Timer). The batch tools carry it per entry.
public class NavigationShadowNoticeWiringTests
{
    internal const string Code = """
        namespace App
        {
            public class Timer
            {
                public Timer() { }
                /// <summary>Starts.</summary>
                public virtual void Start() { }
            }

            public class FastTimer : Timer
            {
                public override void Start() { }
            }

            public static class TimerExt
            {
                public static void Stop(this Timer timer) { }
            }
        }
        """;

    private static RoslynService Load()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Timer.cs");
        return ToolResult.Load(workspace);
    }

    public static IEnumerable<object[]> Calls() => new[]
    {
        new object[] { "get_type_members" },
        new object[] { "get_method_source" },
        new object[] { "get_method_signature" },
        new object[] { "get_extension_methods" },
        new object[] { "get_instantiation_options" },
        new object[] { "get_base_types" },
        new object[] { "get_derived_types" },
        new object[] { "get_documentation" },
        new object[] { "get_super_method" },
    };

    private static Task<object> Call(RoslynService service, string tool, string typeName) => tool switch
    {
        "get_type_members" => service.GetTypeMembersAsync(typeName),
        "get_method_source" => service.GetMethodSourceAsync(typeName, "Start"),
        "get_method_signature" => service.GetMethodSignatureAsync(typeName, "Start"),
        "get_extension_methods" => service.GetExtensionMethodsAsync(typeName),
        "get_instantiation_options" => service.GetInstantiationOptionsAsync(typeName),
        "get_base_types" => service.GetBaseTypesAsync(typeName),
        "get_derived_types" => service.GetDerivedTypesAsync(typeName),
        "get_documentation" => service.GetDocumentationAsync(typeName, "Start"),
        "get_super_method" => service.GetSuperMethodAsync(typeName: typeName, memberName: "Start"),
        _ => throw new ArgumentException(tool, nameof(tool))
    };

    private static object Arguments(string tool, string typeName) => tool switch
    {
        "get_method_source" or "get_method_signature" => new { typeName, methodName = "Start" },
        "get_derived_types" => new { baseTypeName = typeName },
        "get_documentation" or "get_super_method" => new { typeName, memberName = "Start" },
        _ => new { typeName }
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task ShadowingName_CarriesTheNotice(string tool)
    {
        var data = ToolResult.Data(await Call(Load(), tool, "Timer"));

        data["notice"]!.Value<string>().Should().Be(FrameworkTypeResolutionTests.TimerNotice);
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task QualifiedName_HasNoNotice(string tool)
    {
        var data = ToolResult.Data(await Call(Load(), tool, "App.Timer"));

        data["notice"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task BatchTools_CarryTheNoticePerEntry()
    {
        var service = Load();

        var members = ToolResult.Data(await service.GetTypeMembersBatchAsync(new List<string> { "Timer", "App.FastTimer" }));
        var sources = ToolResult.Data(await service.GetMethodSourceBatchAsync(new List<Dictionary<string, object>>
        {
            new() { ["typeName"] = "Timer", ["methodName"] = "Start" }
        }));

        ((JArray)members["results"]!)[0]["data"]!["notice"]!.Value<string>().Should().Be(FrameworkTypeResolutionTests.TimerNotice);
        ((JArray)members["results"]!)[1]["data"]!["notice"]!.Type.Should().Be(JTokenType.Null);
        ((JArray)sources["results"]!)[0]["data"]!["notice"]!.Value<string>().Should().Be(FrameworkTypeResolutionTests.TimerNotice);
    }

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task ShadowingName_CarriesTheNotice_ViaMcp(string tool)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Timer.cs");
        var host = new InMemoryToolHost(workspace);

        var data = await host.DataAsync(tool, Arguments(tool, "Timer"));

        data["notice"]!.Value<string>().Should().Be(FrameworkTypeResolutionTests.TimerNotice);
    }
}
