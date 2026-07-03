using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// check_architecture's deny rules can target referenced/metadata namespaces
// (package and BCL), not only in-solution source types.
public class ArchitectureMetadataEdgeTests
{
    [Fact]
    public async Task DenyRuleAgainstBclNamespace_FlagsTheMetadataEdge()
    {
        const string code = @"
namespace MyApp
{
    public class Service
    {
        public System.Text.StringBuilder Build() => new System.Text.StringBuilder();
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Service.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.CheckArchitectureAsync(
            new List<ArchitectureRule> { new() { From = "MyApp*", Deny = "System.Text*" } });
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var violations = (json["data"]!["violations"] as JArray)!;
        violations.Should().Contain(
            v => v["toType"]!.Value<string>()!.Contains("StringBuilder"),
            "a deny rule against the BCL System.Text namespace must flag the edge to StringBuilder");
    }

    [Fact]
    public async Task DenyRule_FlagsInheritanceAndGenericArgumentEdges()
    {
        const string code = @"
namespace Banned { public class BaseThing { } public class Item { } }
namespace MyApp
{
    public class Derived : Banned.BaseThing { }
    public class Holder { public System.Collections.Generic.List<Banned.Item> Items = new(); }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Edges.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.CheckArchitectureAsync(
            new List<ArchitectureRule> { new() { From = "MyApp*", Deny = "Banned*" } });
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var toTypes = (json["data"]!["violations"] as JArray)!
            .Select(v => v["toType"]!.Value<string>()!).ToList();
        toTypes.Should().Contain(t => t.Contains("BaseThing"),
            "an inheritance edge (Derived : Banned.BaseThing) must be a type-to-type edge");
        toTypes.Should().Contain(t => t.Contains("Item"),
            "a generic type argument (List<Banned.Item>) must be a type-to-type edge");
    }

    [Fact]
    public async Task ImplicitObjectBase_IsNotReportedAsAnEdge()
    {
        // A plain type implicitly inherits System.Object; that is not a dependency edge,
        // so a {deny:'System*'} rule must NOT fire for it (the over-report regression).
        const string code = @"
namespace MyApp
{
    public class Plain { public int Value; }
    public struct Val { public int N; }
    public enum Color { Red }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Plain.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.CheckArchitectureAsync(
            new List<ArchitectureRule> { new() { From = "MyApp*", Deny = "System*" } });
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var toTypes = (json["data"]!["violations"] as JArray)!
            .Select(v => v["toType"]!.Value<string>()!).ToList();
        toTypes.Should().NotContain(t => t.Contains("System.Object"),
            "the implicit object base is not an edge");
        toTypes.Should().NotContain(t => t.Contains("System.ValueType"),
            "the implicit struct base is not an edge");
        toTypes.Should().NotContain(t => t.Contains("System.Enum"),
            "the implicit enum base is not an edge");
        // System.Int32 (the int field) IS a real edge, so the rule legitimately fires on it;
        // assert specifically that the implicit bases above do not.
    }
}
