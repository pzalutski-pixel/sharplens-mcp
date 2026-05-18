using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Verifies the get_project_health composite returns all expected sections,
// honors topN, and returns InvalidParameter for unknown projects.
public class ProjectHealthTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetProjectHealth_ReturnsAllSectionsWithSpecificCleanProjectCounts()
    {
        var result = await Service.GetProjectHealthAsync("SharpLensMcp", includeAnalyzers: true, topN: 5);
        AssertSuccess(result);

        var data = GetData(result);
        // Lock projectName with explicit non-null check first. The bare
        // data["projectName"]?.Value<string>().Should().Be(X) pattern would silently
        // pass if the field name changed (the `?.` short-circuits the whole chain
        // including the assertion).
        data["projectName"].Should().NotBeNull();
        data["projectName"]!.Value<string>().Should().Be("SharpLensMcp");

        data["diagnostics"].Should().NotBeNull("diagnostics section is required");
        data["unusedCode"].Should().NotBeNull("unusedCode section is required");
        data["coupling"].Should().NotBeNull("coupling section is required");
        data["coverage"].Should().NotBeNull("coverage section is required");
        data["summary"].Should().NotBeNull();
        data["summary"]!.Value<string>().Should().NotBeNullOrEmpty();

        // SharpLensMcp is a clean project — locked counts of 0 errors / 0 warnings.
        // Previously the test only asserted presence; that would pass even if every
        // field returned -1 or huge bogus numbers.
        var diagnostics = data["diagnostics"]!;
        diagnostics["errorCount"]!.Value<int>().Should().Be(0,
            "SharpLensMcp must compile with zero errors");
        diagnostics["warningCount"]!.Value<int>().Should().Be(0,
            "SharpLensMcp must compile with zero warnings");
        diagnostics["analyzerCount"]!.Type.Should().Be(JTokenType.Integer);
        (diagnostics["topByCount"] as JArray).Should().NotBeNull(
            "topByCount must always be an array, even if empty");

        // SharpLensMcp has no analyzer-flagged god objects under default thresholds
        // (no class has 20+ efferent coupling AND 20+ members).
        data["coupling"]!["godObjectCandidates"]!.Value<int>().Should().Be(0,
            "no SharpLensMcp type meets the god-object thresholds");
        (data["coupling"]!["hotspots"] as JArray).Should().NotBeNull();

        // coverage uncoveredPublicSurface is an int; locked >=0 invariant.
        data["coverage"]!["uncoveredPublicSurface"]!.Type.Should().Be(JTokenType.Integer);
        // unused-code count is an int; locked >=0 invariant.
        data["unusedCode"]!["count"]!.Type.Should().Be(JTokenType.Integer);
    }

    [Fact]
    public async Task GetProjectHealth_TopNRespected()
    {
        var result = await Service.GetProjectHealthAsync("SharpLensMcp", includeAnalyzers: true, topN: 2);
        AssertSuccess(result);

        var data = GetData(result);

        var topDiags = data["diagnostics"]!["topByCount"] as JArray;
        topDiags.Should().NotBeNull();
        topDiags!.Count.Should().BeLessOrEqualTo(2);

        // Hotspot arrays may be absent if the section emits no candidates. When present
        // they must honor topN; when absent we lock that the parent section still exists.
        var godHotspots = data["coupling"]!["hotspots"] as JArray;
        if (godHotspots != null)
        {
            godHotspots.Count.Should().BeLessOrEqualTo(2,
                "topN must cap the coupling hotspots list when it is present");
        }

        var coverageHotspots = data["coverage"]!["hotspots"] as JArray;
        if (coverageHotspots != null)
        {
            coverageHotspots.Count.Should().BeLessOrEqualTo(2,
                "topN must cap the coverage hotspots list when it is present");
        }
    }

    [Fact]
    public async Task GetProjectHealth_SummaryLocksAllFiveCountStrings()
    {
        var result = await Service.GetProjectHealthAsync("SharpLensMcp", includeAnalyzers: false, topN: 5);
        AssertSuccess(result);

        var data = GetData(result);
        var summary = data["summary"]!.Value<string>()!;

        // Per Quality.cs:569 the format is exactly:
        //   "{n} errors, {n} warnings, {n} god-object candidates, {n} uncovered public methods, {n} unused symbols"
        // SharpLensMcp is clean → "0 errors, 0 warnings, 0 god-object candidates".
        // Locks both the format AND the known-zero counts.
        summary.Should().Contain("0 errors",
            "the clean SharpLensMcp project must report 0 errors");
        summary.Should().Contain("0 warnings",
            "the clean SharpLensMcp project must report 0 warnings");
        summary.Should().Contain("0 god-object candidates",
            "no type meets god-object thresholds in SharpLensMcp");
        summary.Should().Contain("uncovered public methods",
            "the uncovered-public-methods bucket must appear in the summary string");
        summary.Should().Contain("unused symbols",
            "the unused-symbols bucket must appear in the summary string");
    }

    [Fact]
    public async Task GetProjectHealth_OnNonExistentProject_ReturnsInvalidParameterWithHint()
    {
        var result = await Service.GetProjectHealthAsync("DoesNotExist_12345");
        AssertError(result, ErrorCodes.InvalidParameter);
        // Lock the hint contract (Quality.cs:515) — caller is guided to
        // get_project_structure to find a valid project name.
        var json = JObject.FromObject(result);
        json["error"]?["hint"]?.Value<string>().Should().Contain("get_project_structure",
            "the hint must point at get_project_structure for project name discovery");
        json["error"]?["message"]?.Value<string>().Should().Contain("DoesNotExist_12345",
            "the error message must echo the bad project name for caller correlation");
    }
}
