using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Verifies the get_project_health composite returns all expected sections,
// honors topN, and returns InvalidParameter for unknown projects.
public class ProjectHealthTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetProjectHealth_ReturnsAllSections()
    {
        var result = await Service.GetProjectHealthAsync("SharpLensMcp", includeAnalyzers: true, topN: 5);
        AssertSuccess(result);

        var data = GetData(result);
        data["projectName"]?.Value<string>().Should().Be("SharpLensMcp");
        data["diagnostics"].Should().NotBeNull("diagnostics section is required");
        data["unusedCode"].Should().NotBeNull("unusedCode section is required");
        data["coupling"].Should().NotBeNull("coupling section is required");
        data["coverage"].Should().NotBeNull("coverage section is required");
        data["summary"]?.Value<string>().Should().NotBeNullOrEmpty();

        data["diagnostics"]!["errorCount"].Should().NotBeNull();
        data["diagnostics"]!["warningCount"].Should().NotBeNull();
        data["diagnostics"]!["analyzerCount"].Should().NotBeNull();
        data["coupling"]!["godObjectCandidates"].Should().NotBeNull();
        data["coverage"]!["uncoveredPublicSurface"].Should().NotBeNull();
        data["unusedCode"]!["count"].Should().NotBeNull();
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
    public async Task GetProjectHealth_OnNonExistentProject_ReturnsInvalidParameter()
    {
        var result = await Service.GetProjectHealthAsync("DoesNotExist_12345");
        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task GetProjectHealth_SummaryReflectsCounts()
    {
        var result = await Service.GetProjectHealthAsync("SharpLensMcp", includeAnalyzers: false, topN: 5);
        AssertSuccess(result);

        var data = GetData(result);
        var summary = data["summary"]!.Value<string>()!;

        // Summary should mention the four dimensions; format from impl is
        // "{n} errors, {n} warnings, {n} god-object candidates, {n} uncovered public methods, {n} unused symbols"
        summary.Should().Contain("error");
        summary.Should().Contain("warning");
        summary.Should().Contain("uncovered");
    }
}
