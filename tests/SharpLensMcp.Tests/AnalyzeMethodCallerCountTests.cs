using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// analyze_method.totalCallerGroups counts distinct calling symbols (a grouped
// overview), while find_callers.totalCallers counts individual call sites. The
// fixture calls Target.Hit twice from CallerOne.M and once from CallerTwo.N, so
// the two counts genuinely differ — 2 groups vs. 3 sites — and the same field
// name never means two different units.
public class AnalyzeMethodCallerCountTests
{
    private const string Code = @"
namespace Demo;

public class Target { public void Hit() { } }

public class CallerOne
{
    public void M()
    {
        var t = new Target();
        t.Hit();
        t.Hit();
    }
}

public class CallerTwo
{
    public void N() => new Target().Hit();
}
";

    [Fact]
    public async Task AnalyzeMethod_TotalCallerGroups_CountsCallingSymbolsNotSites()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Callers.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.AnalyzeMethodAsync("Target", "Hit", includeCallers: true);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;

        data["totalCallerGroups"]!.Value<int>().Should().Be(2,
            "Target.Hit is called from two distinct methods (CallerOne.M and CallerTwo.N)");
        (data["callers"] as JArray)!.Count.Should().Be(2, "one entry per distinct calling symbol");
        data["callersShown"]!.Value<int>().Should().Be(2);
        data["totalCallers"].Should().BeNull(
            "the grouped count must not reuse find_callers' site-count field name");
    }

    [Fact]
    public async Task FindCallers_TotalCallers_CountsSitesNotGroups()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Callers.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var search = await service.SearchSymbolsAsync("Hit", kind: "Method", maxResults: 10);
        var loc = JObject.FromObject(search)["data"]!["results"]!.First()!["location"]!;

        var result = await service.FindCallersAsync(
            loc["filePath"]!.Value<string>()!, loc["line"]!.Value<int>(), loc["column"]!.Value<int>());
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        (json["data"]!["callers"] as JArray)!.Count.Should().Be(3,
            "three call sites: two in CallerOne.M, one in CallerTwo.N");
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(3,
            "find_callers' totalCallers (meta TotalCount) counts sites, not the two calling methods");
    }
}
