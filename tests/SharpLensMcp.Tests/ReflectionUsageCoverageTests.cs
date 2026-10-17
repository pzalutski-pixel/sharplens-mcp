using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// find_reflection_usage recognizes every System.Type member that looks up or invokes
// members by reflection — the constructor, interface, nested-type and event lookups and
// InvokeMember included — by what the API returns or does, not by a list of names; a
// Type query that only answers a question (IsAssignableFrom) is not reflection. The
// low-signal Object.GetType() call sorts after the rest, each group in source order.
public class ReflectionUsageCoverageTests
{
    internal const string Code = """
        using System;
        using System.Reflection;

        namespace Lib
        {
            public static class Refl
            {
                public static object? A(Type t) => t.GetConstructor(Type.EmptyTypes);
                public static object? B(Type t) => t.GetConstructors();
                public static object? C(Type t) => t.InvokeMember("X", BindingFlags.CreateInstance, null, null, null);
                public static object? D(Type t) => t.GetInterface("IDisposable");
                public static object? E(Type t) => t.GetInterfaces();
                public static object? F(Type t) => t.GetNestedType("N");
                public static object? G(Type t) => t.GetNestedTypes();
                public static object? H(Type t) => t.GetEvents();
                public static object Low(object o) => o.GetType();
                public static bool Query(Type t) => t.IsAssignableFrom(typeof(int));
            }
        }
        """;

    private static readonly string[] Expected =
    {
        "Type.GetConstructor", "Type.GetConstructors", "Type.InvokeMember", "Type.GetInterface",
        "Type.GetInterfaces", "Type.GetNestedType", "Type.GetNestedTypes", "Type.GetEvents", "Object.GetType"
    };

    [Fact]
    public async Task RecognizesEveryReflectionLookup()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Refl.cs");
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.FindReflectionUsageAsync());

        ((JArray)data["usages"]!).Select(u => u["reflectionApi"]!.Value<string>()).Should().Equal(Expected);
    }

    [Fact]
    public async Task ParametersAreValidated()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Refl.cs");
        var service = ToolResult.Load(workspace);

        ToolResult.Error(await service.FindReflectionUsageAsync(maxResults: -1), ErrorCodes.InvalidParameter)
            ["Context"]!["parameter"]!.Value<string>().Should().Be("maxResults");
        ToolResult.Error(await service.FindReflectionUsageAsync("Nope"), ErrorCodes.InvalidParameter)
            ["Message"]!.Value<string>().Should().Be("Project not found: Nope");
    }

    [Fact]
    public async Task RecognizesEveryReflectionLookup_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Refl.cs");
        var host = new InMemoryToolHost(workspace);

        var data = await host.DataAsync("find_reflection_usage", new { });
        await host.ErrorAsync("find_reflection_usage", new { maxResults = 0 }, ErrorCodes.InvalidParameter);

        ((JArray)data["usages"]!).Select(u => u["reflectionApi"]!.Value<string>()).Should().Equal(Expected);
    }
}
