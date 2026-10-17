using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_complexity_metrics counts what each metric defines:
//   - nesting: an else-if chain is one level, not one level per link;
//   - cognitive (Sonar): a switch expression is a structural increment, a lambda or local
//     function nests its body, pattern and/or runs count like &&/||;
//   - cyclomatic: ??=, pattern and/or, switch-label/arm `when` clauses and catch filters
//     are decision points, like ?? and the case labels themselves;
// members are named the way C# declares them (the type name for a constructor,
// "operator +", "this[].get") and located at their identifier, not their attributes; an
// unknown metric name is rejected; the file path is reported solution-relative.
public class ComplexityMetricsCorrectnessTests
{
    internal const string Source = """
        namespace Lib
        {
            public class Metrics
            {
                public int ElseIfChain(int x)
                {
                    if (x == 1) return 1;
                    else if (x == 2) return 2;
                    else if (x == 3) return 3;
                    else if (x == 4) return 4;
                    return 0;
                }

                public Metrics()
                {
                    if (System.DateTime.Now.Year > 2000) { System.Console.WriteLine(); }
                }

                [System.Obsolete("x")]
                public int WithAttr(int a)
                {
                    return a;
                }

                public int SwitchExpr(int x) => x switch { 1 => 10, 2 => 20, _ => 0 };

                public int this[int i] { get { return i > 0 ? i : -i; } }

                public static Metrics operator +(Metrics a, Metrics b) => a;

                public int Coalesce(string? s, string? t)
                {
                    s ??= "a";
                    return (t ?? "b").Length;
                }

                public int Nested(int[] xs)
                {
                    return System.Linq.Enumerable.Count(xs, x =>
                    {
                        if (x > 0) return true;
                        return false;
                    });
                }

                public int Patterns(object o, int x)
                {
                    if (x is 1 or 2 or 3) return 1;
                    switch (o)
                    {
                        case int i when i > 0: return 2;
                    }
                    try { } catch (System.Exception) when (x > 0) { }
                    return 0;
                }
            }
        }
        """;

    private static int LineOf(string marker) => RetargetedSources.Locate(Source, marker).line;

    // name, line, cyclomatic, cognitive, nesting — in declaration order.
    private static readonly string[] Expected =
    {
        $"ElseIfChain {LineOf("ElseIfChain")} 5 4 1",
        $"Metrics {LineOf("public Metrics()")} 2 1 1",
        $"WithAttr {LineOf("WithAttr")} 1 0 0",
        $"SwitchExpr {LineOf("SwitchExpr")} 3 1 0",
        $"this[].get {LineOf("get {")} 2 1 0",
        $"operator + {LineOf("operator +")} 1 0 0",
        $"Coalesce {LineOf("Coalesce")} 3 0 0",
        $"Nested {LineOf("Nested")} 2 2 1",
        $"Patterns {LineOf("Patterns")} 8 4 1",
    };

    private static List<string> Rows(JToken data) =>
        ((JArray)data["methods"]!).Select(m =>
            $"{m["name"]} {m["line"]} {m["metrics"]!["cyclomatic"]} {m["metrics"]!["cognitive"]} {m["metrics"]!["nesting"]}").ToList();

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher, out string file)
    {
        var builder = new QualitySolutionBuilder();
        builder.AddProject("Lib", new[] { ("Metrics.cs", Source) });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        file = builder.FilePath("Lib", "Metrics.cs");
        return builder;
    }

    [Fact]
    public async Task FileScope_ReportsEachMetricByItsDefinition()
    {
        using var builder = Build(out var service, out _, out var file);

        var data = JObject.FromObject(await service.GetComplexityMetricsAsync(file))["data"]!;

        Rows(data).Should().Equal(Expected);
        data["filePath"]!.Value<string>().Should().Be("Lib/Metrics.cs");
    }

    [Theory]
    [InlineData("public Metrics()", "Metrics", "method")]
    [InlineData("operator +", "operator +", "method")]
    [InlineData("i > 0 ?", "this[].get", "accessor")]
    public async Task CursorScope_NamesMembersAsCSharpDeclaresThem(string marker, string name, string scope)
    {
        using var builder = Build(out var service, out _, out var file);
        var (line, column) = RetargetedSources.Locate(Source, marker);

        var data = JObject.FromObject(await service.GetComplexityMetricsAsync(file, line, column))["data"]!;

        data["name"]!.Value<string>().Should().Be(name);
        data["scope"]!.Value<string>().Should().Be(scope);
    }

    [Fact]
    public async Task UnknownMetricName_IsRejected()
    {
        using var builder = Build(out var service, out _, out var file);

        var json = JObject.FromObject(await service.GetComplexityMetricsAsync(file, metrics: new List<string> { "Cyclomatic", "bogus" }));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            "metrics must be one of: cyclomatic, nesting, loc, parameters, cognitive (got 'bogus')");
    }

    [Fact]
    public async Task EmptyMetricList_IsRejected()
    {
        using var builder = Build(out var service, out _, out var file);

        var json = JObject.FromObject(await service.GetComplexityMetricsAsync(file, metrics: new List<string>()));

        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            "metrics must name at least one of: cyclomatic, nesting, loc, parameters, cognitive");
    }

    [Fact]
    public async Task ViaDispatcher_FileScopeValues_AndUnknownMetric()
    {
        using var builder = Build(out _, out var dispatcher, out var file);

        var data = await dispatcher.DataAsync("get_complexity_metrics", new { filePath = file });
        Rows(data).Should().Equal(Expected);
        data["filePath"]!.Value<string>().Should().Be("Lib/Metrics.cs");

        var error = await dispatcher.ErrorAsync("get_complexity_metrics",
            new { filePath = file, metrics = new[] { "bogus" } }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be(
            "metrics must be one of: cyclomatic, nesting, loc, parameters, cognitive (got 'bogus')");
    }
}
