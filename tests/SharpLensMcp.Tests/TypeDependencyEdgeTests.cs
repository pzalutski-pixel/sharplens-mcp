using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The type-dependency graph check_architecture and find_god_objects share, each behavior
// at both levels (ToolPath): every type-to-type edge the bound code makes (enum values,
// static members, attributes, catch types, type tests, foreach and out-var locals, lambda
// parameters, generic method arguments, extension hosts), no edge from compiler-generated
// members or to void, a nested type's body charged to the nested type alone, member
// counts over the members a reader sees, afferent coupling from the whole solution, and
// a multi-targeted project's types reported once.
public class TypeDependencyEdgeTests
{
    internal const string Source = """
        namespace Audit.Infra
        {
            public enum Status { A, B }
            public static class Config { public static int Value = 1; }
            public class InfraException : System.Exception { }
            public class InfraAttribute : System.Attribute { }
            public class Thing { }
            public static class Util { public static T Make<T>() where T : new() => new T(); }
            public static class ThingExt { public static int Weight(this object o) => 1; }
        }

        namespace Audit.Domain
        {
            public class D1 { public bool M(object s) => s.Equals(Audit.Infra.Status.A); }
            public class D2 { public int N() => Audit.Infra.Config.Value; }
            [Audit.Infra.Infra] public class D3 { }
            public class D4 { public void P() { try { } catch (Audit.Infra.InfraException) { } } }
            public class D5 { public bool Q(object o) => o is Audit.Infra.Thing; }
            public class D6 { public void R(System.Collections.IEnumerable xs) { foreach (Audit.Infra.Thing t in xs) { } } }
            public class D7 { public object S() => Audit.Infra.Util.Make<Audit.Infra.Thing>(); }
            public class D8 { public void Run(System.Delegate d) { } public void U() => Run((Audit.Infra.Thing t) => 0); }
            public class D9 { public bool V(Holder h) => h.TryGet(out var t); }
            public class Holder { public bool TryGet(out Audit.Infra.Thing t) { t = null!; return false; } }
        }

        namespace Audit.Domain
        {
            using Audit.Infra;

            public class D10 { public int W(object o) => o.Weight(); }
        }

        namespace Audit.Empty
        {
            public class Nothing { }
            public record Rec(int X);
        }

        namespace Audit.Nest
        {
            public class Outer
            {
                public class Inner { public object Make() => new Audit.Infra.Thing(); }
            }
        }

        namespace Audit.Counting
        {
            public class Counted
            {
                private int _f;
                public Counted() { }
                public int P { get; set; }
                public event System.EventHandler E;
                public void M() { }
                public class Nested { }
            }
        }
        """;

    private static Task<JToken> ArchitectureAsync(ToolPathRunner runner, string from, string deny) =>
        runner.DataAsync("check_architecture", new { rules = new[] { new { from, deny } } },
            s => s.CheckArchitectureAsync(new List<ArchitectureRule> { new() { From = from, Deny = deny } }));

    private static Task<JToken> GodObjectsAsync(ToolPathRunner runner, string? projectName) =>
        runner.DataAsync("find_god_objects",
            new { projectName, minEfferentCoupling = 0, minMembers = 0, maxResults = 100 },
            s => s.FindGodObjectsAsync(projectName, 0, 0, 100));

    private static List<(string from, string to)> Violations(JToken data) =>
        ((JArray)data["violations"]!)
            .Select(v => (v["fromType"]!.Value<string>()!, v["toType"]!.Value<string>()!))
            .ToList();

    private static JToken Candidate(JToken data, string typeName) =>
        ((JArray)data["candidates"]!).Single(c => c["typeName"]!.Value<string>() == typeName);

