using System.Reflection;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_external_type_info lists a metadata type's surface by the one listing rule
// (UserVisibleMembers): no property/event accessors, no explicit interface
// implementations (reachable only through the interface), no inherited constructors,
// each member once, labeled with the MemberKind vocabulary. The expected constructor
// counts come from reflection over the same runtime assemblies the workspace references.
public class ExternalTypeSurfaceTests
{
    private static RoslynService Load()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("namespace Probe { public class P { } }", "P.cs");
        return ToolResult.Load(workspace);
    }

    private static int VisibleConstructorCount(Type type) =>
        type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Count(c => c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly);

    [Fact]
    public async Task List_NoAccessorsNoExplicitImplementations_EachMemberOnce()
    {
        var data = ToolResult.Data(await Load().GetExternalTypeInfoAsync(
            "System.Collections.Generic.List`1", includeXmlDocs: false, maxMembers: 1000));

        var members = (JArray)data["members"]!;
        var names = members.Select(m => m["name"]!.Value<string>()!).ToList();
        names.Should().NotContain(n => n.StartsWith("get_") || n.StartsWith("set_"), "accessors are represented by their property");
        names.Should().NotContain(n => n.Contains('.') && n != ".ctor", "explicit interface implementations are not the type's surface");
        names.Count(n => n == "GetEnumerator").Should().Be(1);
        members.Single(m => m["name"]!.Value<string>() == "Count")["kind"]!.Value<string>().Should().Be("Property");
        members.Count(m => m["kind"]!.Value<string>() == "Constructor").Should().Be(VisibleConstructorCount(typeof(List<>)));
        members.Single(m => m["name"]!.Value<string>() == "this[]")["kind"]!.Value<string>().Should().Be("Indexer");
    }

    [Fact]
    public async Task ArgumentException_ListsItsOwnConstructorsOnly_AndInheritedMembers()
    {
        var data = ToolResult.Data(await Load().GetExternalTypeInfoAsync(
            "System.ArgumentException", includeXmlDocs: false, maxMembers: 1000));

        var members = (JArray)data["members"]!;
        var constructors = members.Where(m => m["kind"]!.Value<string>() == "Constructor").ToList();
        constructors.Should().HaveCount(VisibleConstructorCount(typeof(ArgumentException)));
        constructors.Should().OnlyContain(c => c["signature"]!.Value<string>()!.StartsWith("System.ArgumentException.ArgumentException("));
        var helpLink = members.Single(m => m["name"]!.Value<string>() == "HelpLink");
        helpLink["isInherited"]!.Value<bool>().Should().BeTrue("HelpLink is declared by System.Exception");
    }

    [Fact]
    public async Task MaxMembers_OutOfDomain_IsInvalidParameter()
    {
        var error = ToolResult.Error(
            await Load().GetExternalTypeInfoAsync("System.ArgumentException", maxMembers: -1), ErrorCodes.InvalidParameter);

        error["Context"]!["parameter"]!.Value<string>().Should().Be("maxMembers");
    }

    [Fact]
    public async Task Surface_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("namespace Probe { public class P { } }", "P.cs");
        var host = new InMemoryToolHost(workspace);

        var data = await host.DataAsync("get_external_type_info", new
        {
            typeName = "System.ArgumentException", includeXmlDocs = false, maxMembers = 1000
        });
        await host.ErrorAsync("get_external_type_info", new { typeName = "System.ArgumentException", maxMembers = 0 }, ErrorCodes.InvalidParameter);

        ((JArray)data["members"]!).Count(m => m["kind"]!.Value<string>() == "Constructor")
            .Should().Be(VisibleConstructorCount(typeof(ArgumentException)));
    }
}
