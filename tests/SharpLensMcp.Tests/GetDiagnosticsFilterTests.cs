using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// Tests get_diagnostics filtering against an AdhocWorkspace seeded with KNOWN
// diagnostics at every severity level. This replaces four prior tests in
// AnalysisTests.cs that ran against the real (clean) solution and therefore
// could only assert tautological invariants — "if there are zero diagnostics,
// the filter doesn't produce non-matching diagnostics."
//
// The controlled setup combines:
//   - A C# source that emits CS0219 (unused-variable Warning) and a syntax error
//     (CS1002 missing-semicolon Error).
//   - AlwaysFiresAnalyzer (TEST0001, Warning)
//   - InfoFiresAnalyzer    (TEST0002, Info)
//   - HiddenFiresAnalyzer  (TEST0003, Hidden)
//
// Each severity is therefore present in the workspace, letting every filter
// branch be content-locked.
public class GetDiagnosticsFilterTests
{
    // Two methods so each per-method analyzer fires exactly twice — gives us a
    // known count for filter assertions ("Info filter must return exactly 2").
    private const string CodeWithKnownDiagnostics = @"
namespace Sample;
public class Target
{
    public void First()
    {
        int unusedFirst = 1;  // CS0219 Warning
    }
    public void Second()
    {
        int unusedSecond = 2;  // CS0219 Warning
        var broken = 5  // CS1002 Error: missing ; — keeps file parseable but emits an error
    }
}
";

