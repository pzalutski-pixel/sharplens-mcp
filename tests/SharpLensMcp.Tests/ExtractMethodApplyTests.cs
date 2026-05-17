using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// End-to-end test for the extract_method apply path implemented in
// RoslynService.Refactoring.cs (Phase 2.1 of the 1.5.3 release plan).
//
// Pattern mirrors ChangeSignatureTests: snapshot the fixture file in
// InitializeAsync, restore it in DisposeAsync. xUnit creates a fresh test
// instance per test so the disk state is isolated even on failure.
public class ExtractMethodApplyTests : IAsyncLifetime
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
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "ExtractMethodApplyFixture.cs");
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
    public async Task Apply_ExtractsSelectedStatements_AndInsertsCallSite()
    {
        // Resolve the line indices of `var partial` and `var total` in the source method.
        var lines = File.ReadAllLines(_fixturePath);
        var partialLine = Array.FindIndex(lines, l => l.Contains("var partial = a + b;"));
        var totalLine = Array.FindIndex(lines, l => l.Contains("var total = partial + c;"));
        partialLine.Should().BeGreaterThan(-1);
        totalLine.Should().Be(partialLine + 1);

        var result = await _service.ExtractMethodAsync(
            _fixturePath,
            startLine: partialLine,
            endLine: totalLine,
            methodName: "ComputeIntermediate",
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["applied"]?.Value<bool>().Should().BeTrue();
        json["data"]!["methodName"]?.Value<string>().Should().Be("ComputeIntermediate");
        json["data"]!["statementsExtracted"]?.Value<int>().Should().Be(2);

        // The fixture file on disk must now contain the new method declaration AND
        // a call to it from the source method's body. The extracted statements move
        // from ExtractTarget into ComputeIntermediate.
        var newContent = File.ReadAllText(_fixturePath);
        newContent.Should().Contain("ComputeIntermediate",
            "the extracted method must be inserted into the fixture file");
        newContent.Should().Contain("private",
            "default accessibility is private (see RoslynService.Refactoring.cs:953)");

        // var partial = a + b; should appear exactly once now (inside the new method).
        var partialOccurrences = System.Text.RegularExpressions.Regex
            .Matches(newContent, @"var partial = a \+ b;").Count;
        partialOccurrences.Should().Be(1,
            "the extracted statement must live only in the new method, not in the source");

        // The source method ExtractTarget must invoke the new method.
        var extractTargetBody = ExtractMethodBody(newContent, "ExtractTarget");
        extractTargetBody.Should().Contain("ComputeIntermediate(",
            "the call site to the extracted method must appear in ExtractTarget's body");
        extractTargetBody.Should().NotContain("var partial = a + b;",
            "the extracted statement must no longer appear in ExtractTarget");
    }

    // Helper: extract the body of the method whose declaration contains
    // `<methodName>(`. Looks for the opening brace after the declaration and
    // grabs to the matching close brace at depth 1.
    private static string ExtractMethodBody(string source, string methodName)
    {
        var declIdx = source.IndexOf(methodName + "(", StringComparison.Ordinal);
        declIdx.Should().BeGreaterOrEqualTo(0);
        var openIdx = source.IndexOf('{', declIdx);
        openIdx.Should().BeGreaterOrEqualTo(0);
        var depth = 0;
        for (var i = openIdx; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(openIdx, i - openIdx + 1);
            }
        }
        return source.Substring(openIdx);
    }

    [Fact]
    public async Task Preview_ReturnsExtractedCode_WithoutModifyingFile()
    {
        var beforeContent = File.ReadAllText(_fixturePath);
        var lines = File.ReadAllLines(_fixturePath);
        var partialLine = Array.FindIndex(lines, l => l.Contains("var partial = a + b;"));
        var totalLine = Array.FindIndex(lines, l => l.Contains("var total = partial + c;"));

        var result = await _service.ExtractMethodAsync(
            _fixturePath,
            startLine: partialLine,
            endLine: totalLine,
            methodName: "ComputeIntermediate",
            preview: true);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue();
        json["data"]!["preview"]?.Value<bool>().Should().BeTrue();
        json["data"]!["extractedCode"]?.Value<string>().Should().Contain("ComputeIntermediate");

        // Preview must NOT modify the file.
        File.ReadAllText(_fixturePath).Should().Be(beforeContent,
            "preview mode must not touch disk");
    }
}
