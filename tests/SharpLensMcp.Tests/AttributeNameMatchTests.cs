using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_attribute_usages must strip only a TRAILING "Attribute" suffix, not every
// occurrence: "FooAttributeBarAttribute" reduces to "FooAttributeBar", so a query for
// the all-stripped "FooBar" must NOT match it (the old Replace("Attribute","") did).
public class AttributeNameMatchTests
{
    private const string Code = @"
using System;
namespace Demo
{
    public class FooAttributeBarAttribute : Attribute { }

    [FooAttributeBar]
    public class Decorated { }
}";

    private static async Task<List<string>> UsagesFor(string attributeName)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Attr.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindAttributeUsagesAsync(attributeName);
        return (JObject.FromObject(result)["data"]!["usages"] as JArray)!
            .Select(u => u["symbolName"]!.Value<string>()!).ToList();
    }

    [Fact]
    public async Task TrailingStrippedShortName_Matches()
    {
        var names = await UsagesFor("FooAttributeBar");
        names.Should().Contain("Decorated",
            "the trailing-stripped short name of FooAttributeBarAttribute is FooAttributeBar");
    }

    [Fact]
    public async Task AllStrippedName_DoesNotMatch()
    {
        var names = await UsagesFor("FooBar");
        names.Should().NotContain("Decorated",
            "stripping every 'Attribute' occurrence (FooBar) is wrong — only the trailing suffix is stripped");
    }
}
