using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Verifies find_god_objects against the GodObjectFixture: one engineered
// god-class (25 type deps + 25 members) and one focused class (2 type deps + 3 members).
public class GodObjectTests : RoslynServiceTestBase
{
    [Fact]
    public async Task FindGodObjects_FlagsEngineeredGodClass_NotSmallFocused()
    {
        var result = await Service.FindGodObjectsAsync(
            projectName: "SharpLensMcp.Tests",
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: 50);
        AssertSuccess(result);

        var data = GetData(result);
        var candidates = data["candidates"] as JArray;
        candidates.Should().NotBeNullOrEmpty();

        var typeNames = candidates!.Select(c => c["typeName"]?.Value<string>() ?? "").ToList();

        typeNames.Should().Contain(n => n.EndsWith("GodObjectFixtureClass"),
            "GodObjectFixtureClass has 25 fields of 25 distinct types — definitionally a god-object under these thresholds");
        typeNames.Should().NotContain(n => n.EndsWith("SmallFocusedFixture"),
            "SmallFocusedFixture has 3 members and 2 type deps; under threshold");
    }

    [Fact]
    public async Task FindGodObjects_GodClassHasExpectedCouplingNumbers()
    {
        var result = await Service.FindGodObjectsAsync(
            projectName: "SharpLensMcp.Tests",
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: 50);
        AssertSuccess(result);

        var data = GetData(result);
        var candidates = data["candidates"] as JArray;
        var god = candidates!.First(c => c["typeName"]!.Value<string>()!.EndsWith("GodObjectFixtureClass"));

        god["efferentCoupling"]?.Value<int>().Should().BeGreaterOrEqualTo(25,
            "fixture has 25 fields, each of a distinct GodObjectTypeNN type");
        god["memberCount"]?.Value<int>().Should().BeGreaterOrEqualTo(25,
            "fixture has 25 fields");
    }

    [Fact]
    public async Task FindGodObjects_RespectsMaxResults()
    {
        var result = await Service.FindGodObjectsAsync(
            projectName: "SharpLensMcp.Tests",
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: 1);
        AssertSuccess(result);

        var data = GetData(result);
        var candidates = data["candidates"] as JArray;
        candidates!.Count.Should().BeLessOrEqualTo(1);
    }

    [Fact]
    public async Task FindGodObjects_RespectsThresholdParameters()
    {
        // Set thresholds so high nothing qualifies.
        var result = await Service.FindGodObjectsAsync(
            projectName: "SharpLensMcp.Tests",
            minEfferentCoupling: 10_000,
            minMembers: 10_000,
            maxResults: 50);
        AssertSuccess(result);

        var data = GetData(result);
        var candidates = data["candidates"] as JArray;
        candidates!.Count.Should().Be(0, "no type meets these absurd thresholds");
    }
}
