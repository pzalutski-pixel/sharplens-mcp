using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Consistency;

// Cross-tool invariants of the quality tools:
//   - a member's cyclomatic complexity is the same number from get_complexity_metrics and
//     from find_untested_code's complexity (one counter, one definition);
//   - over a multi-targeted project every quality tool reports a finding once, with the
//     frameworks it occurs in, exactly as the single-targeted project reports it once.
public class QualityToolConsistencyTests : CrossToolConsistencyTestBase
{
    [Fact]
    public async Task Cyclomatic_IsIdentical_FromComplexityMetricsAndUntestedCode()
    {
        using var builder = new QualitySolutionBuilder();
        builder.AddProject("Lib", new[] { ("Metrics.cs", ComplexityMetricsCorrectnessTests.Source) });
        Load(builder.Build());

        var metrics = await CallDataAsync("get_complexity_metrics", new { filePath = builder.FilePath("Lib", "Metrics.cs") });
        var untested = await CallDataAsync("find_untested_code", new { projectName = "Lib", includeProperties = false, maxResults = 100 });

        var fromMetrics = ((JArray)metrics["methods"]!)
            .Where(m => new[] { "ElseIfChain", "Metrics", "WithAttr", "Coalesce", "Nested", "Patterns" }.Contains(m["name"]!.Value<string>()))
            .ToDictionary(m => m["name"]!.Value<string>()!, m => m["metrics"]!["cyclomatic"]!.Value<int>());
        var fromUntested = ((JArray)untested["uncoveredSymbols"]!)
            .ToDictionary(
                s => s["fullName"]!.Value<string>()!.Split('(')[0].Split('.').Last(),
                s => s["complexity"]!.Value<int>());

        fromMetrics.Should().Equal(new Dictionary<string, int>
        {
            ["ElseIfChain"] = 5, ["Metrics"] = 2, ["WithAttr"] = 1, ["Coalesce"] = 3, ["Nested"] = 2, ["Patterns"] = 8,
        });
        foreach (var (name, cyclomatic) in fromMetrics)
        {
            fromUntested[name].Should().Be(cyclomatic, $"{name}'s cyclomatic must not depend on which tool reports it");
        }
    }

    private const string FindingsSource = """
        namespace Multi
        {
            public class Findings
            {
                public async void Fire() { await System.Threading.Tasks.Task.Delay(1); }

                public int Dead()
                {
                    return 1;
                    return 2;
                }

                public void Unused() { int unusedLocal; }

                public void bad_name() { }
            }
        }
        """;

    private static string Frameworks(JToken item) =>
        item["targetFrameworks"] is JArray frameworks ? string.Join(",", frameworks.Values<string>()) : "";

    [Theory]
    [InlineData(true, "net10.0,net8.0")]
    [InlineData(false, "")]
    public async Task EveryQualityTool_ReportsAFindingOnce(bool multiTargeted, string frameworks)
    {
        using var builder = new QualitySolutionBuilder();
        if (multiTargeted) builder.AddMultiTargeted("Multi", new[] { ("Findings.cs", FindingsSource) });
        else builder.AddProject("Multi", new[] { ("Findings.cs", FindingsSource) });
        Load(builder.Build());

        var diagnostics = (JArray)(await CallDataAsync("get_diagnostics", new { projectPath = "Multi", runAnalyzers = false }))["diagnostics"]!;
        var asyncIssues = (JArray)(await CallDataAsync("find_async_issues", new { projectName = "Multi" }))["issues"]!;
        var deadBranches = (JArray)(await CallDataAsync("find_dead_branches", new { projectName = "Multi" }))["deadBranches"]!;
        var violations = (JArray)(await CallDataAsync("find_naming_violations", new { projectName = "Multi" }))["violations"]!;

        diagnostics.Select(d => $"{d["id"]} {Frameworks(d)}").Should().Equal($"CS0162 {frameworks}", $"CS0168 {frameworks}");
        asyncIssues.Select(i => $"{i["category"]} {Frameworks(i)}").Should().Equal($"asyncVoid {frameworks}");
        deadBranches.Select(d => $"{d["method"]} {Frameworks(d)}").Should().Equal($"Findings.Dead {frameworks}");
        violations.Select(v => $"{v["name"]} {Frameworks(v)}").Should().Equal($"bad_name {frameworks}");
    }
}
