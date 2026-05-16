using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Exercises change_signature end-to-end: declaration + every call-site shape
// (plain invocation, named-argument invocation, `new Foo(...)`, `: this(...)`,
// `: base(...)`). xUnit creates a fresh instance per test, so InitializeAsync
// / DisposeAsync isolate disk state — each test starts with a pristine fixture
// and restores it on completion (even if the test failed).
public class ChangeSignatureTests : IAsyncLifetime
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
        _fixturePath = Path.Combine(dir, "tests", "SharpLensMcp.Tests", "Fixtures", "ChangeSignatureFixture.cs");
        _originalFixtureContent = File.ReadAllText(_fixturePath);

        _service = new RoslynService();
        var result = await _service.LoadSolutionAsync(_solutionPath);
        JObject.FromObject(result)["success"]?.Value<bool>().Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        // Always restore the fixture file so tests don't pollute each other or
        // leave the working tree dirty.
        if (_originalFixtureContent != null && _fixturePath != null && File.Exists(_fixturePath))
        {
            File.WriteAllText(_fixturePath, _originalFixtureContent);
        }
        return Task.CompletedTask;
    }

    private async Task<(int line, int column)> LocateConcatAsync()
    {
        var searchResult = await _service.SearchSymbolsAsync("Concat", kind: "Method", maxResults: 50);
        var symbols = (JObject.FromObject(searchResult)["data"]!["results"] as JArray)!;
        var match = symbols.First(s =>
            s["containingType"]?.Value<string>()?.EndsWith("ChangeSignatureMethodHolder") == true);
        var loc = match["location"]!;
        return (loc["line"]!.Value<int>(), loc["column"]!.Value<int>());
    }

    [Fact]
    public async Task Preview_OnMethod_ReturnsCallSitesWithoutApplying()
    {
        var (line, col) = await LocateConcatAsync();
        var before = File.ReadAllText(_fixturePath);

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, line, col,
            new List<SignatureChange>
            {
                new() { Action = "add", Name = "extra", Type = "int", DefaultValue = "0" }
            },
            preview: true);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue();
        json["data"]!["preview"]?.Value<bool>().Should().BeTrue();
        (json["data"]!["callSites"] as JArray)!.Count.Should().BeGreaterThan(0);

        File.ReadAllText(_fixturePath).Should().Be(before, "preview must not modify disk");
    }

    [Fact]
    public async Task Apply_AddParameter_AppendsToDeclarationAndAllCallSites()
    {
        var (line, col) = await LocateConcatAsync();

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, line, col,
            new List<SignatureChange>
            {
                new() { Action = "add", Name = "factor", Type = "int", DefaultValue = "1" }
            },
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["applied"]?.Value<bool>().Should().BeTrue();
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().BeGreaterThan(0);

        // New parameter must be on the declaration.
        json["data"]!["newSignature"]?.Value<string>().Should().Contain("factor");
    }

    [Fact]
    public async Task Apply_RemoveParameter_DropsFromDeclarationSignature()
    {
        var (line, col) = await LocateConcatAsync();

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, line, col,
            new List<SignatureChange>
            {
                new() { Action = "remove", Name = "third" }
            },
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["newSignature"]?.Value<string>().Should().NotContain("int third");
    }

    [Fact]
    public async Task Apply_RenameParameter_UpdatesDeclarationSignature()
    {
        var (line, col) = await LocateConcatAsync();

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, line, col,
            new List<SignatureChange>
            {
                new() { Action = "rename", Name = "first", NewName = "primary" }
            },
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["newSignature"]?.Value<string>().Should().Contain("primary");
    }

    [Fact]
    public async Task Apply_Reorder_PermutesDeclarationSignature()
    {
        var (line, col) = await LocateConcatAsync();

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, line, col,
            new List<SignatureChange>
            {
                new() { Action = "reorder", Order = new List<string> { "third", "first", "second" } }
            },
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        var newSig = json["data"]!["newSignature"]?.Value<string>();
        // Parameter order in the signature must reflect the requested permutation.
        var thirdIdx = newSig!.IndexOf("int third");
        var firstIdx = newSig.IndexOf("int first");
        var secondIdx = newSig.IndexOf("int second");
        thirdIdx.Should().BeLessThan(firstIdx, "third must now come before first");
        firstIdx.Should().BeLessThan(secondIdx, "first must now come before second");
    }

    [Fact]
    public async Task Apply_OnLocalFunction_UpdatesDeclarationAndCallSites()
    {
        var lines = File.ReadAllLines(_fixturePath);
        var combineLine = Array.FindIndex(lines, l => l.Contains("int Combine(int a, int b)"));
        combineLine.Should().BeGreaterThan(-1);

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, combineLine, column: 16,
            new List<SignatureChange>
            {
                new() { Action = "add", Name = "scale", Type = "int", DefaultValue = "1" }
            },
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["applied"]?.Value<bool>().Should().BeTrue();
        json["data"]!["newSignature"]?.Value<string>().Should().Contain("scale");
        // The two `Combine(...)` call sites inside Total() must be updated.
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task Apply_ThenGetDiagnostics_NoNewErrors()
    {
        var (line, col) = await LocateConcatAsync();

        var apply = await _service.ChangeSignatureAsync(
            _fixturePath, line, col,
            new List<SignatureChange>
            {
                new() { Action = "add", Name = "scale", Type = "int", DefaultValue = "10" }
            },
            preview: false);
        JObject.FromObject(apply)["success"]?.Value<bool>().Should().BeTrue();

        // After applying an additive change with a default value, the fixture file must
        // still compile — every call site got the default argument inserted.
        var diag = await _service.GetDiagnosticsAsync(
            filePath: _fixturePath,
            projectPath: null,
            severity: "Error",
            includeHidden: false);
        var diags = JObject.FromObject(diag)["data"]!["diagnostics"] as JArray;
        diags.Should().NotBeNull();
        diags!.Count.Should().Be(0, "additive signature change with default value must not break compilation");
    }

    [Fact]
    public async Task Apply_OnConstructor_UpdatesAllNewAndInitializerCallSites()
    {
        // Find the constructor by reading the fixture (avoids fragile column math).
        var lines = File.ReadAllLines(_fixturePath);
        var ctorLineIndex = Array.FindIndex(lines, l => l.Contains("public ChangeSignatureCtorHolder(int seed, string label)"));
        ctorLineIndex.Should().BeGreaterThan(-1);

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, ctorLineIndex, column: 20,
            new List<SignatureChange>
            {
                new() { Action = "add", Name = "tag", Type = "int", DefaultValue = "0" }
            },
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["applied"]?.Value<bool>().Should().BeTrue();
        // Constructor has two call sites (`: this(seed, "default")` and `new Make(...)`).
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().BeGreaterThan(0);
        json["data"]!["newSignature"]?.Value<string>().Should().Contain("tag");
    }
}
