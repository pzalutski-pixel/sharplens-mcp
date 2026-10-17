using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// get_diagnostics scoping and fidelity, at the RoslynService level and through the
// dispatcher:
//   - projectPath accepts the project file path in any form a tool prints or a user types
//     (solution-relative, either slash, absolute) or the project name; an unknown one errors;
//   - a multi-targeted project is evaluated for EVERY target framework, each diagnostic
//     reported once with the frameworks it occurs in (file, project and solution scope);
//   - DiagnosticSuppressors apply to compiler diagnostics, as in dotnet build;
//   - analyzerCount counts distinct analyzers; severity "Hidden" returns hidden diagnostics;
//   - a file-scoped request analyzes only that file;
//   - entries carry a column; the next-step hint never calls informational findings "healthy".
public class DiagnosticsScopeTests
{
    private const string LibSource = """
        namespace Lib
        {
            public class Diag
            {
                public void First()
                {
                    int unusedA;
                }

                public void Second()
                {
                    int unusedB;
                }
            }
        }
        """;

    private const string MultiSource = """
        namespace Multi
        {
            public class TfmSpecific
            {
                public void OnlyNet10()
                {
        #if NET10_0_OR_GREATER
                    int unusedOnNet10;
        #endif
                    int unusedEverywhere;
                }
            }
        }
        """;

    private const string SuppressedSource = """
        namespace Sup
        {
            public class S
            {
                public void M()
                {
                    int suppressedVar;
                    int shownVar;
                }
            }
        }
        """;

