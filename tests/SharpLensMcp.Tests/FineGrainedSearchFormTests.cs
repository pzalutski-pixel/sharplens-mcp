using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_pattern_usages, find_catch_blocks, find_throw_sites and find_type_instantiations on
// the forms the bound pattern and catch trees take, each at both levels (ToolPath):
// `case T:` labels, patterns under not/and/or/parentheses and list patterns, a bare
// `catch { }` (it catches every exception), the shadow notice of a source type that wins
// over a framework type, and a multi-targeted project's sites reported once, in
// canonical location order.
public class FineGrainedSearchFormTests
{
    internal const string Source = """
        namespace Pat
        {
            public class Order { public int Total { get; set; } public Order Inner { get; set; } }

            public class TimeoutException : System.Exception { }

            public class PatternUser
            {
                public bool A(object o) { switch (o) { case Order: return true; } return false; }
                public bool B(Order o) => o is not { Total: > 5 };
                public bool C(Order o) => o is { Total: > 5 } and { Total: < 9 };
                public bool D(object[] items) => items is [Order first, ..];
                public bool E(object o) => o is (Order) or null;
                public bool F(Order o) => o is { Inner: { Total: 3 } };
            }

            public class Thrower
            {
                public void T1() { throw new TimeoutException(); }
                public void T2() { throw new System.TimeoutException(); }
            }

            public class Catcher
            {
                public void Bare() { try { } catch { } }
                public void Typed() { try { } catch (System.Exception) { } }
                public void Exact() { try { } catch (System.InvalidOperationException) { } }
                public void Other() { try { } catch (System.ArgumentException) { } }
            }
        }
        """;

    private static ToolPathRunner Load(ToolPath toolPath)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Source, "Pat.cs");
        return new ToolPathRunner(toolPath, workspace);
    }

    private static (int line, int column) At(string marker, int offset = 0)
    {
        var (line, column) = RetargetedSources.Locate(Source, marker);
        return (line, column + offset);
    }

    private static List<(int line, int column, string kind)> Matches(JToken data) =>
        ((JArray)data["matches"]!)
            .Select(m => (m["location"]!["line"]!.Value<int>(), m["location"]!["column"]!.Value<int>(), m["kind"]!.Value<string>()!))
            .ToList();

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PatternUsages_CaseLabelsNestedCombinedAndListPatterns_AreAllFound(ToolPath toolPath)
    {
        var runner = Load(toolPath);

        var data = await runner.DataAsync("find_pattern_usages", new { typeName = "Pat.Order" },
            s => s.FindPatternUsagesAsync("Pat.Order"));

        Matches(data).Should().Equal(
            (At("case Order:", 5).line, At("case Order:", 5).column, "typePattern"),
            (At("{ Total: > 5 };").line, At("{ Total: > 5 };").column, "recursivePattern"),
            (At("{ Total: > 5 } and").line, At("{ Total: > 5 } and").column, "recursivePattern"),
            (At("{ Total: < 9 }").line, At("{ Total: < 9 }").column, "recursivePattern"),
            (At("Order first").line, At("Order first").column, "declarationPattern"),
            (At("(Order) or").line, At("(Order) or").column, "typePattern"),
            (At("{ Inner: {").line, At("{ Inner: {").column, "recursivePattern"),
            (At("{ Total: 3 }").line, At("{ Total: 3 }").column, "recursivePattern"));
        data["notice"]!.Type.Should().Be(JTokenType.Null, "Pat.Order is fully qualified and shadows nothing");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task CatchBlocks_BareCatchCatchesEveryException(ToolPath toolPath)
    {
        var runner = Load(toolPath);

        var withBases = await runner.DataAsync("find_catch_blocks",
            new { typeName = "System.InvalidOperationException" },
            s => s.FindCatchBlocksAsync("System.InvalidOperationException"));
        var exactOnly = await runner.DataAsync("find_catch_blocks",
            new { typeName = "System.InvalidOperationException", includeDerived = false },
            s => s.FindCatchBlocksAsync("System.InvalidOperationException", includeDerived: false));

        Matches(withBases).Should().Equal(
            (At("catch { }").line, At("catch { }").column, "catchAll"),
            (At("catch (System.Exception)").line, At("catch (System.Exception)").column, "catch"),
            (At("catch (System.InvalidOperationException)").line, At("catch (System.InvalidOperationException)").column, "catch"));
        Matches(exactOnly).Should().Equal(
            (At("catch (System.InvalidOperationException)").line, At("catch (System.InvalidOperationException)").column, "catch"));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task ThrowSites_ShadowingSourceType_MatchesOnlyItAndCarriesTheNotice(ToolPath toolPath)
    {
        var runner = Load(toolPath);

        var data = await runner.DataAsync("find_throw_sites", new { typeName = "TimeoutException" },
            s => s.FindThrowSitesAsync("TimeoutException"));

        data["typeName"]!.Value<string>().Should().Be("Pat.TimeoutException");
        Matches(data).Should().Equal(
            (At("throw new TimeoutException()").line, At("throw new TimeoutException()").column, "throw"));
        data["notice"]!.Value<string>().Should().Be(
            "'TimeoutException' resolved to Pat.TimeoutException; also matches System.TimeoutException — pass the fully-qualified name to target it.");
    }

    private const string MultiSource = """
        namespace Multi
        {
            public class Greeter
            {
                public static Greeter Make() => new Greeter();
                public bool Is(object o) => o is Greeter;
            }
        }
        """;

    private const string ConsumerSource = """
        namespace Consumer
        {
            public class App
            {
                public object Run() => new Multi.Greeter();
            }
        }
        """;

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task MultiTargetedProject_EachSiteOnce_InCanonicalLocationOrder(ToolPath toolPath)
    {
        using var fixture = new MultiTargetSolutionFixture(MultiSource, ConsumerSource);
        var runner = new ToolPathRunner(toolPath, fixture.Workspace);

        var instantiations = await runner.DataAsync("find_type_instantiations", new { typeName = "Multi.Greeter" },
            s => s.FindTypeInstantiationsAsync("Multi.Greeter"));
        var patterns = await runner.DataAsync("find_pattern_usages", new { typeName = "Multi.Greeter" },
            s => s.FindPatternUsagesAsync("Multi.Greeter"));

        ((JArray)instantiations["matches"]!).Select(m => m["location"]!["filePath"]!.Value<string>()).Should().Equal(
            fixture.ConsumerFilePath(MultiTargetSources.ConsumerFileName).Replace('\\', '/'),
            fixture.MultiFilePath(MultiTargetSources.MultiFileName).Replace('\\', '/'));
        ((JArray)patterns["matches"]!).Select(m => m["kind"]!.Value<string>()).Should().Equal("isExpression");
    }
}
