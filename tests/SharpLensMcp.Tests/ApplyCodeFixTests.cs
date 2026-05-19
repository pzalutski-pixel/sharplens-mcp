using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;
// End-to-end snapshot/restore test for apply_code_fix against a real
// compiler-emitted CS0219 (unused local variable). The fixture's committed
// state is clean (the variable is used); InitializeAsync writes a broken
// state to disk BEFORE LoadSolutionAsync so the in-memory Roslyn solution
// sees the diagnostic. DisposeAsync restores the committed content so the
// repo build stays warning-free.
public class ApplyCodeFixTests : IAsyncLifetime
{
    private RoslynService _service = null !;
    private string _solutionPath = null !;
    private string _fixturePath = null !;
    private string _originalFixtureContent = null !;
    private const string BrokenContent = "namespace SharpLensMcp.Tests.Fixtures;\n" + "\n" + "public class UnusedVariableTarget\n" + "{\n" + "    public int Compute()\n" + "    {\n" + "        int x = 5;\n" + "        return 0;\n" + "    }\n" + "}\n";
    public async Task InitializeAsync()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !File.Exists(Path.Combine(dir, "SharpLensMcp.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null)
            throw new InvalidOperationException("SharpLensMcp.sln not found");
        _solutionPath = Path.Combine(dir, "SharpLensMcp.sln");
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "UnusedVariableFixture.cs");
        _originalFixtureContent = File.ReadAllText(_fixturePath);
        File.WriteAllText(_fixturePath, BrokenContent);
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
    public async Task Apply_OnCS0219_RemovesUnusedVariableAndClearsDiagnostic()
    {
        var beforeDiag = await _service.GetDiagnosticsAsync(filePath: _fixturePath, projectPath: null, severity: null, includeHidden: false, runAnalyzers: false);
        var beforeJson = JObject.FromObject(beforeDiag);
        beforeJson["success"]?.Value<bool>().Should().BeTrue();
        var beforeIds = beforeJson["data"]!["diagnostics"]!.Select(d => d["id"]!.Value<string>()).ToList();
        beforeIds.Should().Contain("CS0219", "the pre-mutated fixture must emit CS0219 before apply");
        var preview = await _service.ApplyCodeFixAsync(_fixturePath, "CS0219", line: 6, column: 12, fixIndex: 0, preview: true);
        var previewJson = JObject.FromObject(preview);
        previewJson["success"]?.Value<bool>().Should().BeTrue(previewJson.ToString());
        previewJson["data"]!["preview"]?.Value<bool>().Should().BeTrue();
        previewJson["data"]!["applied"]?.Value<bool>().Should().BeFalse();
        previewJson["data"]!["diagnosticId"]?.Value<string>().Should().Be("CS0219");
        var changedFiles = previewJson["data"]!["changedFiles"] as JArray;
        changedFiles.Should().NotBeNull();
        changedFiles!.Count.Should().BeGreaterThan(0);
        var firstChange = changedFiles[0];
        firstChange["filePath"]!.Value<string>()!.Should().Contain("UnusedVariableFixture.cs");
        firstChange["changeType"]!.Value<string>().Should().Be("Modified");
        firstChange["newText"]!.Value<string>()!.Should().NotContain("int x = 5", "the preview text must show the unused variable removed");
        var diskAfterPreview = File.ReadAllText(_fixturePath);
        diskAfterPreview.Should().Contain("int x = 5", "preview must NOT apply the fix to disk");
        var applied = await _service.ApplyCodeFixAsync(_fixturePath, "CS0219", line: 6, column: 12, fixIndex: 0, preview: false);
        var appliedJson = JObject.FromObject(applied);
        appliedJson["success"]?.Value<bool>().Should().BeTrue(appliedJson.ToString());
        appliedJson["data"]!["applied"]?.Value<bool>().Should().BeTrue();
        appliedJson["data"]!["preview"]?.Value<bool>().Should().BeFalse();
        var diskAfterApply = File.ReadAllText(_fixturePath);
        diskAfterApply.Should().NotContain("int x = 5", "apply must rewrite the file without the unused variable");
        diskAfterApply.Should().Contain("return 0", "the rest of the method must still be present");
        var afterDiag = await _service.GetDiagnosticsAsync(filePath: _fixturePath, projectPath: null, severity: null, includeHidden: false, runAnalyzers: false);
        var afterJson = JObject.FromObject(afterDiag);
        afterJson["success"]?.Value<bool>().Should().BeTrue();
        var afterIds = afterJson["data"]!["diagnostics"]!.Select(d => d["id"]!.Value<string>()).ToList();
        afterIds.Should().NotContain("CS0219", "apply must remove the CS0219 diagnostic");
    }
}