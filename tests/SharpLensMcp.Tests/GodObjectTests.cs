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
    public async Task FindGodObjects_GodClassHasExpectedCouplingNumbersAndFullEntryShape()
    {
        var result = await Service.FindGodObjectsAsync(
            projectName: "SharpLensMcp.Tests",
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: 50);
        AssertSuccess(result);

        var data = GetData(result);

        // The top-level threshold field must round-trip the caller's arguments. Without
        // this, a regression that returns wrong thresholds in the response (decoupling
        // the displayed thresholds from the applied ones) would go undetected.
        var threshold = data["threshold"] as JObject;
        threshold.Should().NotBeNull();
        threshold!["efferent"]?.Value<int>().Should().Be(20);
        threshold["members"]?.Value<int>().Should().Be(20);

        var candidates = data["candidates"] as JArray;
        var god = candidates!.First(c => c["typeName"]!.Value<string>()!.EndsWith("GodObjectFixtureClass"));

        god["efferentCoupling"]?.Value<int>().Should().BeGreaterOrEqualTo(25,
            "fixture has 25 fields, each of a distinct GodObjectTypeNN type");
        god["memberCount"]?.Value<int>().Should().BeGreaterOrEqualTo(25,
            "fixture has 25 fields");

        // Every entry's shape: afferentCoupling, score, location must be present.
        god["afferentCoupling"]?.Type.Should().Be(JTokenType.Integer,
            "afferentCoupling is always reported as an integer");
        // score is a numeric scalar; the impl computes it from coupling/member counts.
        // Either int or float — assert >0 covers both via .Value<double>().
        god["score"]?.Value<double>().Should().BeGreaterThan(0,
            "the god class must have a positive numeric score");
        god["location"].Should().NotBeNull(
            "every candidate must surface its declaration location");
    }

    [Fact]
    public async Task FindGodObjects_RespectsMaxResults_FillsExactlyOne()
    {
        var result = await Service.FindGodObjectsAsync(
            projectName: "SharpLensMcp.Tests",
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: 1);
        AssertSuccess(result);

        var data = GetData(result);
        var candidates = data["candidates"] as JArray;
        // GodObjectFixtureClass deterministically meets the thresholds — Count must
        // FILL the cap, not be 0. Otherwise "maxResults" looks like "max OR fewer
        // because nothing qualified" which is the silent-pass pattern.
        candidates!.Count.Should().Be(1, "maxResults=1 must fill the cap given a qualifying candidate");
        candidates![0]["typeName"]!.Value<string>().Should().EndWith("GodObjectFixtureClass");
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

    [Fact]
    public async Task FindGodObjects_WithoutProjectFilter_FindsGodAcrossAllProjects()
    {
        // No projectName → scans ALL projects (RoslynService.Quality.cs:326-328).
        // The god-object fixture lives in SharpLensMcp.Tests and must still be flagged.
        var result = await Service.FindGodObjectsAsync(
            projectName: null,
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: 50);
        AssertSuccess(result);

        var data = GetData(result);
        var candidates = data["candidates"] as JArray;
        candidates.Should().NotBeNullOrEmpty();
        candidates!.Any(c => c["typeName"]?.Value<string>()?.EndsWith("GodObjectFixtureClass") == true)
            .Should().BeTrue("the god class must surface in a no-filter scan");
    }
}
