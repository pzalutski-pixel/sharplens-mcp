using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_complexity_metrics and the find_untested_code / find_god_objects estimate
// share one cyclomatic counter, so the same method yields the same number from
// both tools. The counter also counts do-while loops and switch-expression arms,
// which were previously dropped.
public class CyclomaticUnificationTests
{
    private const string Code = @"
namespace Production
{
    public class CycHost
    {
        public int DoWhileMethod(int n)
        {
            do { n--; } while (n > 0);
            return n;
        }

        public string SwitchExprMethod(int x) => x switch
        {
            0 => ""zero"",
            1 => ""one"",
            _ => ""many""
        };
    }
}";

    [Fact]
    public async Task ComplexityMetrics_CountsDoWhileAndSwitchExpressionArms()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "CycHost.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.GetComplexityMetricsAsync(document.FilePath!));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var methods = (json["data"]!["methods"] as JArray)!;

        var doWhile = methods.Single(m => m["name"]!.Value<string>() == "DoWhileMethod");
        doWhile["metrics"]!["cyclomatic"]!.Value<int>().Should().Be(2,
            "1 + the do-while loop (previously the do-while was not counted)");

        var switchExpr = methods.Single(m => m["name"]!.Value<string>() == "SwitchExprMethod");
        switchExpr["metrics"]!["cyclomatic"]!.Value<int>().Should().Be(3,
            "1 + the two non-discard switch-expression arms (previously the arms were not counted)");
    }

    [Fact]
    public async Task BothTools_AgreeOnCyclomatic_ForTheSameMethods()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "CycHost.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var metrics = JObject.FromObject(await service.GetComplexityMetricsAsync(document.FilePath!));
        var byNameMetrics = (metrics["data"]!["methods"] as JArray)!
            .ToDictionary(m => m["name"]!.Value<string>()!, m => m["metrics"]!["cyclomatic"]!.Value<int>());

        var untested = JObject.FromObject(await service.FindUntestedCodeAsync(
            projectName: null, includeProperties: false, includeInternal: false, maxResults: 50));
        var uncovered = (untested["data"]!["uncoveredSymbols"] as JArray)!;

        foreach (var name in new[] { "DoWhileMethod", "SwitchExprMethod" })
        {
            var symbol = uncovered.Single(s => s["fullName"]!.Value<string>()!.Contains("CycHost." + name));
            symbol["complexity"]!.Value<int>().Should().Be(byNameMetrics[name],
                $"get_complexity_metrics and the find_untested_code estimate must report the same cyclomatic for {name}");
        }
    }

    [Fact]
    public async Task ComplexityMetrics_DoWhile_CountsInCognitiveAndNesting()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "CycHost.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.GetComplexityMetricsAsync(document.FilePath!));
        var doWhile = (json["data"]!["methods"] as JArray)!
            .Single(m => m["name"]!.Value<string>() == "DoWhileMethod")["metrics"]!;

        doWhile["cognitive"]!.Value<int>().Should().Be(1,
            "a do-while is a loop and adds one cognitive point at nesting level 0");
        doWhile["nesting"]!.Value<int>().Should().Be(1,
            "the do-while body is one nesting level deep");
    }

    [Fact]
    public async Task ComplexityMetrics_Constructor_ReportsParameterCount_FileAndCursor()
    {
        const string code = @"
namespace Production
{
    public class WithCtor
    {
        public WithCtor(int a, string b) { }
    }
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "WithCtor.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // File scope: the constructor entry — named by its type, as C# declares it — reports
        // its real parameter count, not 0.
        var fileJson = JObject.FromObject(await service.GetComplexityMetricsAsync(document.FilePath!));
        var ctor = (fileJson["data"]!["methods"] as JArray)!
            .Single(m => m["name"]!.Value<string>() == "WithCtor")["metrics"]!;
        ctor["parameters"]!.Value<int>().Should().Be(2, "the constructor declares two parameters");

        // Cursor on the constructor: the position path analyzes it, not SymbolNotFound.
        var lines = code.Split('\n');
        var ctorLine = Array.FindIndex(lines, l => l.Contains("public WithCtor("));
        var col = lines[ctorLine].IndexOf("WithCtor(", StringComparison.Ordinal);
        var cursorJson = JObject.FromObject(
            await service.GetComplexityMetricsAsync(document.FilePath!, ctorLine, col));
        cursorJson["success"]!.Value<bool>().Should().BeTrue(cursorJson.ToString());
        cursorJson["data"]!["metrics"]!["parameters"]!.Value<int>().Should().Be(2,
            "positioning on a constructor must analyze it and report its parameter count");
    }
}
