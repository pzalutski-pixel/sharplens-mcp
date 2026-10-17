using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Inherited-member walks across get_type_members, get_method_signature, and
// get_external_type_info: an override and the base it replaces collapse into one
// entry (no inflated overload count), and a class does not inherit an interface's
// members — a default interface member is callable only through the interface
// (`new DerivedHost().Farewell()` is CS1061), so it is not part of the class's surface.
public class InheritedMembersTests
{
    private const string Probe = """
        namespace Probe;

        public interface IGreeter
        {
            string Greet();
            string Farewell() => "bye";
        }

        public class BaseHost
        {
            public virtual int Compute(int x) => x;
        }

        public class DerivedHost : BaseHost, IGreeter
        {
            public override int Compute(int x) => x + 1;
            public string Greet() => "hi";
        }
        """;

    private static RoslynService LoadProbe()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Probe, "Probe.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task GetMethodSignature_OverriddenMethod_DoesNotInflateOverloadCount()
    {
        var result = await LoadProbe().GetMethodSignatureAsync("DerivedHost", "Compute");

        var data = JObject.FromObject(result)["data"]!;
        data["overloadCount"]!.Value<int>().Should().Be(1,
            "DerivedHost.Compute overrides BaseHost.Compute — the same method, not two overloads");
    }

    [Fact]
    public async Task GetTypeMembers_IncludeInherited_DedupesOverride_AndExcludesInterfaceDefaultMembers()
    {
        var result = await LoadProbe().GetTypeMembersAsync("DerivedHost", includeInherited: true, maxResults: 200);

        var members = (JObject.FromObject(result)["data"]!["members"] as JArray)!
            .Select(m => m["name"]!.Value<string>()!)
            .ToList();

        members.Should().Equal(new[] { "Compute", "Greet" },
            "the override replaces the base method it overrides, and the default interface member Farewell is reachable only through IGreeter");
    }

    [Fact]
    public async Task GetExternalTypeInfo_IncludeInherited_DedupesOverride_AndExcludesInterfaceDefaultMembers()
    {
        var result = await LoadProbe().GetExternalTypeInfoAsync(
            "Probe.DerivedHost", includeInherited: true, includeXmlDocs: false, maxMembers: 200);

        var members = (JObject.FromObject(result)["data"]!["members"] as JArray)!
            .Select(m => m["name"]!.Value<string>()!)
            .ToList();

        members.Should().Equal(new[] { "Compute", "Greet" },
            "the override and the base method collapse into one member, and the default interface member is not the class's surface");
    }
}
