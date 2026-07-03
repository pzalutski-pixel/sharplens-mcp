using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// End-to-end snapshot/restore test for add_null_checks preview=false path.
// Pattern from ExtractMethodApplyTests / ChangeSignatureTests: per-test fixture
// snapshot in InitializeAsync, restore in DisposeAsync.
public class AddNullChecksApplyTests : IAsyncLifetime
{
    private RoslynService _service = null!;
    private string _solutionPath = null!;
    private string _fixturePath = null!;
    private string _originalFixtureContent = null!;

    public async Task InitializeAsync()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "SharpLensMcp.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null) throw new InvalidOperationException("SharpLensMcp.sln not found");

        _solutionPath = Path.Combine(dir, "SharpLensMcp.sln");
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "AddNullChecksFixture.cs");
        _originalFixtureContent = File.ReadAllText(_fixturePath);

        _service = new RoslynService();
        var result = await _service.LoadSolutionAsync(_solutionPath);
        var loadJson = JObject.FromObject(result);
        loadJson["success"].Should().NotBeNull();
        loadJson["success"]!.Value<bool>().Should().BeTrue();
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

    [Fact]
    public async Task Apply_OnTargetMethod_WritesArgumentNullGuardToDisk()
    {
        // Locate Process method by line in the fixture.
        var lines = File.ReadAllLines(_fixturePath);
        var methodLine = Array.FindIndex(lines, l => l.Contains("public string Process(string input)"));
        methodLine.Should().BeGreaterThan(-1);

        var result = await _service.AddNullChecksAsync(
            _fixturePath,
            line: methodLine,
            column: 18,
            preview: false);

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["applied"].Should().NotBeNull();
        data["applied"]!.Value<bool>().Should().BeTrue(
            "preview=false sets applied=true");
        data["methodName"].Should().NotBeNull();
        data["methodName"]!.Value<string>().Should().Be("Process");
        var paramsList = (data["parametersWithNullChecks"] as JArray)!;
        paramsList.Select(p => p.Value<string>()).Should().BeEquivalentTo(new[] { "input" });

        // Verify on disk: file contains ArgumentNullException.ThrowIfNull(input).
        var diskContent = File.ReadAllText(_fixturePath);
        diskContent.Should().Contain("ArgumentNullException.ThrowIfNull(input)",
            "the guard clause must be written to disk on apply");
        diskContent.Should().Contain("return input.ToUpperInvariant();",
            "the original body must be preserved after the inserted guard");
    }
}
