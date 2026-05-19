using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// In-memory test for find_attribute_usages covering the type-level attribute
// branch at Discovery.cs:28-42. The impl loops type.GetAttributes() before
// member.GetAttributes(), so a class-level attribute must surface with
// symbolKind="Type" (not "Method"), and a method-level attribute with the
// same simple name must surface separately with symbolKind="Method".
public class FindAttributeUsagesTypeTests
{
    private const string Code = @"
namespace MyCollection
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method)]
    public class TaggedAttribute : System.Attribute
    {
        public TaggedAttribute(string name) { Name = name; }
        public string Name { get; }
    }
}

namespace Production
{
    [MyCollection.Tagged(""ClassLevel"")]
    public class DecoratedClass
    {
        [MyCollection.Tagged(""MethodLevel"")]
        public void DecoratedMethod() { }

        public void Plain() { }
    }

    public class Untouched { }
}";

    [Fact]
    public async Task FindAttributeUsages_EmitsTypeKind_ForClassLevelAttribute()
    {
        var(workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.FindAttributeUsagesAsync(attributeName: "Tagged");
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var usages = (json["data"]!["usages"] as JArray)!;
        var typeUsage = usages.FirstOrDefault(u => u["symbolKind"]!.Value<string>() == "Type");
        typeUsage.Should().NotBeNull("Discovery.cs:28-42 must emit a Type-kinded entry for the class-level [Tagged] attribute");
        typeUsage!["symbolName"]!.Value<string>()!.Should().Contain("DecoratedClass",
            "the Type entry must name the decorated class");
        typeUsage["attributeName"]!.Value<string>().Should().Be("TaggedAttribute");
        typeUsage["arguments"]!.Values<string>().Should().Contain(a => a!.Contains("ClassLevel"),
            "the constructor argument string must round-trip the literal");
    }

    [Fact]
    public async Task FindAttributeUsages_EmitsMethodKind_ForMethodLevelAttribute()
    {
        var(workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.FindAttributeUsagesAsync(attributeName: "Tagged");
        var json = JObject.FromObject(result);
        var usages = (json["data"]!["usages"] as JArray)!;
        var methodUsage = usages.FirstOrDefault(u =>
            u["symbolKind"]!.Value<string>() == "Method" &&
            u["symbolName"]!.Value<string>()!.Contains("DecoratedMethod"));
        methodUsage.Should().NotBeNull("Discovery.cs:44-61 must emit a Method-kinded entry for the method-level [Tagged] attribute");
        methodUsage!["arguments"]!.Values<string>().Should().Contain(a => a!.Contains("MethodLevel"));
    }

    [Fact]
    public async Task FindAttributeUsages_TotalCount_CountsBothTypeAndMethodHits()
    {
        var(workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var result = await service.FindAttributeUsagesAsync(attributeName: "Tagged");
        var json = JObject.FromObject(result);
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(2,
            "the impl increments totalFound for both the type-level and method-level [Tagged] hits");
    }
}
