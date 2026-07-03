using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// rename_symbol apply path (preview:false): snapshot the fixture, rename a
// file-local private method, assert applied=true and that the new name actually
// landed on disk, then restore. The fixture's symbol is referenced only within
// its own file, so the apply touches exactly that file.
public class RenameApplyTests : IAsyncLifetime
{
    private RoslynService _service = null!;
    private string _solutionPath = null!;
    private string _fixturePath = null!;
    private string _originalContent = null!;

    public async Task InitializeAsync()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "SharpLensMcp.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null) throw new InvalidOperationException("SharpLensMcp.sln not found");

        _solutionPath = Path.Combine(dir, "SharpLensMcp.sln");
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "RenameApplyFixture.cs");
        _originalContent = File.ReadAllText(_fixturePath);

        _service = new RoslynService();
        var load = JObject.FromObject(await _service.LoadSolutionAsync(_solutionPath));
        load["success"]!.Value<bool>().Should().BeTrue(load.ToString());
    }

    public Task DisposeAsync()
    {
        if (_originalContent != null && _fixturePath != null)
        {
            File.WriteAllText(_fixturePath, _originalContent);
            File.ReadAllText(_fixturePath).Should().Be(_originalContent,
                "the fixture must be restored to its committed content after the test");
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Apply_RenamesFileLocalMethod_LandsOnDisk()
    {
        var lines = File.ReadAllLines(_fixturePath);
        var declLine = Array.FindIndex(lines, l => l.Contains("private int RenameApplyOriginal()"));
        declLine.Should().BeGreaterThan(-1);
        var column = lines[declLine].IndexOf("RenameApplyOriginal", StringComparison.Ordinal);

        var result = await _service.RenameSymbolAsync(
            _fixturePath, declLine, column, newName: "RenameApplyRenamed", preview: false);

        var data = JObject.FromObject(result)["data"]!;
        data["applied"]!.Value<bool>().Should().BeTrue("preview:false must apply");
        data["newName"]!.Value<string>().Should().Be("RenameApplyRenamed");

        var onDisk = File.ReadAllText(_fixturePath);
        onDisk.Should().Contain("RenameApplyRenamed", "the new name must be written to disk");
        onDisk.Should().NotContain("RenameApplyOriginal", "no occurrence of the old name may remain");
        // Both call sites (Run calls it twice) plus the declaration → 3 occurrences.
        System.Text.RegularExpressions.Regex.Matches(onDisk, "RenameApplyRenamed").Count
            .Should().Be(3, "the declaration and both call sites must all be renamed");
    }
}
