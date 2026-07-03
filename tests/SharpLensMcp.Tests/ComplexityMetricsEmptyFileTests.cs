using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks get_complexity_metrics on files with no methods (interfaces, enums,
// DTOs): the file-level maxCyclomatic aggregation previously called Max() on
// an empty sequence and surfaced as an internal error.
public class ComplexityMetricsEmptyFileTests
{
    [Fact]
    public async Task FileWithNoMethods_ReturnsZeroTotalsInsteadOfCrashing()
    {
        const string code = @"
namespace Production;

public interface IShape
{
    int Sides { get; }
}

public enum Color
{
    Red,
    Green
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "Shapes.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GetComplexityMetricsAsync(document.FilePath!);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(
            "a file without methods is a normal input, not an error: " + json);

        var data = json["data"]!;
        data["methodCount"]!.Value<int>().Should().Be(0);
        data["fileTotals"]!["maxCyclomatic"]!.Value<int>().Should().Be(0,
            "an empty method set has no complexity — previously this path threw on Max()");
        data["fileTotals"]!["avgCyclomatic"]!.Value<double>().Should().Be(0);
        data["fileTotals"]!["maxNesting"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task FileMetrics_IncludeConstructorsAndAccessors()
    {
        // File-scope metrics previously enumerated only MethodDeclarationSyntax, omitting
        // constructors, operators, and property accessors from the per-member list and
        // totals. They must now appear (bodied members only).
        const string code = @"
namespace Production;

public class Widget
{
    private int _v;
    public Widget(int v) { if (v > 0) _v = v; }
    public int Value { get { if (_v < 0) return 0; return _v; } }
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "Widget.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GetComplexityMetricsAsync(document.FilePath!);
        var data = JObject.FromObject(result)["data"]!;
        var names = (data["methods"] as JArray)!.Select(m => m["name"]!.Value<string>()).ToList();

        names.Should().Contain(".ctor", "the constructor must appear in file-scope metrics");
        names.Should().Contain("Value.get", "the property getter must appear in file-scope metrics");

        // Cursor scope on the same getter must name it identically to file scope.
        var codeLines = code.Split('\n');
        var getterLine = Array.FindIndex(codeLines, l => l.Contains("get {"));
        var getterCol = codeLines[getterLine].IndexOf("return _v", StringComparison.Ordinal);
        var cursor = JObject.FromObject(
            await service.GetComplexityMetricsAsync(document.FilePath!, getterLine, getterCol))["data"]!;
        cursor["scope"]!.Value<string>().Should().Be("accessor");
        cursor["name"]!.Value<string>().Should().Be("Value.get",
            "cursor and file scope must name the same accessor identically");
    }

    [Fact]
    public async Task FileTotals_AggregateEveryRequestedMetric_IncludingParametersAndCognitive()
    {
        // A(int,int,int) has 3 parameters and cognitive 0; B(int) has 1 parameter and
        // cognitive 1 (a single if). The file totals must carry parameters and cognitive
        // aggregates, not just cyclomatic/nesting/loc.
        const string code = @"
namespace Production;

public class T
{
    public int A(int x, int y, int z) => x + y + z;
    public int B(int x) { if (x > 0) return 1; return 0; }
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "T.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var totals = JObject.FromObject(await service.GetComplexityMetricsAsync(document.FilePath!))["data"]!["fileTotals"]!;

        totals["maxParameters"]!.Value<int>().Should().Be(3, "A(int,int,int) has the most parameters");
        totals["maxCognitive"]!.Value<int>().Should().Be(1, "B's single if is cognitive 1");
        totals["avgCognitive"]!.Value<double>().Should().Be(0.5, "cognitive 0 and 1 over two methods");
    }

    [Fact]
    public async Task FileTotals_MatchMetricNamesCaseInsensitively_AndParametersAreSymmetric()
    {
        // Per-method matching lowercases the metric name; file totals must too, or a
        // capitalized "Cyclomatic"/"Parameters" silently produces no file total. And
        // parameters carries avg + max like the other count metrics.
        const string code = @"
namespace Production;

public class T
{
    public int A(int x, int y, int z) => x + y + z;
    public int B(int x) { if (x > 0) return 1; return 0; }
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "T.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var totals = JObject.FromObject(await service.GetComplexityMetricsAsync(
            document.FilePath!, metrics: new List<string> { "Cyclomatic", "Parameters" }))["data"]!["fileTotals"]!;

        totals["maxCyclomatic"]!.Value<int>().Should().Be(2,
            "the capitalized metric name must still produce file totals (B's if = cyclomatic 2)");
        totals["maxParameters"]!.Value<int>().Should().Be(3);
        totals["avgParameters"]!.Value<double>().Should().Be(2.0, "(3 + 1) / 2 parameters");
    }
}
