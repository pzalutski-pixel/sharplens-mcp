using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Descending sorts and truncations must break ties deterministically by symbol
// display name, so the set kept at the maxResults boundary is fixed for equal-score
// inputs (List.Sort is not stable, and a walk-order cap is order-dependent).
public class SortDeterminismTests
{
    [Fact]
    public async Task FindUntestedCode_EqualComplexity_OrdersByDisplayName()
    {
        // Three public methods, each cyclomatic 1, declared in non-alphabetical order.
        const string code = @"
namespace Demo;
public class T
{
    public int Zeta() => 1;
    public int Mid() => 2;
    public int Alpha() => 3;
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "T.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindUntestedCodeAsync(includeProperties: false);
        var names = (JObject.FromObject(result)["data"]!["uncoveredSymbols"] as JArray)!
            .Select(s => s["fullName"]!.Value<string>()!)
            .Where(n => n.StartsWith("Demo.T."))
            .ToList();

        names.Should().Equal(new[] { "Demo.T.Alpha()", "Demo.T.Mid()", "Demo.T.Zeta()" },
            "equal-complexity members tie-break by display name, regardless of declaration order");
    }

    [Fact]
    public async Task FindNamingViolations_Truncation_KeepsTheDisplayNameSortedPrefix()
    {
        // Three lowercase type names violate the PascalCase rule; declared in
        // non-alphabetical order. With maxResults=2 the kept set must be the two
        // alphabetically-first symbols, not whichever two the walk reached first.
        const string code = @"
namespace Demo;
public class zebra { }
public class apple { }
public class mango { }
";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Types.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.FindNamingViolationsAsync(maxResults: 2))["data"]!;
        var symbols = (data["violations"] as JArray)!
            .Select(v => v["symbol"]!.Value<string>()!)
            .ToList();

        data["violationCount"]!.Value<int>().Should().Be(3, "all three lowercase types violate the rule");
        symbols.Should().Equal(new[] { "Demo.apple", "Demo.mango" },
            "deterministic truncation keeps the two display-name-first symbols, in order");
    }
}