    private static (RoslynService service, string filePath) BuildServiceWithAnalyzers()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            CodeWithKnownDiagnostics, fileName: "Target.cs");

        var analyzerRef = new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(
            new AlwaysFiresAnalyzer(),
            new InfoFiresAnalyzer(),
            new HiddenFiresAnalyzer()));
        var solution = workspace.CurrentSolution.AddAnalyzerReference(document.Project.Id, analyzerRef);
        workspace.TryApplyChanges(solution);

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, document.FilePath!);
    }

    [Fact]
    public async Task GetDiagnostics_NoFilter_RunAnalyzersTrue_IncludesEverySeverityExceptHidden()
    {
        var (service, _) = BuildServiceWithAnalyzers();

        var result = await service.GetDiagnosticsAsync(
            filePath: null, projectPath: null,
            severity: null, includeHidden: false,
            runAnalyzers: true);

        var data = JObject.FromObject(result)["data"]!;
        data["analyzersRan"]!.Value<bool>().Should().BeTrue(
            "the workspace has three analyzers attached");
        data["analyzerCount"]!.Value<int>().Should().Be(3,
            "exactly three analyzers were attached to the project");

        var diagnostics = (data["diagnostics"] as JArray)!;
        var bySeverity = diagnostics
            .GroupBy(d => d["severity"]!.Value<string>())
            .ToDictionary(g => g.Key!, g => g.Count());

        // includeHidden=false: TEST0003 (Hidden) must NOT appear.
        diagnostics.Any(d => d["id"]?.Value<string>() == "TEST0003").Should().BeFalse(
            "Hidden-severity diagnostics must be filtered out when includeHidden=false");
        // TEST0001 (Warning) fires once per method → 2 entries.
        diagnostics.Count(d => d["id"]?.Value<string>() == "TEST0001").Should().Be(2);
        // TEST0002 (Info) fires once per method → 2 entries.
        diagnostics.Count(d => d["id"]?.Value<string>() == "TEST0002").Should().Be(2);

        bySeverity.GetValueOrDefault("Error", 0).Should().BeGreaterThan(0,
            "the fixture's missing semicolon produces at least one Error");
        bySeverity.GetValueOrDefault("Warning", 0).Should().BeGreaterOrEqualTo(2,
            "TEST0001 emits 2 warnings, plus CS0219 unused-variable warnings");
        bySeverity.GetValueOrDefault("Info", 0).Should().Be(2,
            "TEST0002 emits exactly 2 Info entries (one per method)");
    }

    [Fact]
    public async Task GetDiagnostics_SeverityErrorFilter_OnlyReturnsErrors()
    {
        var (service, _) = BuildServiceWithAnalyzers();

        var result = await service.GetDiagnosticsAsync(
            filePath: null, projectPath: null,
            severity: "Error", includeHidden: false,
            runAnalyzers: true);

        var data = JObject.FromObject(result)["data"]!;
        var diagnostics = (data["diagnostics"] as JArray)!;
        diagnostics.Should().NotBeEmpty(
            "the fixture deliberately contains a compiler error (missing ;)");

        foreach (var diag in diagnostics)
        {
            diag["severity"]!.Value<string>().Should().Be("Error",
                "severity=Error must drop every non-Error entry");
        }
        // Analyzer diagnostics at Warning/Info must NOT appear.
        diagnostics.Any(d => d["id"]?.Value<string>() == "TEST0001").Should().BeFalse();
        diagnostics.Any(d => d["id"]?.Value<string>() == "TEST0002").Should().BeFalse();

        data["errorCount"]!.Value<int>().Should().Be(diagnostics.Count,
            "with severity=Error, errorCount must equal the array length");
        data["warningCount"]!.Value<int>().Should().Be(0,
            "with severity=Error, warningCount must be 0");
    }

    [Fact]
    public async Task GetDiagnostics_SeverityWarningFilter_OnlyReturnsWarnings()
    {
        var (service, _) = BuildServiceWithAnalyzers();

        var result = await service.GetDiagnosticsAsync(
            filePath: null, projectPath: null,
            severity: "Warning", includeHidden: false,
            runAnalyzers: true);

        var data = JObject.FromObject(result)["data"]!;
        var diagnostics = (data["diagnostics"] as JArray)!;
        diagnostics.Should().NotBeEmpty("the fixture has CS0219 unused-variable + TEST0001 warnings");

        foreach (var diag in diagnostics)
        {
            diag["severity"]!.Value<string>().Should().Be("Warning",
                "severity=Warning must drop every non-Warning entry");
        }
        diagnostics.Count(d => d["id"]?.Value<string>() == "TEST0001").Should().Be(2,
            "both method-level TEST0001 warnings must survive the filter");
        diagnostics.Any(d => d["id"]?.Value<string>() == "TEST0002").Should().BeFalse(
            "Info-severity TEST0002 must not appear");

        data["errorCount"]!.Value<int>().Should().Be(0,
            "with severity=Warning, errorCount must be 0");
    }

    [Fact]
    public async Task GetDiagnostics_SeverityInfoFilter_OnlyReturnsInfoEntries()
    {
        var (service, _) = BuildServiceWithAnalyzers();

        var result = await service.GetDiagnosticsAsync(
            filePath: null, projectPath: null,
            severity: "Info", includeHidden: false,
            runAnalyzers: true);

        var data = JObject.FromObject(result)["data"]!;
        var diagnostics = (data["diagnostics"] as JArray)!;
        diagnostics.Should().NotBeEmpty(
            "InfoFiresAnalyzer guarantees Info-severity diagnostics in this fixture");

        foreach (var diag in diagnostics)
        {
            diag["severity"]!.Value<string>().Should().Be("Info");
        }
        diagnostics.Count(d => d["id"]?.Value<string>() == "TEST0002").Should().Be(2,
            "both method-level TEST0002 Info entries must survive the filter");
        diagnostics.Any(d => d["id"]?.Value<string>() == "TEST0001").Should().BeFalse(
            "Warning-severity TEST0001 must not appear");

        data["errorCount"]!.Value<int>().Should().Be(0);
        data["warningCount"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetDiagnostics_IncludeHiddenTrue_SurfacesHiddenAnalyzerDiagnostics()
    {
        var (service, _) = BuildServiceWithAnalyzers();

        var withoutHidden = await service.GetDiagnosticsAsync(
            filePath: null, projectPath: null,
            severity: null, includeHidden: false,
            runAnalyzers: true);
        var withHidden = await service.GetDiagnosticsAsync(
            filePath: null, projectPath: null,
            severity: null, includeHidden: true,
            runAnalyzers: true);

        var visible = (JObject.FromObject(withoutHidden)["data"]!["diagnostics"] as JArray)!;
        var all = (JObject.FromObject(withHidden)["data"]!["diagnostics"] as JArray)!;

        // Concrete lock: TEST0003 (Hidden) must be absent without and present with.
        visible.Any(d => d["id"]?.Value<string>() == "TEST0003").Should().BeFalse(
            "Hidden-severity TEST0003 must not surface when includeHidden=false");
        all.Count(d => d["id"]?.Value<string>() == "TEST0003").Should().Be(2,
            "Hidden-severity TEST0003 must surface twice (one per method) when includeHidden=true");
        // includeHidden=true is a superset of includeHidden=false.
        all.Count.Should().BeGreaterThan(visible.Count,
            "the Hidden entries must increase the total count, not just replace others");
    }

    [Fact]
    public async Task GetDiagnostics_RunAnalyzersFalse_OmitsAllAnalyzerDiagnostics()
    {
        var (service, _) = BuildServiceWithAnalyzers();

        var result = await service.GetDiagnosticsAsync(
            filePath: null, projectPath: null,
            severity: null, includeHidden: true,
            runAnalyzers: false);

        var data = JObject.FromObject(result)["data"]!;
        data["analyzersRan"]!.Value<bool>().Should().BeFalse();
        data["analyzerCount"]!.Value<int>().Should().Be(0);

        var diagnostics = (data["diagnostics"] as JArray)!;
        // CS-prefixed compiler diagnostics must still appear; analyzer-emitted ones must not.
        diagnostics.Any(d => d["id"]?.Value<string>() == "TEST0001").Should().BeFalse();
        diagnostics.Any(d => d["id"]?.Value<string>() == "TEST0002").Should().BeFalse();
        diagnostics.Any(d => d["id"]?.Value<string>() == "TEST0003").Should().BeFalse();
        diagnostics.Any(d => d["id"]?.Value<string>()?.StartsWith("CS") == true)
            .Should().BeTrue("compiler-only diagnostics (CS####) must still surface");
    }

    [Fact]
    public async Task GetDiagnostics_ErrorAndWarningCountsMatchArray_WithKnownContent()
    {
        var (service, _) = BuildServiceWithAnalyzers();

        var result = await service.GetDiagnosticsAsync(
            filePath: null, projectPath: null,
            severity: null, includeHidden: false,
            runAnalyzers: true);

        var data = JObject.FromObject(result)["data"]!;
        var diagnostics = (data["diagnostics"] as JArray)!;
        // With known content, the consistency check is no longer trivially 0 == 0.
        var actualErrors = diagnostics.Count(d => d["severity"]?.Value<string>() == "Error");
        var actualWarnings = diagnostics.Count(d => d["severity"]?.Value<string>() == "Warning");
        actualErrors.Should().BeGreaterThan(0, "the fixture contains a compiler error");
        actualWarnings.Should().BeGreaterOrEqualTo(2, "TEST0001 + CS0219 produce ≥2 warnings");

        data["errorCount"]!.Value<int>().Should().Be(actualErrors);
        data["warningCount"]!.Value<int>().Should().Be(actualWarnings);
    }

    [Fact]
    public async Task GetDiagnostics_ForSpecificFile_RestrictsToThatFile_VerifiesBothDirections()
    {
        // Two-file workspace; each file emits its OWN CS0219 unused-variable warning
        // with a distinct variable name so we can tell A's diagnostics from B's by message.
        const string codeA = @"
namespace SampleA;
public class A
{
    public void M() { int unusedA = 1; }
}";
        const string codeB = @"
namespace SampleB;
public class B
{
    public void M() { int unusedB = 2; }
}";
        var (workspace, documents) = TestHelpers.CreateWorkspaceWithMultipleDocuments(
            (codeA, "A.cs"),
            (codeB, "B.cs"));
        var pathA = documents[0].FilePath!;
        var pathB = documents[1].FilePath!;

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Filter to A: every returned diagnostic must reference A.cs, none B.cs.
        var resultA = await service.GetDiagnosticsAsync(
            filePath: pathA, projectPath: null,
            severity: null, includeHidden: false, runAnalyzers: false);
        var diagsA = (JObject.FromObject(resultA)["data"]!["diagnostics"] as JArray)!;
        diagsA.Should().NotBeEmpty("A.cs has a CS0219 unused-variable warning");
        diagsA.All(d => d["filePath"]!.Value<string>()!.EndsWith("A.cs"))
            .Should().BeTrue("filter-to-A must drop B.cs entries");
        diagsA.Any(d => d["message"]?.Value<string>()?.Contains("unusedA") == true)
            .Should().BeTrue("A's specific 'unusedA' warning must appear");
        diagsA.Any(d => d["message"]?.Value<string>()?.Contains("unusedB") == true)
            .Should().BeFalse("B's 'unusedB' warning must not appear in A's results");

        // Filter to B: symmetric assertions — proves the filter actually varies with input,
        // not just always returns whatever the unfiltered call returns.
        var resultB = await service.GetDiagnosticsAsync(
            filePath: pathB, projectPath: null,
            severity: null, includeHidden: false, runAnalyzers: false);
        var diagsB = (JObject.FromObject(resultB)["data"]!["diagnostics"] as JArray)!;
        diagsB.Should().NotBeEmpty("B.cs has a CS0219 unused-variable warning");
        diagsB.All(d => d["filePath"]!.Value<string>()!.EndsWith("B.cs"))
            .Should().BeTrue("filter-to-B must drop A.cs entries");
        diagsB.Any(d => d["message"]?.Value<string>()?.Contains("unusedB") == true)
            .Should().BeTrue("B's specific 'unusedB' warning must appear");
        diagsB.Any(d => d["message"]?.Value<string>()?.Contains("unusedA") == true)
            .Should().BeFalse("A's 'unusedA' warning must not appear in B's results");
    }
}
