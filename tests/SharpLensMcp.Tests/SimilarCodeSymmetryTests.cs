using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// find_similar_code fingerprints a body's CONTENT the same way for a query method, a
// snippet (with or without enclosing braces) and every candidate, so token-identical bodies
// score exactly 1; it never matches the query method with its own copy from another target
// framework, reports each candidate once, and treats expression-bodied properties as the
// candidates its schema lists.
public class SimilarCodeSymmetryTests
{
    private const string Source = """
        namespace Lib
        {
            public class Similar
            {
                public int SumPositive(int[] values)
                {
                    var total = 0;
                    foreach (var v in values)
                    {
                        if (v > 0) total += v;
                    }
                    return total;
                }

                public int SumOther(int[] items)
                {
                    var acc = 0;
                    foreach (var x in items)
                    {
                        if (x > 0) acc += x;
                    }
                    return acc;
                }

                public int TotalMethod() => System.Linq.Enumerable.Sum(new[] { 1, 2, 3, 4 });

                public int Total => System.Linq.Enumerable.Sum(new[] { 5, 6, 7, 8 });
            }
        }
        """;

    private const string BodySnippet = "var total = 0;\nforeach (var v in values)\n{\n    if (v > 0) total += v;\n}\nreturn total;";

    private static string Row(string method, string marker)
    {
        var (line, _) = RetargetedSources.Locate(Source, marker);
        return $"{method} Lib/Similar.cs:{line} 1";
    }

    private static List<string> Rows(JToken data) =>
        ((JArray)data["results"]!).Select(r => $"{r["method"]} {r["location"]!["filePath"]}:{r["location"]!["line"]} {r["score"]}").ToList();

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        builder.AddMultiTargeted("Lib", new[] { ("Similar.cs", Source) });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    [Fact]
    public async Task MethodQuery_InAMultiTargetedProject_NeverMatchesItself_AndReportsEachCandidateOnce()
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.FindSimilarCodeAsync("Similar", "SumPositive", threshold: 0.99))["data"]!;

        Rows(data).Should().Equal(Row("Similar.SumOther", "public int SumOther"));
        string.Join(",", ((JArray)data["results"]!)[0]["targetFrameworks"]!.Values<string>()).Should().Be("net10.0,net8.0");
    }

    [Theory]
    [InlineData(BodySnippet)]
    [InlineData("{\n" + BodySnippet + "\n}")]
    public async Task Snippet_ScoresLikeTheMethodBody_WithOrWithoutBraces(string snippet)
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.FindSimilarCodeAsync(snippet: snippet, threshold: 0.99))["data"]!;

        Rows(data).Should().Equal(
            Row("Similar.SumOther", "public int SumOther"),
            Row("Similar.SumPositive", "public int SumPositive"));
    }

    [Fact]
    public async Task ExpressionBodiedProperties_AreCandidates()
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.FindSimilarCodeAsync("Similar", "TotalMethod", threshold: 0.99))["data"]!;

        Rows(data).Should().Equal(Row("Similar.Total", "public int Total =>"));
    }

    [Fact]
    public async Task ViaDispatcher_SelfMatchExcluded_AndBracedSnippet()
    {
        using var builder = Build(out _, out var dispatcher);

        var byMethod = await dispatcher.DataAsync("find_similar_code", new { typeName = "Similar", methodName = "SumPositive", threshold = 0.99 });
        Rows(byMethod).Should().Equal(Row("Similar.SumOther", "public int SumOther"));

        var bySnippet = await dispatcher.DataAsync("find_similar_code", new { snippet = "{\n" + BodySnippet + "\n}", threshold = 0.99 });
        Rows(bySnippet).Should().Equal(
            Row("Similar.SumOther", "public int SumOther"),
            Row("Similar.SumPositive", "public int SumPositive"));

        var error = await dispatcher.ErrorAsync("find_similar_code", new { snippet = BodySnippet, maxResults = 0 }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("maxResults must be between 1 and 1000 (got 0)");
    }
}
