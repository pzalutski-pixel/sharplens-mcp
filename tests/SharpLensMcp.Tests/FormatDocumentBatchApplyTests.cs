using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;
// End-to-end snapshot/restore test for format_document_batch preview=false.
public class FormatDocumentBatchApplyTests : IAsyncLifetime
{
    private RoslynService _service = null !;
    private string _solutionPath = null !;
    private string _fixturePath = null !;
    private string _originalFixtureContent = null !;
    public async Task InitializeAsync()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "SharpLensMcp.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null)
            throw new InvalidOperationException("SharpLensMcp.sln not found");
        _solutionPath = Path.Combine(dir, "SharpLensMcp.sln");
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "BadlyFormattedFixture.cs");
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
    public async Task Preview_WithFilePattern_RestrictsScanToMatchingFile()
    {
        // Positive lock on the filePattern filter: a glob that uniquely matches
        // BadlyFormattedFixture.cs must scan exactly one file, not the whole
        // SharpLensMcp.Tests project. Without this assertion the apply test below
        // would still pass even if filePattern were silently ignored, because the
        // badly-formatted fixture always reformats.
        var result = await _service.FormatDocumentBatchAsync(
            projectName: "SharpLensMcp.Tests",
            includeTests: true,
            preview: true,
            filePattern: "BadlyFormattedFixture.cs");
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["totalFilesScanned"].Should().NotBeNull();
        json["data"]!["totalFilesScanned"]!.Value<int>().Should().Be(1,
            "filePattern must restrict the scan to the single matching file (project has many .cs files)");
        json["data"]!["filesFormatted"].Should().NotBeNull();
        json["data"]!["filesFormatted"]!.Value<int>().Should().Be(1,
            "the single matched file is badly formatted and must report as formatted");
    }

    [Fact]
    public async Task Apply_CanonicalMode_OnMinifiedFixture_ExpandsToMultipleLines()
    {
        // The fixture is all on one line. mode:"canonical" (NormalizeWhitespace)
        // is the engine that un-minifies — it must break the file into multiple
        // lines on apply.
        var beforeLines = _originalFixtureContent.Count(c => c == '\n');
        var result = await _service.FormatDocumentBatchAsync(projectName: "SharpLensMcp.Tests", includeTests: true, preview: false, filePattern: "BadlyFormattedFixture.cs", mode: "canonical");
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull();
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["preview"].Should().NotBeNull();
        json["data"]!["preview"]!.Value<bool>().Should().BeFalse();
        json["data"]!["mode"].Should().NotBeNull();
        json["data"]!["mode"]!.Value<string>().Should().Be("canonical");
        json["data"]!["filesFormatted"].Should().NotBeNull();
        json["data"]!["filesFormatted"]!.Value<int>().Should().BeGreaterOrEqualTo(1, "the minified fixture must be formatted");
        var diskContent = File.ReadAllText(_fixturePath);
        var afterLines = diskContent.Count(c => c == '\n');
        afterLines.Should().BeGreaterThan(beforeLines, "canonical mode must add newlines to the all-on-one-line fixture");
        // The class signature and a brace must appear on separate lines after formatting.
        diskContent.Should().Contain("class BadlyFormattedTarget");
        diskContent.Should().MatchRegex(@"\{\s*[\r\n]", "after canonical formatting there must be at least one opening brace at end of line (newline may be \\r\\n on Windows)");
    }

    [Fact]
    public async Task Apply_DefaultMode_OnMinifiedFixture_FixesSpacingWithoutChangingLineStructure()
    {
        // Contract of the default (whitespace) mode, matching `dotnet format`:
        // token spacing is corrected but the author's line structure is preserved
        // — a minified file is NOT expanded. Un-minifying requires mode:"canonical".
        var beforeLines = _originalFixtureContent.Count(c => c == '\n');
        var result = await _service.FormatDocumentBatchAsync(projectName: "SharpLensMcp.Tests", includeTests: true, preview: false, filePattern: "BadlyFormattedFixture.cs");
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["mode"].Should().NotBeNull();
        json["data"]!["mode"]!.Value<string>().Should().Be("whitespace", "whitespace is the default mode");
        json["data"]!["filesFormatted"].Should().NotBeNull();
        json["data"]!["filesFormatted"]!.Value<int>().Should().Be(1, "the minified fixture has spacing the default mode corrects");
        var diskContent = File.ReadAllText(_fixturePath);
        diskContent.Count(c => c == '\n').Should().Be(beforeLines, "the default mode must not change line structure");
    }

    [Fact]
    public async Task FormatDocumentBatch_WithUnknownMode_ReturnsInvalidParameter()
    {
        var result = await _service.FormatDocumentBatchAsync(projectName: "SharpLensMcp.Tests", includeTests: true, preview: true, filePattern: "BadlyFormattedFixture.cs", mode: "aggressive");
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeFalse("unknown mode values must be rejected, not silently defaulted");
        json["error"].Should().NotBeNull();
        json["error"]!["Code"].Should().NotBeNull();
        json["error"]!["Code"]!.Value<string>().Should().Be("INVALID_PARAMETER");
    }

    [Fact]
    public async Task Preview_OnAlreadyFormattedFile_WithSuppressionAndBlankLines_ReportsNoChanges()
    {
        // Regression lock for the formatting engine itself: a correctly-formatted
        // file containing the null-suppression operator ("null!"), intentional
        // blank lines between members, and a file-scoped namespace must come back
        // unchanged. The previous engine (NormalizeWhitespace) rewrote all of
        // these ("null !", stripped blank lines) — that bug once mangled 64 files
        // of this very test suite.
        var result = await _service.FormatDocumentBatchAsync(
            projectName: "SharpLensMcp.Tests",
            includeTests: true,
            preview: true,
            filePattern: "CleanFormattedFixture.cs");
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["totalFilesScanned"].Should().NotBeNull();
        json["data"]!["totalFilesScanned"]!.Value<int>().Should().Be(1,
            "filePattern must match exactly the clean fixture");
        json["data"]!["filesFormatted"].Should().NotBeNull();
        json["data"]!["filesFormatted"]!.Value<int>().Should().Be(0,
            "an already-formatted file with null! and intentional blank lines must not be reformatted");
    }
}