    private static ToolPathRunner Load(ToolPath toolPath)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Source, "Audit.cs");
        return new ToolPathRunner(toolPath, workspace);
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task Architecture_EveryBoundTypeToTypeEdge_IsChecked(ToolPath toolPath)
    {
        var data = await ArchitectureAsync(Load(toolPath), "Audit.Domain", "Audit.Infra");

        Violations(data).Should().Equal(
            ("Audit.Domain.D1", "Audit.Infra.Status"),
            ("Audit.Domain.D10", "Audit.Infra.ThingExt"),
            ("Audit.Domain.D2", "Audit.Infra.Config"),
            ("Audit.Domain.D3", "Audit.Infra.InfraAttribute"),
            ("Audit.Domain.D4", "Audit.Infra.InfraException"),
            ("Audit.Domain.D5", "Audit.Infra.Thing"),
            ("Audit.Domain.D6", "Audit.Infra.Thing"),
            ("Audit.Domain.D7", "Audit.Infra.Thing"),
            ("Audit.Domain.D7", "Audit.Infra.Util"),
            ("Audit.Domain.D8", "Audit.Infra.Thing"),
            ("Audit.Domain.D9", "Audit.Infra.Thing"),
            ("Audit.Domain.Holder", "Audit.Infra.Thing"));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task Architecture_CompilerGeneratedMembersAndVoid_AreNotEdges(ToolPath toolPath)
    {
        var data = await ArchitectureAsync(Load(toolPath), "Audit.Empty", "System*");

        Violations(data).Should().Equal(new[] { ("Audit.Empty.Rec", "int") },
            "Rec's declared parameter X is an int; its synthesized members, implicit constructors and void are not edges");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task Architecture_NestedTypeBody_IsChargedToTheNestedTypeOnly(ToolPath toolPath)
    {
        var data = await ArchitectureAsync(Load(toolPath), "Audit.Nest", "Audit.Infra");

        Violations(data).Should().Equal(("Audit.Nest.Outer.Inner", "Audit.Infra.Thing"));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task GodObjects_CountsUserVisibleMembers_GenericArgumentEdges_AndNestedTypesApart(ToolPath toolPath)
    {
        var data = await GodObjectsAsync(Load(toolPath), null);

        var counted = Candidate(data, "Audit.Counting.Counted");
        counted["memberCount"]!.Value<int>().Should().Be(5,
            "_f, the constructor, P, E and M - not P's accessors, not the nested type");
        Candidate(data, "Audit.Domain.D7")["efferentCoupling"]!.Value<int>().Should().Be(2,
            "Make<Thing>() depends on Util and on its generic argument Thing");
        Candidate(data, "Audit.Nest.Outer")["efferentCoupling"]!.Value<int>().Should().Be(0,
            "the nested Inner's body is Inner's dependency, not Outer's");
        Candidate(data, "Audit.Nest.Outer.Inner")["efferentCoupling"]!.Value<int>().Should().Be(1);
    }

    private const string LibSource = """
        namespace Lib
        {
            public class Metrics { public int Count; }
        }
        """;

    private const string AppSource = """
        namespace Lib.Tests
        {
            public class App { public Lib.Metrics Metrics; }
        }
        """;

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task GodObjects_ScopedToAProject_CountsAfferentCouplingFromTheWholeSolution(ToolPath toolPath)
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, AppSource, retarget: false);
        var runner = new ToolPathRunner(toolPath, fixture.Workspace);

        var data = await GodObjectsAsync(runner, RetargetedSolutionFixture.LibProjectName);

        ((JArray)data["candidates"]!).Select(c => c["typeName"]!.Value<string>()).Should().Equal(
            new[] { "Lib.Metrics" }, "only the scoped project's types are reported");
        Candidate(data, "Lib.Metrics")["afferentCoupling"]!.Value<int>().Should().Be(1,
            "Lib.Tests.App depends on Lib.Metrics, though App is outside the scope");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task GodObjects_MultiTargetedProject_ReportsEachTypeOnce_UnderItsLogicalName(ToolPath toolPath)
    {
        using var fixture = new MultiTargetSolutionFixture();
        var runner = new ToolPathRunner(toolPath, fixture.Workspace);

        var scoped = await GodObjectsAsync(runner, MultiTargetSolutionFixture.MultiName);
        var all = await GodObjectsAsync(runner, null);

        ((JArray)scoped["candidates"]!).Select(c => c["typeName"]!.Value<string>()).Should().Equal("Multi.Greeter");
        ((JArray)all["candidates"]!).Select(c => c["typeName"]!.Value<string>()).Should()
            .BeEquivalentTo("Multi.Greeter", "Consumer.App");
        Candidate(all, "Multi.Greeter")["afferentCoupling"]!.Value<int>().Should().Be(1, "Consumer.App");
    }
}
