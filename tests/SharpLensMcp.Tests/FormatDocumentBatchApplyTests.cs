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
        JObject.FromObject(result)["success"]?.Value<bool>().Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        if (_originalFixtureContent != null && _fixturePath != null && File.Exists(_fixturePath))
        {
            File.WriteAllText(_fixturePath, _originalFixtureContent);
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
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["totalFilesScanned"]?.Value<int>().Should().Be(1,
            "filePattern must restrict the scan to the single matching file (project has many .cs files)");
        json["data"]!["filesFormatted"]?.Value<int>().Should().Be(1,
            "the single matched file is badly formatted and must report as formatted");
    }

    [Fact]
    public async Task Apply_OnBadlyFormattedFixture_NormalizesWhitespaceOnDisk()
    {
        // The fixture is all on one line. NormalizeWhitespace (Analysis.cs:1141)
        // must break it into multiple lines on apply.
        var beforeLines = _originalFixtureContent.Count(c => c == '\n');
        var result = await _service.FormatDocumentBatchAsync(projectName: "SharpLensMcp.Tests", includeTests: true, preview: false, filePattern: "BadlyFormattedFixture.cs");
        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["preview"]?.Value<bool>().Should().BeFalse();
        json["data"]!["filesFormatted"]?.Value<int>().Should().BeGreaterOrEqualTo(1, "the badly-formatted fixture must be formatted");
        var diskContent = File.ReadAllText(_fixturePath);
        var afterLines = diskContent.Count(c => c == '\n');
        afterLines.Should().BeGreaterThan(beforeLines, "NormalizeWhitespace must add newlines to the all-on-one-line fixture");
        // The class signature and a brace must appear on separate lines after formatting.
        diskContent.Should().Contain("class BadlyFormattedTarget");
        diskContent.Should().MatchRegex(@"\{\s*[\r\n]", "after NormalizeWhitespace there must be at least one opening brace at end of line (newline may be \\r\\n on Windows)");
    }
}