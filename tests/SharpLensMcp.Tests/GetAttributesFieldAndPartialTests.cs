using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_attributes must enumerate declarations the same way find_attribute_usages does:
// report field/event-field attributes (GetDeclaredSymbol on the field declaration node
// returns null — its variables must be resolved individually), and count a partial type's
// attribute once rather than once per partial part.
public class GetAttributesFieldAndPartialTests
{
    private const string Code = @"
using System;
namespace Demo
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class MarkAttribute : Attribute { }

    public partial class Widget { }

    [Mark]
    public partial class Widget { public int Used() => 1; }

    public class Holder
    {
        [Mark]
        private int _field;
    }
}";

    private static RoslynService Loaded()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Attrs.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task FieldAttribute_Reported_AndPartialTypeCountedOnce()
    {
        var service = Loaded();
        var data = JObject.FromObject(await service.GetAttributesAsync("Mark"))["data"]!;
        var symbols = (data["symbols"] as JArray)!;
        var names = symbols.Select(s => s["symbolName"]!.Value<string>()).ToList();

        names.Should().Contain("_field", "a [Mark] on a field must be reported, not dropped");
        names.Count(n => n == "Widget").Should().Be(1,
            "a partial type's attribute is counted once, not once per partial part");
        data["totalFound"]!.Value<int>().Should().Be(symbols.Count,
            "the per-application count must equal the returned-symbol count");
    }

    [Fact]
    public async Task MatchCount_AgreesWithFindAttributeUsages()
    {
        var service = Loaded();
        var getAttrs = (JObject.FromObject(await service.GetAttributesAsync("Mark"))["data"]!["symbols"] as JArray)!.Count;
        var findUsages = (JObject.FromObject(await service.FindAttributeUsagesAsync("Mark"))["data"]!["usages"] as JArray)!.Count;

        getAttrs.Should().Be(2, "Widget (one partial type) and Holder._field each carry one [Mark]");
        getAttrs.Should().Be(findUsages, "get_attributes and find_attribute_usages must report the same matches");
    }
}
