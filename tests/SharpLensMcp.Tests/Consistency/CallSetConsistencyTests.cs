using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Consistency;

// Cross-tool invariants of the one call-edge model, asked through the dispatcher:
//   - the callee set of a method is the same in get_outgoing_calls, get_call_graph
//     (callees, depth 1), find_path_between (one-hop reachability), get_exception_flow's
//     analyzed members and the coverage walk (find_tests: covering for a direct callee,
//     dispatch-only for an implementation reached through the interface);
//   - the caller set of a method is the same in find_callers and get_call_graph callers,
//     property-getter and member-initializer callers included;
//   - a call site in a multi-targeted project appears once in every call tool.
public class CallSetConsistencyTests : CrossToolConsistencyTestBase
{
    internal const string Source = """
        namespace Cons
        {
            public interface IStep { void Run(); }
            public class StepA : IStep { public void Run() { } }
            public class StepB : IStep { public void Run() { } }
            public static class Ext { public static int Twice(this int x) => x * 2; }
            public class Box
            {
                public Box() { }
                public int Size { get { return 1; } }
            }
            public partial class Host
            {
                partial void Hook();
                partial void Hook() { }
                public static T Id<T>(T x) => x;
                public void Unrelated() { }
                public int Root(IStep step)
                {
                    var b = new Box();
                    step.Run();
                    Hook();
                    return Id(1) + 3.Twice() + b.Size;
                }
            }
        }

        namespace Xunit
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class FactAttribute : System.Attribute { }
        }

        namespace Cons.Tests
        {
            public class HostTests
            {
                [Xunit.Fact] public void RootTest() { new Cons.Host().Root(new Cons.StepA()); }
            }
        }
        """;

    // Each callee of Host.Root, with the endpoint that names it and whether it has a body.
    private static readonly (string fullName, string type, string member, bool viaDispatch, bool hasBody)[] RootCallees =
    {
        ("Cons.Box.Box()", "Box", "Box", false, true),
        ("Cons.IStep.Run()", "IStep", "Run", false, false),
        ("Cons.StepA.Run()", "StepA", "Run", true, true),
        ("Cons.StepB.Run()", "StepB", "Run", true, true),
        ("Cons.Host.Hook()", "Host", "Hook", false, true),
        ("Cons.Host.Id<T>(T)", "Host", "Id", false, true),
        ("Cons.Ext.Twice(int)", "Ext", "Twice", false, true),
        ("Cons.Box.Size.get", "Box", "Size", false, true)
    };

    private static IEnumerable<string> Sorted(IEnumerable<string> names) => names.OrderBy(n => n, StringComparer.Ordinal);

