using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_complexity_metrics must count expression-bodied properties/indexers, which have no
// accessor list and so are matched on the declaration itself — both in the file-level
// member list/totals and when the cursor targets one directly. Their `{ get => ... }`
// sibling is already covered by the accessor case.
public class ComplexityExpressionBodiedPropertyTests
{
    private const string Code = @"
namespace Production;

public class C
{
    private int _x;
    public int Sign => _x > 0 ? 1 : _x < 0 ? -1 : 0;
}";

    [Fact]
    public async Task FileMetrics_IncludeExpressionBodiedProperty()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.GetComplexityMetricsAsync(document.FilePath!))["data"]!;

        var sign = (data["methods"] as JArray)!.SingleOrDefault(m => m["name"]!.Value<string>() == "Sign");
        sign.Should().NotBeNull("the expression-bodied property must appear in file-scope metrics");
        sign!["metrics"]!["cyclomatic"]!.Value<int>().Should().Be(3, "base 1 plus two nested ?: branches");
        data["fileTotals"]!["maxCyclomatic"]!.Value<int>().Should().Be(3,
            "the expression-bodied property's complexity must feed the file totals");
    }

    [Fact]
    public async Task CursorMetrics_OnExpressionBodiedProperty_ReportsPropertyScope()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var codeLines = Code.Split('\n');
        var signLine = Array.FindIndex(codeLines, l => l.Contains("Sign =>"));
        var signCol = codeLines[signLine].IndexOf("_x > 0", StringComparison.Ordinal);

        var data = JObject.FromObject(
            await service.GetComplexityMetricsAsync(document.FilePath!, signLine, signCol))["data"]!;

        data["scope"]!.Value<string>().Should().Be("property");
        data["name"]!.Value<string>().Should().Be("Sign");
        data["metrics"]!["cyclomatic"]!.Value<int>().Should().Be(3);
    }

    [Fact]
    public async Task FileMetrics_IncludeExpressionBodiedIndexer()
    {
        const string code = @"
namespace Production;

public class C
{
    private int[] _v = new int[4];
    public int this[int i] => i > 0 ? _v[i] : 0;
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "Indexer.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.GetComplexityMetricsAsync(document.FilePath!))["data"]!;

        var indexer = (data["methods"] as JArray)!.SingleOrDefault(m => m["name"]!.Value<string>() == "this[]");
        indexer.Should().NotBeNull("the expression-bodied indexer must appear in file-scope metrics");
        indexer!["metrics"]!["cyclomatic"]!.Value<int>().Should().Be(2, "base 1 plus one ?: branch");
        indexer["metrics"]!["parameters"]!.Value<int>().Should().Be(1,
            "the expression-bodied indexer's single parameter is counted");
    }
}
