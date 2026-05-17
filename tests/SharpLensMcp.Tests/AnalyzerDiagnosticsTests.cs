using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// Exercises get_diagnostics's analyzer integration end-to-end with a real
// DiagnosticAnalyzer plugged into an AdhocWorkspace. The analyzer lives in a
// separate netstandard2.0 project (per RS1038/RS1041) so the test project stays
// clean. The compiler alone won't emit AlwaysFiresAnalyzer's TEST0001 rule -
// only the analyzer pipeline does. So this test proves
// WithAnalyzers().GetAllDiagnosticsAsync() is being invoked.
public class AnalyzerDiagnosticsTests
{
    private const string SampleCode = @"
namespace Sample;
public class Target
{
    public void DoWork() { }
    public int Compute(int x) => x * 2;
}
";

    private static AdhocWorkspace CreateWorkspaceWithAnalyzer()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(SampleCode, fileName: "Sample.cs");

        var analyzerRef = new AnalyzerImageReference(
            ImmutableArray.Create<DiagnosticAnalyzer>(new AlwaysFiresAnalyzer()));
        var solution = workspace.CurrentSolution.AddAnalyzerReference(document.Project.Id, analyzerRef);
        workspace.TryApplyChanges(solution);

        return workspace;
    }

    [Fact]
    public async Task GetDiagnostics_WithAnalyzerAttached_ReportsAnalyzerDiagnostics()
    {
        var workspace = CreateWorkspaceWithAnalyzer();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: null,
            includeHidden: false,
            runAnalyzers: true);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());

        var data = json["data"]!;
        data["analyzersRan"]?.Value<bool>().Should().BeTrue();
        data["analyzerCount"]?.Value<int>().Should().Be(1,
            "exactly one analyzer (AlwaysFiresAnalyzer) was attached");

        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNullOrEmpty();
        // The analyzer fires once per method declaration. SampleCode has 2 methods.
        var test0001 = diagnostics!
            .Where(d => d["id"]?.Value<string>() == AlwaysFiresAnalyzer.DiagnosticId)
            .ToList();
        test0001.Should().HaveCount(2, "AlwaysFiresAnalyzer must report once per method declaration");

        // Verify EACH analyzer diagnostic's shape round-trips correctly. Without this,
        // a broken message template or stripped location would still pass the count check.
        var byMethod = test0001
            .Select(d => d["message"]?.Value<string>() ?? "")
            .OrderBy(m => m)
            .ToList();
        byMethod[0].Should().Contain("Method 'Compute' triggers TEST0001",
            "the analyzer's messageFormat must surface the method name via the '{0}' placeholder");
        byMethod[1].Should().Contain("Method 'DoWork' triggers TEST0001");

        foreach (var d in test0001)
        {
            d["severity"]?.Value<string>().Should().Be("Warning",
                "AlwaysFiresAnalyzer declares defaultSeverity: Warning");
            d["filePath"]?.Value<string>().Should().EndWith("Sample.cs",
                "the diagnostic location must surface the source file");
            d["line"]?.Type.Should().Be(JTokenType.Integer,
                "every diagnostic must carry a numeric line position");
        }
    }

    [Fact]
    public async Task GetDiagnostics_WithAnalyzerAttached_RunAnalyzersFalse_OmitsAnalyzerDiagnostics()
    {
        var workspace = CreateWorkspaceWithAnalyzer();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: null,
            includeHidden: false,
            runAnalyzers: false);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue();

        var data = json["data"]!;
        data["analyzersRan"]?.Value<bool>().Should().BeFalse();
        data["analyzerCount"]?.Value<int>().Should().Be(0);

        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull("response must always carry a diagnostics array");
        diagnostics!.Any(d => d["id"]?.Value<string>() == AlwaysFiresAnalyzer.DiagnosticId)
            .Should().BeFalse("with runAnalyzers:false, analyzer rules must not appear");
    }
}
