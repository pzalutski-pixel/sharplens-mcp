using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks two get_diagnostics contracts: error/warning counts reflect the FULL
// diagnostic set even when the returned list is capped by ROSLYN_MAX_DIAGNOSTICS,
// and an unknown severity filter is rejected instead of throwing.
public class GetDiagnosticsCountsTests
{
    // Seven distinct CS0103 errors — comfortably above the cap of 5 used below.
    private const string SevenErrorsCode = @"
namespace Production;

public class Broken
{
    public void Run()
    {
        UndefinedOne();
        UndefinedTwo();
        UndefinedThree();
        UndefinedFour();
        UndefinedFive();
        UndefinedSix();
        UndefinedSeven();
    }
}";

    // One warning (CS0219, the unused 'spare') ahead of one error (CS0103) in source
    // order, so a naive Take would surface the warning first.
    private const string WarningThenErrorCode = @"
namespace Production;

public class Mixed
{
    public void Run()
    {
        int spare = 5;
        UndefinedThing();
    }
}";

    private static RoslynService CreateService(out string filePath)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(SevenErrorsCode, "Broken.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        filePath = document.FilePath!;
        return service;
    }

    [Fact]
    public async Task CappedList_OrdersErrorsAheadOfWarnings()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(WarningThenErrorCode, "Mixed.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        service.SetMaxDiagnosticsForTesting(1);

        var result = await service.GetDiagnosticsAsync(
            document.FilePath, projectPath: null, severity: null, includeHidden: false, runAnalyzers: false);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var diagnostics = (json["data"]!["diagnostics"] as JArray)!;
        diagnostics.Count.Should().Be(1, "the cap is 1");
        diagnostics[0]["severity"]!.Value<string>().Should().Be("Error",
            "errors sort ahead of warnings when the page is capped, even though the warning is earlier in source");
        json["data"]!["errorCount"]!.Value<int>().Should().Be(1);
        json["data"]!["warningCount"]!.Value<int>().Should().BeGreaterOrEqualTo(1,
            "the unused 'spare' warning is still counted in the full set");
    }

    [Fact]
    public async Task ErrorCount_ReflectsFullSet_WhenDiagnosticsListIsCapped()
    {
        var service = CreateService(out var filePath);
        service.SetMaxDiagnosticsForTesting(5);

        var result = await service.GetDiagnosticsAsync(filePath, projectPath: null, severity: null, includeHidden: false, runAnalyzers: false);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var diagnostics = json["data"]!["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull();
        diagnostics!.Count.Should().Be(5, "the returned list is capped at maxDiagnostics");

        json["data"]!["errorCount"].Should().NotBeNull();
        json["data"]!["errorCount"]!.Value<int>().Should().Be(7,
            "errorCount must reflect ALL errors, not just the capped page — agents gate commits on this number");

        // Direct-service tests see ResponseMetadata's PascalCase property names;
        // the camelCase form only appears on the wire through the dispatcher.
        json["meta"]!["TotalCount"].Should().NotBeNull();
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(7,
            "totalCount must report the uncapped diagnostic count");
        json["meta"]!["Truncated"]!.Value<bool>().Should().BeTrue(
            "the response must signal that the list was capped");
    }

    [Fact]
    public async Task SeverityFilter_NarrowsPage_ButCountsReflectFullSet()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(WarningThenErrorCode, "Mixed.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GetDiagnosticsAsync(
            document.FilePath, projectPath: null, severity: "Warning", includeHidden: false, runAnalyzers: false);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var diagnostics = json["data"]!["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull();
        diagnostics!.Count.Should().Be(1, "only the single warning matches the severity filter");
        diagnostics[0]["severity"]!.Value<string>().Should().Be("Warning");

        json["data"]!["errorCount"].Should().NotBeNull();
        json["data"]!["errorCount"]!.Value<int>().Should().Be(1,
            "the CS0103 error must still be counted under a severity:Warning filter — the filter narrows the page, not the health summary");
        json["data"]!["warningCount"]!.Value<int>().Should().Be(1);

        // totalCount reflects the FILTERED set (what was paginated), not the full set.
        json["meta"]!["TotalCount"].Should().NotBeNull();
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(1,
            "totalCount counts the diagnostics matching the severity filter");
    }

    [Fact]
    public async Task UnknownSeverity_ReturnsInvalidParameter()
    {
        var service = CreateService(out var filePath);

        var result = await service.GetDiagnosticsAsync(filePath, projectPath: null, severity: "bogus", includeHidden: false, runAnalyzers: false);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeFalse("an unknown severity filter must be rejected, not crash or be ignored");
        json["error"]!["Code"].Should().NotBeNull();
        json["error"]!["Code"]!.Value<string>().Should().Be("INVALID_PARAMETER");
        json["error"]!["Message"]!.Value<string>().Should().Contain("Error",
            "the message must list the valid severity names");
    }
}
