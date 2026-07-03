using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Global invariant canaries over this repo's own solution. Per-tool tests
// assert shapes and fixture content; these assert the one property every
// analysis tool depends on — the compilations SharpLens builds for a CLEAN
// solution carry zero compiler errors. A regression that poisons the
// compilation pipeline (like a generator pass running twice) trips these
// regardless of which tool would have surfaced it first.
public class SolutionHealthInvariantTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetDiagnostics_SolutionWide_ReportsZeroCompilerErrors()
    {
        // dotnet build is clean on this solution, so the compiler-only pass over
        // every project must agree. Any nonzero count here means SharpLens's own
        // compilation pipeline manufactured the errors.
        var result = await Service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: "Error",
            includeHidden: false,
            runAnalyzers: false);

        AssertSuccess(result);
        var data = GetData(result);
        var diagnostics = (data["diagnostics"] as JArray)!;
        data["errorCount"]!.Value<int>().Should().Be(0,
            "the solution builds clean, so SharpLens's compilations must too; " +
            $"got: {string.Join("; ", diagnostics.Take(5).Select(d => d["id"] + " " + d["message"]))}");
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProjectCompilation_GeneratorProject_ContainsEachGeneratedTreeExactlyOnce()
    {
        // The tests project carries the System.Text.Json source-generator fixture
        // (FixtureJsonContext). The compilation every tool consumes must contain
        // its generated trees — and contain them ONCE: the workspace already runs
        // generators on Roslyn 5.3, so a second driver pass duplicates every
        // generated member.
        var solution = Service.GetSolutionForTesting()!;
        var testsProject = solution.Projects.First(p => p.Name == "SharpLensMcp.Tests");

        var compilation = await Service.GetProjectCompilationAsync(testsProject);
        compilation.Should().NotBeNull();

        var fixtureContextTrees = compilation!.SyntaxTrees
            .Where(t => t.FilePath.EndsWith("FixtureJsonContext.g.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        fixtureContextTrees.Should().HaveCount(1,
            "generated code must be visible to tools exactly once — zero means " +
            "generator output is invisible (the 1.5.2 phantom-CS0117 bug), more " +
            "than one means the generator pass ran twice");
    }
}
