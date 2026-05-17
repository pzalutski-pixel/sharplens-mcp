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
        data["analyzerCount"]?.Value<int>().Should().BeGreaterThan(0);

        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNullOrEmpty();
        // The analyzer fires once per method declaration. SampleCode has 2 methods.
        diagnostics!.Count(d => d["id"]?.Value<string>() == AlwaysFiresAnalyzer.DiagnosticId)
            .Should().Be(2, "AlwaysFiresAnalyzer must report once per method declaration");
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
        diagnostics.Should().NotBeNull();
        diagnostics!.Any(d => d["id"]?.Value<string>() == AlwaysFiresAnalyzer.DiagnosticId)
            .Should().BeFalse("with runAnalyzers:false, analyzer rules must not appear");
    }
}