    [Fact]
    public async Task CalleeSet_AgreesAcrossEveryCallTool_AndTheCoverageWalk()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Source, "Cons.cs");
        Load(workspace);
        var (line, column) = RetargetedSources.Locate(Source, "Root(IStep step)");
        var path = document.FilePath!;
        var expected = Sorted(RootCallees.Select(c => c.fullName)).ToList();

        var outgoing = await CallDataAsync("get_outgoing_calls", new { filePath = path, line, column });
        var graph = await CallDataAsync("get_call_graph",
            new { filePath = path, line, column, direction = "callees", maxDepth = 1, maxNodes = 100 });
        var flow = await CallDataAsync("get_exception_flow", new { typeName = "Host", memberName = "Root", maxDepth = 2 });

        Sorted(((JArray)outgoing["calls"]!).Select(c => c["method"]!.Value<string>()!)).Should().Equal(expected);
        Sorted(((JArray)graph["nodes"]!).Skip(1).Select(n => n["fullName"]!.Value<string>()!)).Should().Equal(expected);
        Sorted(((JArray)flow["analyzedMembers"]!).Select(m => m.Value<string>()!).Where(m => m != "Cons.Host.Root(Cons.IStep)"))
            .Should().Equal(Sorted(RootCallees.Where(c => c.hasBody).Select(c => c.fullName)));
        ((JArray)outgoing["calls"]!).ToDictionary(c => c["method"]!.Value<string>()!, c => c["viaDispatch"]!.Value<bool>())
            .Should().Equal(RootCallees.ToDictionary(c => c.fullName, c => c.viaDispatch));

        foreach (var callee in RootCallees)
        {
            var pair = ((JArray)(await CallDataAsync("find_path_between", new
            {
                sources = new[] { new { typeName = "Host", memberName = "Root" } },
                targets = new[] { new { typeName = callee.type, memberName = callee.member } },
                maxHops = 1
            }))["pairs"]!).Single();
            pair["reachable"]!.Value<bool>().Should().BeTrue($"{callee.fullName} is one hop from Root");
            pair["hopCount"]!.Value<int>().Should().Be(1);
        }
        var unrelated = ((JArray)(await CallDataAsync("find_path_between", new
        {
            sources = new[] { new { typeName = "Host", memberName = "Root" } },
            targets = new[] { new { typeName = "Host", memberName = "Unrelated" } },
            maxHops = 1
        }))["pairs"]!).Single();
        unrelated["reachable"]!.Value<bool>().Should().BeFalse();

        foreach (var callee in RootCallees.Where(c => c.hasBody))
        {
            var coverage = await CallDataAsync("find_tests", new { typeName = callee.type, memberName = callee.member });
            var covering = ((JArray)coverage["coveringTests"]!).Select(t => t["testMethod"]!.Value<string>()).ToList();
            var dispatchOnly = ((JArray)coverage["dispatchOnlyTests"]!).Select(t => t["testMethod"]!.Value<string>()).ToList();
            if (callee.viaDispatch)
            {
                covering.Should().BeEmpty($"{callee.fullName} is reached only through IStep.Run");
                dispatchOnly.Should().Equal(new[] { "RootTest" }, callee.fullName);
            }
            else
            {
                covering.Should().Equal(new[] { "RootTest" }, callee.fullName);
            }
        }
    }

    [Fact]
    public async Task CallerSet_AgreesBetweenFindCallersAndTheCallGraph_GetterAndInitializerCallersIncluded()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(CallFlowSources.Flow, CallFlowSources.FileName);
        Load(workspace);
        var (line, column) = CallFlowSources.Locate("ForCg() => 0");
        var path = document.FilePath!;

        var callers = await CallDataAsync("find_callers", new { filePath = path, line, column });
        var graph = await CallDataAsync("get_call_graph",
            new { filePath = path, line, column, direction = "callers", maxDepth = 1, maxNodes = 100 });

        var fromCallers = Sorted(((JArray)callers["callers"]!).Select(c => c["callingSymbol"]!["signature"]!.Value<string>()!)).ToList();
        var fromGraph = Sorted(((JArray)graph["nodes"]!).Skip(1).Select(n => n["fullName"]!.Value<string>()!)).ToList();

        fromCallers.Should().Equal("Flow.CgHost.CgHost()", "Flow.CgHost.Direct()", "Flow.CgHost.L()", "Flow.CgHost.P.get");
        fromGraph.Should().Equal(fromCallers);
    }

    [Fact]
    public async Task MultiTargetedCallSite_AppearsOnceInEveryCallTool()
    {
        using var fixture = new MultiTargetSolutionFixture();
        Load(fixture.Workspace);
        var path = fixture.MultiFilePath(MultiTargetSources.MultiFileName);
        var (formatLine, formatColumn) = RetargetedSources.Locate(MultiTargetSources.Multi, "Format(string name) =>");
        var (greetLine, greetColumn) = RetargetedSources.Locate(MultiTargetSources.Multi, "Greet(string name)");

        var callers = await CallDataAsync("find_callers", new { filePath = path, line = formatLine, column = formatColumn });
        var graph = await CallDataAsync("get_call_graph",
            new { filePath = path, line = formatLine, column = formatColumn, direction = "callers", maxDepth = 1, maxNodes = 100 });
        var outgoing = await CallDataAsync("get_outgoing_calls", new { filePath = path, line = greetLine, column = greetColumn });

        ((JArray)callers["callers"]!).Select(c => c["callingSymbol"]!["signature"]!.Value<string>()).Should().Equal(
            new[] { "Multi.Greeter.Greet(string)", "Multi.Greeter.Modern()" },
            "Greet's call site is compiled by both target frameworks and listed once; Modern exists in net10.0 only");
        ((JArray)graph["nodes"]!).Select(n => n["fullName"]!.Value<string>()).Should().Equal(
            "Multi.Greeter.Format(string)", "Multi.Greeter.Greet(string)", "Multi.Greeter.Modern()");
        ((JArray)outgoing["calls"]!).Select(c => c["method"]!.Value<string>()).Should().Equal(
            new[] { "Multi.Greeter.Format(string)" }, "the one call Greet makes, once");
    }
}
