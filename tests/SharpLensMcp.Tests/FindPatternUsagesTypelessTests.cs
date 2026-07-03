using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_pattern_usages attributes a type-less recursive/positional/property
// pattern to the type of the expression it is matched against.
public class FindPatternUsagesTypelessTests
{
    [Fact]
    public async Task TypelessRecursivePattern_MatchesByInputExpressionType()
    {
        const string code = @"
namespace N
{
    public class Order { public int Total { get; set; } }

    public class Check
    {
        public bool Big(Order order) => order is { Total: > 100 };
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Order.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.FindPatternUsagesAsync("Order"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var kinds = (json["data"]!["matches"] as JArray)!
            .Select(m => m["kind"]!.Value<string>()).ToList();
        kinds.Should().Contain("recursivePattern",
            "the type-less `order is { Total: > 100 }` pattern matches against Order via its input expression's type");
    }

    [Fact]
    public async Task NestedTypelessSubpattern_MatchesByMemberType()
    {
        const string code = @"
namespace N
{
    public class Customer { public string Name { get; set; } = """"; }
    public class Order { public Customer Buyer { get; set; } = new(); }

    public class Check
    {
        public bool Named(Order order) => order is { Buyer: { Name: ""x"" } };
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Order.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.FindPatternUsagesAsync("Customer"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var kinds = (json["data"]!["matches"] as JArray)!
            .Select(m => m["kind"]!.Value<string>()).ToList();
        kinds.Should().Contain("recursivePattern",
            "the nested type-less `{ Name: \"x\" }` subpattern matches against Customer via Order.Buyer's type, not just the three top-level positions");
    }
}
