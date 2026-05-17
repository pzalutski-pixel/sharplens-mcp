using System.Text.RegularExpressions;
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

    // Strip whitespace before substring matching so disk assertions are robust to the
    // impl's compressed-formatting output (no spaces around `=`, no spaces after commas).
    // Both "Concat(1, 2, 3, 1)" and "Concat(1,2,3,1)" normalize to "Concat(1,2,3,1)".
    private static string Normalize(string text) => Regex.Replace(text, @"\s+", "");

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
    public async Task Preview_OnMethod_LocksFullResponseShapeAndDoesNotModifyDisk()
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
        var data = json["data"]!;
        data["preview"]?.Value<bool>().Should().BeTrue();
        data["methodName"]?.Value<string>().Should().Be("Concat");

        // Old/new signature must lock the rewrite Roslyn would emit.
        data["oldSignature"]?.Value<string>().Should()
            .Contain("Concat(int first, int second, int third)");
        data["newSignature"]?.Value<string>().Should()
            .Contain("Concat(int first, int second, int third, int extra = 0)",
                "the added int parameter with default 0 must appear at the end of the signature");

        // The fixture has exactly 5 invocation sites of Concat (4 in MethodHolder + 1 through `other`).
        // Concat is called from InvokeMethodAllPositional, InvokeMethodMixed (twice),
        // InvokeMethodNamedOnly, InvokeMethodFromAnotherClass.
        var callSites = (data["callSites"] as JArray)!;
        callSites.Count.Should().Be(5,
            "Concat has exactly 5 call sites across ChangeSignatureMethodHolder");
        data["callSitesCount"]?.Value<int>().Should().Be(5,
            "callSitesCount must equal the total — fewer than 20 so no truncation");
        data["hasMoreCallSites"]?.Value<bool>().Should().BeFalse(
            "5 < 20 cap; no truncation");

        File.ReadAllText(_fixturePath).Should().Be(before, "preview must not modify disk");
    }

    [Fact]
    public async Task Apply_AddParameter_UpdatesAllFiveCallSitesAndAppearsOnDisk()
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
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().Be(5,
            "all 5 Concat call sites must receive the new argument");
        json["data"]!["newSignature"]?.Value<string>().Should().Contain("factor");

        // Verify on disk: the declaration and at least one call site got the new arg.
        // Whitespace is stripped before matching — see Normalize().
        var diskContent = Normalize(File.ReadAllText(_fixturePath));
        diskContent.Should().Contain("intfactor=1",
            "the declaration must show the new parameter with its default (normalized)");
        // Positional call site Concat(1, 2, 3) gets the default appended: Concat(1, 2, 3, 1).
        diskContent.Should().Contain("Concat(1,2,3,1)",
            "the positional all-positional call site must have the default value appended");
    }

    [Fact]
    public async Task Apply_RemoveParameter_DropsFromDeclarationAndCallSitesOnDisk()
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
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().Be(5);
        json["data"]!["newSignature"]?.Value<string>().Should().NotContain("int third");

        // Verify on disk (whitespace stripped by Normalize): the declaration is now
        // (int first, int second) only.
        var diskContent = Normalize(File.ReadAllText(_fixturePath));
        diskContent.Should().Contain("publicintConcat(intfirst,intsecond)",
            "the declaration must drop the third parameter on disk");
        // Concat(1, 2, 3) becomes Concat(1, 2) — third arg dropped.
        diskContent.Should().Contain("Concat(1,2)",
            "positional call site must drop the third argument");
        diskContent.Should().NotContain("Concat(1,2,3)",
            "the original three-arg call must no longer exist");
        // Named-arg call sites also drop `third: 30` etc.
        diskContent.Should().NotContain("third:",
            "no `third:` named argument may remain after removing the parameter");
    }

    [Fact]
    public async Task Apply_RenameParameter_UpdatesNamedArgumentsOnAllCallSites()
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
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().Be(5,
            "every call site must be visited so named-arg labels can be re-written");
        json["data"]!["newSignature"]?.Value<string>().Should().Contain("int primary",
            "renamed parameter must appear in the new signature");
        json["data"]!["newSignature"]?.Value<string>().Should().NotContain("int first",
            "old parameter name must be gone from the signature");

        // The signature rewrite is half the contract — named-argument call sites must
        // also be updated. The fixture has `Concat(first: 4, ...)` and
        // `Concat(third: 30, first: 10, ...)`. Both must now use `primary:` not `first:`.
        // Whitespace stripped by Normalize, so "primary: 4" becomes "primary:4".
        var diskContent = Normalize(File.ReadAllText(_fixturePath));
        diskContent.Should().Contain("primary:4",
            "InvokeMethodMixed's named arg must be renamed first→primary");
        diskContent.Should().Contain("primary:10",
            "InvokeMethodNamedOnly's named arg must be renamed first→primary");
        diskContent.Should().NotContain("first:",
            "no `first:` named argument may survive the rename");
    }

    [Fact]
    public async Task Apply_Reorder_PermutesDeclarationAndPositionalCallSitesOnDisk()
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
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().Be(5);
        var newSig = json["data"]!["newSignature"]?.Value<string>();
        // Parameter order in the signature must reflect the requested permutation.
        var thirdIdx = newSig!.IndexOf("int third");
        var firstIdx = newSig.IndexOf("int first");
        var secondIdx = newSig.IndexOf("int second");
        thirdIdx.Should().BeLessThan(firstIdx, "third must now come before first");
        firstIdx.Should().BeLessThan(secondIdx, "first must now come before second");

        // Verify positional call sites on disk (whitespace stripped):
        // Concat(1, 2, 3) → Concat(3, 1, 2), Concat(7, 8, 9) → Concat(9, 7, 8),
        // Concat(0, 0, 1) → Concat(1, 0, 0).
        var diskContent = Normalize(File.ReadAllText(_fixturePath));
        diskContent.Should().Contain("Concat(3,1,2)",
            "InvokeMethodAllPositional's (1, 2, 3) must reorder to (3, 1, 2)");
        diskContent.Should().Contain("Concat(9,7,8)",
            "InvokeMethodMixed's positional (7, 8, 9) must reorder to (9, 7, 8)");
        diskContent.Should().Contain("Concat(1,0,0)",
            "InvokeMethodFromAnotherClass's (0, 0, 1) must reorder to (1, 0, 0)");
    }

    [Fact]
    public async Task Apply_OnLocalFunction_UpdatesDeclarationAndExactlyTwoCallSites()
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
        // Fixture's Total() calls Combine exactly twice — `Combine(1, 2)` and `Combine(3, 4)`.
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().Be(2);

        // On disk: both call sites must have the default appended (whitespace stripped).
        var diskContent = Normalize(File.ReadAllText(_fixturePath));
        diskContent.Should().Contain("Combine(1,2,1)",
            "first Combine call must get the scale default appended");
        diskContent.Should().Contain("Combine(3,4,1)",
            "second Combine call must get the scale default appended");
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
    public async Task Apply_OnConstructor_UpdatesExactlyTwoCallSites_ThisInitializerAndNewExpression()
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
        // Two call sites: `: this(seed, "default")` and `new ChangeSignatureCtorHolder(seed, "made")`.
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().Be(2,
            "the ctor has exactly two call sites: this-initializer and new-expression");
        json["data"]!["newSignature"]?.Value<string>().Should().Contain("tag");

        // Disk verification (whitespace stripped): both forms must include the new
        // default-argument 0.
        var diskContent = Normalize(File.ReadAllText(_fixturePath));
        diskContent.Should().Contain(":this(seed,\"default\",0)",
            "the this-initializer must have the new default argument appended");
        diskContent.Should().Contain("newChangeSignatureCtorHolder(seed,\"made\",0)",
            "the new-expression call site must have the new default argument appended");
    }

    [Fact]
    public async Task Apply_OnNonMethodPosition_ReturnsNotAMethod()
    {
        // Cursor on the class-declaration line of ChangeSignatureMethodHolder — not a
        // method/ctor/local-function. Refactoring.cs:600 returns NotAMethod.
        var lines = File.ReadAllLines(_fixturePath);
        var classLine = Array.FindIndex(lines, l => l.Contains("public class ChangeSignatureMethodHolder"));
        classLine.Should().BeGreaterThan(-1);

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, classLine, column: 14,
            new List<SignatureChange>
            {
                new() { Action = "add", Name = "x", Type = "int", DefaultValue = "0" }
            },
            preview: true);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeFalse();
        json["error"]?["code"]?.Value<string>().Should().Be(ErrorCodes.NotAMethod);
    }

    [Fact]
    public async Task Apply_MultipleChanges_AddAndRenameInOneCall_BothEffectsLandOnDisk()
    {
        // Verify the impl correctly composes multiple changes in one apply: rename
        // `first` → `alpha` AND add `extra` parameter. Both must show in the signature
        // AND on every call site (positional gets new arg appended; named gets relabeled).
        var (line, col) = await LocateConcatAsync();

        var result = await _service.ChangeSignatureAsync(
            _fixturePath, line, col,
            new List<SignatureChange>
            {
                new() { Action = "rename", Name = "first", NewName = "alpha" },
                new() { Action = "add", Name = "extra", Type = "int", DefaultValue = "0" }
            },
            preview: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["callSitesUpdated"]?.Value<int>().Should().Be(5,
            "every call site must be visited for the combined add+rename to land");

        var newSig = json["data"]!["newSignature"]?.Value<string>()!;
        newSig.Should().Contain("int alpha", "rename must surface in the new signature");
        newSig.Should().Contain("int extra = 0", "add must surface in the new signature");
        newSig.Should().NotContain("int first", "old parameter name must disappear");

        var diskContent = Normalize(File.ReadAllText(_fixturePath));
        diskContent.Should().Contain("alpha:4",
            "the named-arg call site must have first→alpha applied (whitespace stripped)");
        diskContent.Should().Contain("Concat(1,2,3,0)",
            "the positional call site must have the extra=0 default appended");
    }
}
