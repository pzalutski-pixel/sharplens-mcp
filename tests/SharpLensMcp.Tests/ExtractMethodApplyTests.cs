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
        var data = json["data"]!;
        data["applied"]?.Value<bool>().Should().BeTrue();
        data["methodName"]?.Value<string>().Should().Be("ComputeIntermediate");
        data["statementsExtracted"]?.Value<int>().Should().Be(2);

        // Response shape lock: every documented field must be present and correct.
        // `total` flows out (used by `return total`), so the extracted method returns int
        // via `total`. Parameters are a, b, c (read inside the selection).
        data["returnType"]?.Value<string>().Should().Be("int",
            "the extracted method returns int because `total` flows out and is int");
        data["returnVariable"]?.Value<string>().Should().Be("total");
        data["signature"]?.Value<string>().Should()
            .Contain("private int ComputeIntermediate",
                "default accessibility 'private' + int return + chosen name");
        var filesModified = data["filesModified"] as JArray;
        filesModified.Should().NotBeNullOrEmpty();
        filesModified!.Any(f => f.Value<string>()?.EndsWith("ExtractMethodApplyFixture.cs") == true)
            .Should().BeTrue("the fixture file must appear in filesModified");

        // The fixture file on disk must now contain the new method declaration AND
        // a call to it from the source method's body.
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

        // The source method ExtractTarget must invoke the new method AND keep `return total;`.
        var extractTargetBody = ExtractMethodBody(newContent, "ExtractTarget");
        extractTargetBody.Should().Contain("var total = ComputeIntermediate(a, b, c);",
            "the call-site replacement must capture the returned `total` and forward the parameters");
        extractTargetBody.Should().Contain("return total;",
            "the return statement that remained outside the extraction must still be present");
        extractTargetBody.Should().NotContain("var partial = a + b;",
            "the extracted statement must no longer appear in ExtractTarget");

        // After applying with a default-value-free signature, the file must still compile.
        var diag = await _service.GetDiagnosticsAsync(
            filePath: _fixturePath,
            projectPath: null,
            severity: "Error",
            includeHidden: false);
        var diags = JObject.FromObject(diag)["data"]!["diagnostics"] as JArray;
        diags.Should().NotBeNull();
        diags!.Count.Should().Be(0,
            "extracted method + replacement must compile without errors");
    }

    [Fact]
    public async Task Apply_WithCustomAccessibility_UsesThatAccessibilityOnDecl()
    {
        var lines = File.ReadAllLines(_fixturePath);
        var partialLine = Array.FindIndex(lines, l => l.Contains("var partial = a + b;"));
        var totalLine = Array.FindIndex(lines, l => l.Contains("var total = partial + c;"));

        var result = await _service.ExtractMethodAsync(
            _fixturePath,
            startLine: partialLine,
            endLine: totalLine,
            methodName: "ComputeIntermediate",
            accessibility: "public",
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["signature"]?.Value<string>().Should()
            .Contain("public int ComputeIntermediate",
                "the caller-supplied accessibility must appear in the signature");
        File.ReadAllText(_fixturePath).Should()
            .Contain("public int ComputeIntermediate",
                "the apply path must emit the chosen accessibility on disk");
    }

    [Fact]
    public async Task Apply_WithNoStatementsInRange_ReturnsAnalysisFailed()
    {
        // Pointing at line 0 (blank/namespace declaration) finds no statements.
        // RoslynService.Refactoring.cs:1003-1011 returns AnalysisFailed.
        var result = await _service.ExtractMethodAsync(
            _fixturePath,
            startLine: 0,
            endLine: 0,
            methodName: "Whatever",
            preview: true);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeFalse();
        json["error"]?["code"]?.Value<string>().Should().Be(ErrorCodes.AnalysisFailed);
        json["error"]?["message"]?.Value<string>().Should().Contain("No statements");
    }

    [Fact]
    public async Task Apply_OnFileNotInSolution_ReturnsFileNotInSolution()
    {
        // A path that's nowhere in the loaded solution — exercises the
        // FileNotInSolution branch (RoslynService.Refactoring.cs:966-971).
        var fakePath = Path.Combine(Path.GetTempPath(), $"not-in-solution-{Guid.NewGuid():N}.cs");
        var result = await _service.ExtractMethodAsync(
            fakePath, startLine: 0, endLine: 1,
            methodName: "Whatever", preview: true);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeFalse();
        json["error"]?["code"]?.Value<string>().Should().Be(ErrorCodes.FileNotInSolution);
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
        var data = json["data"]!;
        data["preview"]?.Value<bool>().Should().BeTrue();
        data["methodName"]?.Value<string>().Should().Be("ComputeIntermediate");
        data["returnType"]?.Value<string>().Should().Be("int");
        data["returnVariable"]?.Value<string>().Should().Be("total");
        data["statementsExtracted"]?.Value<int>().Should().Be(2);
        data["signature"]?.Value<string>().Should()
            .Contain("private int ComputeIntermediate(int a, int b, int c)");
        data["extractedCode"]?.Value<string>().Should().Contain("ComputeIntermediate");
        data["replacementCode"]?.Value<string>().Should()
            .Be("var total = ComputeIntermediate(a, b, c);",
                "the call-site replacement string must capture `total` and forward params");

        // Preview must NOT modify the file.
        File.ReadAllText(_fixturePath).Should().Be(beforeContent,
            "preview mode must not touch disk");
    }
}
