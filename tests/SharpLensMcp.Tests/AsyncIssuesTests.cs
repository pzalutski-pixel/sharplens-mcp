using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_async_issues against AsyncIssuesFixture: one violation per category, the
// event handler exempt, and the count surfaced in get_project_health.
public class AsyncIssuesTests : RoslynServiceTestBase
{
    private static List<(string category, string symbol)> Issues(JToken data) =>
        (data["issues"] as JArray)!
            .Select(i => (i["category"]!.Value<string>()!, i["symbol"]!.Value<string>()!))
            .ToList();

    [Fact]
    public async Task FlagsOneOfEachCategory_AndExemptsTheEventHandler()
    {
        var data = GetData(await Service.FindAsyncIssuesAsync(projectName: "SharpLensMcp.Tests"));

        var issues = Issues(data).Where(i => i.symbol.Contains("AsyncIssuesTarget")).ToList();

        issues.Where(i => i.category == "asyncVoid").Should().ContainSingle()
            .Which.symbol.Should().Contain("FireAndForget");
        issues.Should().NotContain(i => i.symbol.Contains("OnClick"),
            "an (object, EventArgs) async void is a legitimate event handler");

        issues.Where(i => i.category == "blockingCall").Should().Contain(
            i => i.symbol.Contains("BlockOnResult"));

        issues.Where(i => i.category == "tokenNotForwarded").Should().Contain(
            i => i.symbol.Contains("ForwardsNothing"));
        issues.Should().NotContain(i => i.symbol.Contains("ProcessText"),
            "Process(string) has no cancellable variant — an unrelated Process(CancellationToken) " +
            "overload must NOT trigger tokenNotForwarded");
    }

    [Fact]
    public async Task BlockingCall_CarriesDetailAndLocation()
    {
        var data = GetData(await Service.FindAsyncIssuesAsync(projectName: "SharpLensMcp.Tests"));

        var blocking = (data["issues"] as JArray)!
            .First(i => i["symbol"]!.Value<string>()!.Contains("BlockOnResult"));
        blocking["detail"]!.Value<string>().Should().Contain(".Result");
        var fixtureRel = blocking["location"]!["filePath"]!.Value<string>()!;
        fixtureRel.Should().EndWith("AsyncIssuesFixture.cs");

        // Pin the reported line to the fixture's actual `.Result` line (0-based, the
        // tool's GetSymbolLocation convention) resolved from a marker, so the lock is
        // exact yet immune to drift when the fixture is edited.
        var fixtureAbs = Path.Combine(Path.GetDirectoryName(SolutionPath)!, fixtureRel);
        var fixtureLines = File.ReadAllLines(fixtureAbs);
        var resultLine = Array.FindIndex(fixtureLines, l => l.Contains("return ComputeAsync().Result;"));
        resultLine.Should().BeGreaterThan(-1, "the fixture must contain the blocking .Result call");
        blocking["location"]!["line"]!.Value<int>().Should().Be(resultLine,
            "the blocking call's reported line must equal the .Result expression's 0-based line");
    }

    [Fact]
    public async Task CountByCategory_CoversTheThreeCategories()
    {
        var data = GetData(await Service.FindAsyncIssuesAsync(projectName: "SharpLensMcp.Tests"));

        var byCategory = (data["countByCategory"] as JObject)!;
        byCategory.Should().ContainKey("asyncVoid");
        byCategory.Should().ContainKey("blockingCall");
        byCategory.Should().ContainKey("tokenNotForwarded");
    }

    [Fact]
    public async Task ProjectHealth_SurfacesAsyncIssueCount()
    {
        var data = GetData(await Service.GetProjectHealthAsync("SharpLensMcp.Tests", includeAnalyzers: false));

        // Lock the count to find_async_issues's own total over the same project — an
        // exact consistency check rather than a `> 0` tautology. The fixture
        // guarantees the total is non-zero, which the equality then pins precisely.
        var direct = GetData(await Service.FindAsyncIssuesAsync(projectName: "SharpLensMcp.Tests"));
        var expectedCount = (direct["issues"] as JArray)!.Count;
        expectedCount.Should().BeGreaterThan(0, "the async fixture contributes issues");
        data["asyncHygiene"]!["count"]!.Value<int>().Should().Be(expectedCount,
            "project_health must surface the same async-issue count find_async_issues reports");
        data["summary"]!.Value<string>().Should().Contain("async issues",
            "the async bucket must appear in the project_health summary");
    }

    [Fact]
    public async Task MaxResultsOutOfRange_ReturnsInvalidParameter()
    {
        var result = await Service.FindAsyncIssuesAsync(maxResults: 0);

        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task UnknownProject_ReturnsInvalidParameter()
    {
        // The quality family shares one unknown-projectName contract.
        var result = await Service.FindAsyncIssuesAsync(projectName: "NoSuchProject");

        AssertError(result, ErrorCodes.InvalidParameter);
        JObject.FromObject(result)["error"]!["Message"]!.Value<string>()
            .Should().Contain("Project not found");
    }
}