    private static QualitySolutionBuilder BuildSolution(out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        builder.AddProject("Lib", new[] { ("Diag.cs", LibSource) });
        builder.AddMultiTargeted("Multi", new[] { ("MultiCode.cs", MultiSource) });
        builder.AddProject("Sup", new[] { ("S.cs", SuppressedSource) },
            analyzers: new[] { new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new UnusedVariableSuppressor())) });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    private static List<string> Entries(JToken data) =>
        ((JArray)data["diagnostics"]!).Select(d =>
            $"{d["id"]} {d["filePath"]}:{d["line"]}:{d["column"]} [{string.Join(",", (d["targetFrameworks"] as JArray)?.Select(t => t.Value<string>()) ?? Array.Empty<string>())}]")
            .ToList();

    private static string Entry(string source, string marker, string file) =>
        $"CS0168 {file}:{RetargetedSources.Locate(source, marker).line}:{RetargetedSources.Locate(source, marker).column}";

    [Theory]
    [InlineData("Lib/Lib.csproj")]
    [InlineData("Lib\\Lib.csproj")]
    [InlineData("Lib")]
    [InlineData("lib")]
    public async Task ProjectPath_AcceptsSolutionRelativePathsAndTheProjectName(string projectPath)
    {
        using var builder = BuildSolution(out var service, out _);

        var data = JObject.FromObject(await service.GetDiagnosticsAsync(null, projectPath, null, false, runAnalyzers: false))["data"]!;

        Entries(data).Should().Equal(
            Entry(LibSource, "unusedA", "Lib/Diag.cs") + " []",
            Entry(LibSource, "unusedB", "Lib/Diag.cs") + " []");
        data["warningCount"]!.Value<int>().Should().Be(2);
    }

    [Fact]
    public async Task ProjectPath_AcceptsAbsolutePathsWithEitherSlash()
    {
        using var builder = BuildSolution(out var service, out _);
        var absolute = builder.ProjectFilePath("Lib");

        foreach (var form in new[] { absolute, absolute.Replace('\\', '/') })
        {
            var data = JObject.FromObject(await service.GetDiagnosticsAsync(null, form, null, false, runAnalyzers: false))["data"]!;
            Entries(data).Should().Equal(
                Entry(LibSource, "unusedA", "Lib/Diag.cs") + " []",
                Entry(LibSource, "unusedB", "Lib/Diag.cs") + " []");
        }
    }

    [Fact]
    public async Task ProjectPath_Unknown_IsAnInvalidParameter_NotAHealthyEmptyResult()
    {
        using var builder = BuildSolution(out var service, out _);

        var json = JObject.FromObject(await service.GetDiagnosticsAsync(null, "Nope/Nope.csproj", null, false, runAnalyzers: false));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be("Project not found: Nope/Nope.csproj");
    }

    [Theory]
    [InlineData("project")]
    [InlineData("file")]
    [InlineData("solution")]
    public async Task MultiTargetedProject_EveryFrameworkIsEvaluated_EachDiagnosticReportedOnce(string scope)
    {
        using var builder = BuildSolution(out var service, out _);
        var file = builder.FilePath("Multi", "MultiCode.cs");

        var result = scope switch
        {
            "project" => await service.GetDiagnosticsAsync(null, "Multi", null, false, runAnalyzers: false),
            "file" => await service.GetDiagnosticsAsync(file, null, null, false, runAnalyzers: false),
            _ => await service.GetDiagnosticsAsync(null, null, null, false, runAnalyzers: false),
        };
        var data = JObject.FromObject(result)["data"]!;

        Entries(data).Where(e => e.Contains("Multi/")).Should().Equal(
            Entry(MultiSource, "unusedOnNet10", "Multi/MultiCode.cs") + " [net10.0]",
            Entry(MultiSource, "unusedEverywhere", "Multi/MultiCode.cs") + " [net10.0,net8.0]");
    }

    [Fact]
    public async Task FrameworkQualifiedProjectName_EvaluatesThatFrameworkOnly()
    {
        using var builder = BuildSolution(out var service, out _);

        var data = JObject.FromObject(await service.GetDiagnosticsAsync(null, "Multi(net8.0)", null, false, runAnalyzers: false))["data"]!;

        Entries(data).Should().Equal(Entry(MultiSource, "unusedEverywhere", "Multi/MultiCode.cs") + " [net8.0]");
        data["warningCount"]!.Value<int>().Should().Be(1);
    }

    [Theory]
    [InlineData(true, "project")]
    [InlineData(false, "project")]
    [InlineData(true, "file")]
    [InlineData(false, "file")]
    public async Task DiagnosticSuppressors_ApplyToCompilerDiagnostics(bool runAnalyzers, string scope)
    {
        using var builder = BuildSolution(out var service, out _);

        var result = scope == "project"
            ? await service.GetDiagnosticsAsync(null, "Sup", null, false, runAnalyzers)
            : await service.GetDiagnosticsAsync(builder.FilePath("Sup", "S.cs"), null, null, false, runAnalyzers);
        var data = JObject.FromObject(result)["data"]!;

        Entries(data).Should().Equal(Entry(SuppressedSource, "shownVar", "Sup/S.cs") + " []");
        data["warningCount"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task AnalyzerCount_CountsDistinctAnalyzers_NotPerProjectSums()
    {
        using var builder = new QualitySolutionBuilder();
        builder.AddProject("A", new[] { ("A.cs", "namespace A { public class C { public void M() { } } }") },
            analyzers: new[] { new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new AlwaysFiresAnalyzer())) });
        builder.AddProject("B", new[] { ("B.cs", "namespace B { public class C { public void M() { } } }") },
            analyzers: new[] { new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new AlwaysFiresAnalyzer())) });
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(builder.Build());

        var data = JObject.FromObject(await service.GetDiagnosticsAsync(null, null, null, false, runAnalyzers: true))["data"]!;

        data["analyzerCount"]!.Value<int>().Should().Be(1, "both projects run the one AlwaysFiresAnalyzer");
        ((JArray)data["diagnostics"]!).Select(d => d["id"]!.Value<string>()).Should().Equal("TEST0001", "TEST0001");
    }

    [Fact]
    public async Task SeverityHidden_ReturnsTheHiddenDiagnostics()
    {
        using var builder = new QualitySolutionBuilder();
        builder.AddProject("H", new[] { ("H.cs", "namespace H { public class C { public void M() { } public void N() { } } }") },
            analyzers: new[] { new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new HiddenFiresAnalyzer())) });
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(builder.Build());

        var data = JObject.FromObject(await service.GetDiagnosticsAsync(null, null, "Hidden", includeHidden: false, runAnalyzers: true))["data"]!;

        ((JArray)data["diagnostics"]!).Select(d => $"{d["id"]} {d["severity"]} {d["column"]}")
            .Should().Equal("TEST0003 Hidden 43", "TEST0003 Hidden 63");
    }

    [Fact]
    public async Task InfoOnlyResult_IsNotCalledHealthy_AndAnEmptyResultSaysSo()
    {
        using var builder = new QualitySolutionBuilder();
        builder.AddProject("I", new[] { ("I.cs", "namespace I { public class C { public void M() { } } }") },
            analyzers: new[] { new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new InfoFiresAnalyzer())) });
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(builder.Build());

        var info = JObject.FromObject(await service.GetDiagnosticsAsync(null, null, null, false, runAnalyzers: true));
        info["meta"]!["SuggestedNextTools"]!.Values<string>().Should().Equal(
            "No errors or warnings - the remaining diagnostics are informational",
            "get_code_fixes for a diagnostic to see available fixes");

        var clean = JObject.FromObject(await service.GetDiagnosticsAsync(null, null, null, false, runAnalyzers: false));
        clean["meta"]!["SuggestedNextTools"]!.Values<string>().Should().Equal("No diagnostics found in the requested scope");
    }

    [Fact]
    public async Task FileScope_AnalyzesOnlyThatFile()
    {
        var recorder = new TreeRecordingAnalyzer();
        using var builder = new QualitySolutionBuilder();
        builder.AddProject("R", new[]
            {
                ("A.cs", "namespace R { public class A { } }"),
                ("B.cs", "namespace R { public class B { } }"),
            },
            analyzers: new[] { new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(recorder)) });
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(builder.Build());

        var data = JObject.FromObject(await service.GetDiagnosticsAsync(builder.FilePath("R", "A.cs"), null, null, false, runAnalyzers: true))["data"]!;

        recorder.AnalyzedFiles.Should().BeEquivalentTo(new[] { "A.cs" }, "a file-scoped request must not run analyzers over the rest of the project");
        ((JArray)data["diagnostics"]!).Select(d => $"{d["id"]} {d["filePath"]}").Should().Equal("TEST0100 R/A.cs");
    }

    [Fact]
    public async Task ViaDispatcher_ProjectPathForms_MultiTargetMerge_Suppression_AndUnknownPath()
    {
        using var builder = BuildSolution(out _, out var dispatcher);

        var lib = await dispatcher.DataAsync("get_diagnostics", new { projectPath = "Lib/Lib.csproj", runAnalyzers = false });
        Entries(lib).Should().Equal(
            Entry(LibSource, "unusedA", "Lib/Diag.cs") + " []",
            Entry(LibSource, "unusedB", "Lib/Diag.cs") + " []");

        var multi = await dispatcher.DataAsync("get_diagnostics", new { projectPath = "Multi", runAnalyzers = false });
        Entries(multi).Should().Equal(
            Entry(MultiSource, "unusedOnNet10", "Multi/MultiCode.cs") + " [net10.0]",
            Entry(MultiSource, "unusedEverywhere", "Multi/MultiCode.cs") + " [net10.0,net8.0]");

        var sup = await dispatcher.DataAsync("get_diagnostics", new { projectPath = "Sup" });
        Entries(sup).Should().Equal(Entry(SuppressedSource, "shownVar", "Sup/S.cs") + " []");

        var error = await dispatcher.ErrorAsync("get_diagnostics", new { projectPath = "Nope/Nope.csproj" }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("Project not found: Nope/Nope.csproj");
    }
}
