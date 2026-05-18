using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Verifies get_project_health (Quality.cs:501-621) returns every documented
// section, honors topN, exercises the includeAnalyzers gate, matches project
// names case-insensitively, and returns InvalidParameter with a guiding hint
// for unknown projects.
public class ProjectHealthTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetProjectHealth_ReturnsAllSectionsWithSpecificCleanProjectCounts()
    {
        var result = await Service.GetProjectHealthAsync("SharpLensMcp", includeAnalyzers: true, topN: 5);
        AssertSuccess(result);

        var data = GetData(result);
        // Lock the top-level shape. NotBeNull-first defeats the null-conditional
        // silent-pass — `data["X"]?.Value<T>().Should().Be(Y)` skips entirely when X
        // is missing.
        data["projectName"]!.Value<string>().Should().Be("SharpLensMcp");

        // Every documented section must be present (Quality.cs:572-612).
        var diagnostics = data["diagnostics"]!;
        var unusedCode = data["unusedCode"]!;
        var coupling = data["coupling"]!;
        var coverage = data["coverage"]!;
        data["summary"]!.Value<string>().Should().NotBeNullOrEmpty();

        // SharpLensMcp is a clean project — locked counts of 0 errors / 0 warnings.
        diagnostics["errorCount"]!.Value<int>().Should().Be(0,
            "SharpLensMcp must compile with zero errors");
        diagnostics["warningCount"]!.Value<int>().Should().Be(0,
            "SharpLensMcp must compile with zero warnings");
        diagnostics["analyzerCount"]!.Value<int>().Should().BeGreaterThan(0,
            "includeAnalyzers=true must produce a non-zero analyzer count");

        var topDiagsByCount = diagnostics["topByCount"] as JArray;
        topDiagsByCount.Should().NotBeNull("topByCount must always be emitted as an array");
        foreach (var entry in topDiagsByCount!)
        {
            entry["id"]!.Value<string>().Should().NotBeNullOrEmpty(
                "each topByCount entry must carry the diagnostic id (Quality.cs:530)");
            entry["count"]!.Value<int>().Should().BeGreaterThan(0,
                "a grouped diagnostic must have at least one occurrence");
        }

        // SharpLensMcp has no analyzer-flagged god objects under default thresholds
        // (no class has 20+ efferent coupling AND 20+ members).
        coupling["godObjectCandidates"]!.Value<int>().Should().Be(0,
            "no SharpLensMcp type meets the god-object thresholds");
        var couplingHotspots = coupling["hotspots"] as JArray;
        couplingHotspots.Should().NotBeNull("coupling.hotspots must always be an array");
        foreach (var h in couplingHotspots!)
        {
            // Per-hotspot shape (Quality.cs:591-598).
            h["typeName"]!.Value<string>().Should().NotBeNullOrEmpty();
            h["efferentCoupling"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
            h["afferentCoupling"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
            h["memberCount"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
            h["score"].Should().NotBeNull();
            h["location"].Should().NotBeNull();
        }

        // coverage.uncoveredPublicSurface is the int from FindUntestedCode total
        // (Quality.cs:602). hotspots is always emitted as an array.
        coverage["uncoveredPublicSurface"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        var coverageHotspots = coverage["hotspots"] as JArray;
        coverageHotspots.Should().NotBeNull("coverage.hotspots must always be an array");
        foreach (var h in coverageHotspots!)
        {
            // Per-hotspot shape (Quality.cs:604-610).
            h["fullName"]!.Value<string>().Should().NotBeNullOrEmpty();
            h["kind"]!.Value<string>().Should().NotBeNullOrEmpty();
            h["accessibility"].Should().NotBeNull();
            h["location"].Should().NotBeNull();
        }

        // unusedCode.count + topByKind are always emitted (Quality.cs:582-586).
        unusedCode["count"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        var topByKind = unusedCode["topByKind"] as JArray;
        topByKind.Should().NotBeNull("unusedCode.topByKind must always be emitted as an array");
        foreach (var entry in topByKind!)
        {
            entry["kind"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["count"]!.Value<int>().Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task GetProjectHealth_TopNRespected()
    {
        var result = await Service.GetProjectHealthAsync("SharpLensMcp", includeAnalyzers: true, topN: 2);
        AssertSuccess(result);

        var data = GetData(result);

        // topN caps three different arrays inside the response. The impl always emits
        // these arrays (Quality.cs:590, 603, 580) — never null — so the prior
        // `if (array != null)` guards were dead code that would have skipped the cap
        // verification on a regression that dropped a field.
        var topDiags = data["diagnostics"]!["topByCount"] as JArray;
        topDiags.Should().NotBeNull();
        topDiags!.Count.Should().BeLessOrEqualTo(2);

        var godHotspots = data["coupling"]!["hotspots"] as JArray;
        godHotspots.Should().NotBeNull();
        godHotspots!.Count.Should().BeLessOrEqualTo(2,
            "topN must cap the coupling hotspots list (Quality.cs:550 passes maxResults: topN)");

        var coverageHotspots = data["coverage"]!["hotspots"] as JArray;
        coverageHotspots.Should().NotBeNull();
        coverageHotspots!.Count.Should().BeLessOrEqualTo(2,
            "topN must cap the coverage hotspots list (Quality.cs:559 passes maxResults: topN)");
    }

    [Fact]
    public async Task GetProjectHealth_IncludeAnalyzersFalse_ZeroAnalyzersAndSummaryLocksAllFiveCounts()
    {
        var result = await Service.GetProjectHealthAsync("SharpLensMcp", includeAnalyzers: false, topN: 5);
        AssertSuccess(result);

        var data = GetData(result);
        // includeAnalyzers=false is forwarded as runAnalyzers=false to
        // GetDiagnosticsDataAsync (Quality.cs:524) — analyzerCount must be 0.
        data["diagnostics"]!["analyzerCount"]!.Value<int>().Should().Be(0,
            "includeAnalyzers=false must skip analyzer execution");

        // Per Quality.cs:569 the summary format is exactly:
        //   "{n} errors, {n} warnings, {n} god-object candidates, {n} uncovered public methods, {n} unused symbols"
        // SharpLensMcp is clean → "0 errors, 0 warnings, 0 god-object candidates".
        // Locks both the format AND the known-zero counts.
        var summary = data["summary"]!.Value<string>()!;
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
    public async Task GetProjectHealth_MatchesProjectNameCaseInsensitively()
    {
        // Quality.cs:509 uses StringComparison.OrdinalIgnoreCase. Lock that branch —
        // a regression to case-sensitive would silently break callers that pass the
        // project name in different casing.
        var result = await Service.GetProjectHealthAsync("sharplensmcp", includeAnalyzers: false, topN: 5);
        AssertSuccess(result);
        var data = GetData(result);
        data["projectName"]!.Value<string>().Should().Be("SharpLensMcp",
            "the response must echo the canonical project name even when the request used lowercase");
    }

    [Fact]
    public async Task GetProjectHealth_OnNonExistentProject_ReturnsInvalidParameterWithHint()
    {
        var result = await Service.GetProjectHealthAsync("DoesNotExist_12345");
        AssertError(result, ErrorCodes.InvalidParameter);
        // Lock the error contract (Quality.cs:512-516). RoslynError uses PascalCase
        // (Hint/Message); the prior test used lowercase ["hint"]/["message"] and the
        // `?.` chain silently skipped both assertions.
        var json = JObject.FromObject(result);
        json["error"]!["Hint"]!.Value<string>().Should().Contain("get_project_structure",
            "the hint must point at get_project_structure for project name discovery");
        json["error"]!["Message"]!.Value<string>().Should().Contain("DoesNotExist_12345",
            "the error message must echo the bad project name for caller correlation");
    }
}
