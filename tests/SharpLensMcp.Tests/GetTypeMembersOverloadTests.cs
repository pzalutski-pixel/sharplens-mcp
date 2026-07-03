using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory tests for get_type_members overload reporting. Overloads still
// collapse into one entry per member name, but the entry carries an
// overloadCount field naming how many overloads share the name — previously
// the collapse silently dropped that information and an agent had no signal
// that get_method_signature offers more overloads to inspect.
public class GetTypeMembersOverloadTests
{
    private const string OverloadCode = @"
namespace Production
{
    public class OverloadHost
    {
        public void Ping() { }
        public void Ping(int count) { }
        public int Solo() => 1;
        public string Label { get; set; } = """";
    }
}";

    private const string OverrideCode = @"
namespace Production
{
    public class BaseRunner
    {
        public virtual void Run() { }
    }

    public class DerivedRunner : BaseRunner
    {
        public override void Run() { }
    }
}";

    private static async Task<JToken> GetMembersDataAsync(
        string code, string typeName, bool includeInherited = false, string verbosity = "compact")
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.GetTypeMembersAsync(
            typeName, includeInherited: includeInherited, verbosity: verbosity);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }

    [Fact]
    public async Task GetTypeMembers_OverloadedMethod_OneEntryCarryingOverloadCount()
    {
        var data = await GetMembersDataAsync(OverloadCode, "OverloadHost");
        var members = (data["members"] as JArray)!;

        var pingEntries = members.Where(m => m["name"]!.Value<string>() == "Ping").ToList();
        pingEntries.Should().HaveCount(1, "overloads collapse into one entry per name");
        pingEntries[0]["overloadCount"]!.Value<int>().Should().Be(2,
            "Ping has two overloads and the entry must say so");

        var solo = members.Single(m => m["name"]!.Value<string>() == "Solo");
        solo["overloadCount"]!.Value<int>().Should().Be(1);

        // Property entries carry no overload notion — the field is method-specific.
        var label = members.Single(m => m["name"]!.Value<string>() == "Label");
        label["overloadCount"].Should().BeNull();

        // totalMembers counts name+kind groups: Ping (collapsed), Solo, Label,
        // and Label's get_Label/set_Label accessors, which GetMembers() surfaces
        // as declared method symbols.
        data["totalMembers"]!.Value<int>().Should().Be(5);
    }

    [Fact]
    public async Task GetTypeMembers_SummaryVerbosity_MethodEntriesCarryOverloadCount()
    {
        var data = await GetMembersDataAsync(OverloadCode, "OverloadHost", verbosity: "summary");
        var members = (data["members"] as JArray)!;

        var ping = members.Single(m => m["name"]!.Value<string>() == "Ping");
        ping["overloadCount"]!.Value<int>().Should().Be(2,
            "summary verbosity must still surface the overload count for methods");
    }

    [Fact]
    public async Task GetTypeMembers_ConstructorKind_WithInherited_ExcludesBaseConstructors()
    {
        const string code = @"
namespace Production
{
    public class BaseWithCtor
    {
        public BaseWithCtor(int x) { }
    }
    public class DerivedWithCtor : BaseWithCtor
    {
        public DerivedWithCtor(string y) : base(0) { }
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.GetTypeMembersAsync(
            "DerivedWithCtor", includeInherited: true, memberKind: "Constructor");
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;

        var members = (data["members"] as JArray)!;
        members.Should().HaveCount(1, "constructors share the .ctor name and group into one entry");
        members[0]["overloadCount"]!.Value<int>().Should().Be(1,
            "constructors are NOT inherited — only DerivedWithCtor's own ctor counts, not BaseWithCtor's");
        data["totalMembers"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task GetTypeMembers_DistinctInterfacesSameSignature_AreNotCollapsed()
    {
        const string code = @"
namespace Production
{
    public interface IAlpha { void Do(); }
    public interface IBeta { void Do(); }
    public interface ICombined : IAlpha, IBeta { }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.GetTypeMembersAsync("ICombined", includeInherited: true);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var members = (json["data"]!["members"] as JArray)!;
        var doEntry = members.Single(m => m["name"]!.Value<string>() == "Do");
        doEntry["overloadCount"]!.Value<int>().Should().Be(2,
            "IAlpha.Do() and IBeta.Do() are distinct interface members and must not collapse into a single entry");
    }

    [Fact]
    public async Task GetTypeMembers_InheritedOverride_DoesNotCountAsOverload()
    {
        var data = await GetMembersDataAsync(OverrideCode, "DerivedRunner", includeInherited: true);
        var members = (data["members"] as JArray)!;

        var runEntries = members.Where(m => m["name"]!.Value<string>() == "Run").ToList();
        runEntries.Should().HaveCount(1,
            "the override and the base virtual it replaces are one member, not two");
        runEntries[0]["overloadCount"]!.Value<int>().Should().Be(1,
            "an override sharing the base signature is not an overload");
        runEntries[0]["isOverride"]!.Value<bool>().Should().BeTrue(
            "the most-derived occurrence must be the one kept");
    }
}
