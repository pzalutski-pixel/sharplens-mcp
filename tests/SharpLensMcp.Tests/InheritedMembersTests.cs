using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Inherited-member walks across get_type_members, get_method_signature, and
// get_external_type_info: an override and the base it replaces collapse into one
// entry (no inflated overload count), and interface members — including
// default-interface-members — are part of the inherited surface.
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
    public async Task GetTypeMembers_IncludeInherited_IncludesInterfaceDefaultMembersAndDedupesOverride()
    {
        var result = await LoadProbe().GetTypeMembersAsync("DerivedHost", includeInherited: true, maxResults: 200);

        var members = (JObject.FromObject(result)["data"]!["members"] as JArray)!
            .Select(m => m["name"]!.Value<string>()!)
            .ToList();

        members.Should().Contain("Farewell",
            "a default-interface-member is part of the inherited surface");
        members.Count(n => n == "Compute").Should().Be(1,
            "the override and the base method it replaces collapse into one member");
    }

    [Fact]
    public async Task GetExternalTypeInfo_IncludeInherited_DedupesOverrideAndIncludesInterfaceMembers()
    {
        var result = await LoadProbe().GetExternalTypeInfoAsync(
            "Probe.DerivedHost", includeInherited: true, includeXmlDocs: false, maxMembers: 200);

        var members = (JObject.FromObject(result)["data"]!["members"] as JArray)!
            .Select(m => m["name"]!.Value<string>()!)
            .ToList();

        members.Count(n => n == "Compute").Should().Be(1,
            "the override and the base method collapse into one member");
        members.Should().Contain("Farewell",
            "default-interface-members are part of the inherited surface");
    }
}
