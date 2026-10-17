using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_project_health computes its breakdowns from the FULL data — diagnostics.topByCount
// is not cut by the diagnostics cap, unusedCode.topByKind is not cut to topN symbols — scans
// the same target frameworks in every section (a plain multi-targeted name covers every
// framework, a framework-qualified one that framework alone), and rejects a non-positive
// topN.
public class ProjectHealthFullDataTests
{
    private const string LibSource = """
        namespace Lib
        {
            public class Diag
            {
                public void A() { int a1; }
                public void B() { int b1; }
                public void C() { int c1; }
                public void D() { int d1 = 1; }
                public int P1 { get; set; }
                public int P2 { get; set; }
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

                public async void Fire() { await System.Threading.Tasks.Task.Delay(1); }
            }
        }
        """;

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        builder.AddProject("Lib", new[] { ("Diag.cs", LibSource) });
        builder.AddMultiTargeted("Multi", new[] { ("MultiCode.cs", MultiSource) });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    [Fact]
    public async Task Breakdowns_AreComputedFromTheFullData()
    {
        using var builder = Build(out var service, out _);
        service.SetMaxDiagnosticsForTesting(2);

        var data = JObject.FromObject(await service.GetProjectHealthAsync("Lib", includeAnalyzers: false, topN: 1))["data"]!;

        ((JArray)data["diagnostics"]!["topByCount"]!).Select(d => $"{d["id"]}:{d["count"]}").Should().Equal("CS0168:3");
        data["diagnostics"]!["warningCount"]!.Value<int>().Should().Be(4);

        var unusedFull = JObject.FromObject(await service.FindUnusedCodeAsync("Lib", includePrivate: false, includeInternal: false, maxResults: 500))["data"]!;
        var expectedByKind = ((JArray)unusedFull["unusedSymbols"]!)
            .GroupBy(s => s["kind"]!.Value<string>()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}:{g.Count()}").ToList();
        expectedByKind.Sum(k => int.Parse(k.Split(':')[1])).Should().BeGreaterThan(1,
            "the fixture must have more unused symbols than topN for the check to discriminate");
        ((JArray)data["unusedCode"]!["topByKind"]!).Select(k => $"{k["kind"]}:{k["count"]}").Should().Equal(expectedByKind);
    }

    [Theory]
    [InlineData("Multi", 2, "net10.0,net8.0")]
    [InlineData("Multi(net10.0)", 2, "net10.0")]
    [InlineData("Multi(net8.0)", 1, "net8.0")]
    public async Task EverySection_ScansTheSameFrameworks(string projectName, int warnings, string frameworks)
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.GetProjectHealthAsync(projectName, includeAnalyzers: false))["data"]!;

        data["projectName"]!.Value<string>().Should().Be("Multi");
        string.Join(",", data["targetFrameworks"]!.Values<string>()).Should().Be(frameworks);
        data["diagnostics"]!["warningCount"]!.Value<int>().Should().Be(warnings);
        data["asyncHygiene"]!["count"]!.Value<int>().Should().Be(1, "the one async void, reported once across frameworks");
    }

    [Fact]
    public async Task NonPositiveTopN_IsAnInvalidParameter()
    {
        using var builder = Build(out var service, out _);

        var json = JObject.FromObject(await service.GetProjectHealthAsync("Lib", topN: 0));

        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be("topN must be between 1 and 100 (got 0)");
    }

    [Fact]
    public async Task ViaDispatcher_PlainMultiTargetedName_AndInvalidTopN()
    {
        using var builder = Build(out _, out var dispatcher);

        var data = await dispatcher.DataAsync("get_project_health", new { projectName = "Multi", includeAnalyzers = false });
        data["diagnostics"]!["warningCount"]!.Value<int>().Should().Be(2);
        data["asyncHygiene"]!["count"]!.Value<int>().Should().Be(1);

        var error = await dispatcher.ErrorAsync("get_project_health", new { projectName = "Lib", topN = -3 }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("topN must be between 1 and 100 (got -3)");
    }
}
