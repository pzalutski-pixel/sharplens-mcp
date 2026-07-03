using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Fixtures;
using Xunit;

namespace SharpLensMcp.Tests;

// remove_unused_code is COMPUTE-ONLY: the tool reports removals and rewritten
// contents, the agent applies them. Test 1 locks the computed content and
// that disk stays untouched; test 2 plays the agent (writes the content,
// reloads, re-runs) to lock idempotence and that the result compiles. The
// snapshot/restore pattern guards the fixture because test 2 writes to it.
public class UnusedRemovalToolTests : IAsyncLifetime
{
    private RoslynService _service = null!;
    private string _solutionPath = null!;
    private string _fixturePath = null!;
    private string _originalFixtureContent = null!;

    private static readonly List<string> DeadNames =
        new() { "DeadHelperOne", "DeadHelperTwo", "UnusedRemovalGhost" };

    public async Task InitializeAsync()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "SharpLensMcp.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null) throw new InvalidOperationException("SharpLensMcp.sln not found");

        _solutionPath = Path.Combine(dir, "SharpLensMcp.sln");
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "UnusedRemovalFixture.cs");
        _originalFixtureContent = File.ReadAllText(_fixturePath);

        _service = new RoslynService();
        var result = await _service.LoadSolutionAsync(_solutionPath);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        // Always restore the fixture to its committed content — recreating it even
        // if a test deleted it — and assert the restore physically landed, so a
        // swallowed write can't leave the tree dirty for the next serialized test.
        if (_originalFixtureContent != null && _fixturePath != null)
        {
            File.WriteAllText(_fixturePath, _originalFixtureContent);
            File.ReadAllText(_fixturePath).Should().Be(_originalFixtureContent,
                "the fixture must be restored to its committed content after the test");
        }
        return Task.CompletedTask;
    }

    private async Task<JToken> RunOnDeadNames(RoslynService service)
    {
        var result = await service.RemoveUnusedCodeAsync(
            projectName: "SharpLensMcp.Tests", symbolNames: DeadNames);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }

    [Fact]
    public async Task ComputesRemovalsAndNewlyUnusedUsing_WithoutTouchingDisk()
    {
        // Anchor: the host class and LiveMethod are REFERENCED right here, so
        // only the dead helpers and the ghost class are removable.
        new UnusedRemovalHost().LiveMethod().Should().Be("live");

        var data = await RunOnDeadNames(_service);

        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse();
        var removedNames = (data["removed"] as JArray)!.Select(r => r["name"]!.Value<string>()).ToList();
        removedNames.Should().BeEquivalentTo(DeadNames);

        var files = (data["files"] as JArray)!;
        files.Count.Should().Be(1, "all three symbols live in the one fixture file");
        files[0]["filePath"]!.Value<string>().Should().EndWith("UnusedRemovalFixture.cs");
        files[0]["removedDeclarations"]!.Value<int>().Should().Be(3);
        files[0]["removedUsings"]!.Value<int>().Should().Be(1,
            "DeadHelperTwo was the only user of System.Text");

        var content = files[0]["updatedContent"]!.Value<string>()!;
        content.Should().NotContain("DeadHelperOne");
        content.Should().NotContain("DeadHelperTwo");
        content.Should().NotContain("UnusedRemovalGhost");
        content.Should().NotContain("using System.Text;",
            "the using became unnecessary the moment DeadHelperTwo went away");
        content.Should().Contain("LiveMethod");

        File.ReadAllText(_fixturePath).Should().Be(_originalFixtureContent,
            "a generation-only tool must never modify the file");
    }

    [Fact]
    public async Task AgentAppliesContent_ResultCompiles_AndSecondRunReportsNothing()
    {
        var data = await RunOnDeadNames(_service);
        var updatedContent = (data["files"] as JArray)![0]["updatedContent"]!.Value<string>()!;

        // The agent's role: write the computed content, then reload.
        File.WriteAllText(_fixturePath, updatedContent);
        var freshService = new RoslynService();
        var loadJson = JObject.FromObject(await freshService.LoadSolutionAsync(_solutionPath));
        loadJson["success"]!.Value<bool>().Should().BeTrue();

        var diagnostics = JObject.FromObject(await freshService.GetDiagnosticsAsync(
            _fixturePath, projectPath: null, severity: null, includeHidden: false, runAnalyzers: false));
        diagnostics["data"]!["errorCount"]!.Value<int>().Should().Be(0,
            "the computed removal must leave a compiling file");

        var secondRun = await RunOnDeadNames(freshService);
        (secondRun["removed"] as JArray)!.Should().BeEmpty("everything was already removed");
        (secondRun["files"] as JArray)!.Should().BeEmpty();
        var skippedNames = (secondRun["skipped"] as JArray)!.Select(s => s["name"]!.Value<string>()).ToList();
        skippedNames.Should().BeEquivalentTo(DeadNames,
            "every requested name must be accounted for with a skip reason");
    }

    [Fact]
    public async Task UnknownSymbolName_ReportedSkippedWithReason()
    {
        var result = await _service.RemoveUnusedCodeAsync(
            projectName: "SharpLensMcp.Tests",
            symbolNames: new List<string> { "TotallyMissingSymbolXyz" });

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue();
        var data = json["data"]!;
        (data["removed"] as JArray)!.Should().BeEmpty();
        (data["files"] as JArray)!.Should().BeEmpty();
        var skipped = (data["skipped"] as JArray)!;
        skipped.Count.Should().Be(1);
        skipped[0]["name"]!.Value<string>().Should().Be("TotallyMissingSymbolXyz");
        skipped[0]["reason"]!.Value<string>().Should().Contain("Not reported unused");
    }

    [Fact]
    public async Task MaxSymbolsOutOfRange_ReturnsInvalidParameter()
    {
        var result = await _service.RemoveUnusedCodeAsync(maxSymbols: 0);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Contain("between 1 and 500");
    }
}
