using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// change_signature on Roslyn's symbol machinery: a renamed parameter follows every
// reference (body, nameof, <param>, named arguments) through the Renamer; untouched
// parameters keep their original syntax (attributes, modifiers, type spelling, default
// literal text, layout); call sites are found by SymbolFinder and rewritten per
// invocation from the bound argument-to-parameter mapping (a skipped optional parameter
// makes later arguments named; an empty params list stays empty); a method-group
// reference is never mistaken for a call; overrides, interface implementations and
// the members they implement change together; a recursive call inside the edited
// declaration is rewritten; and every change is validated before anything is computed.
public class ChangeSignatureRebuildTests
{
    internal static Task<object> Change(RoslynService service, string typeName, string memberName,
        bool preview, params SignatureChange[] changes) =>
        service.ChangeSignatureAsync(null, null, null, changes.ToList(), preview, typeName, memberName);

    internal static SignatureChange Add(string name, string type, string? defaultValue = null, int? position = null) =>
        new() { Action = "add", Name = name, Type = type, DefaultValue = defaultValue, Position = position };

    internal static SignatureChange Rename(string name, string newName) =>
        new() { Action = "rename", Name = name, NewName = newName };

    internal static SignatureChange Remove(string name) => new() { Action = "remove", Name = name };

    internal static SignatureChange Reorder(params string[] order) => new() { Action = "reorder", Order = order.ToList() };

    internal const string RenameSource = """
        namespace Demo
        {
            public static class Calc
            {
                /// <summary>Doubles.</summary>
                /// <param name="x">The value.</param>
                public static int Twice(int x)
                {
                    System.Console.WriteLine(nameof(x));
                    return x * 2;
                }

                public static int Use() => Twice(x: 3) + Twice(4);
            }
        }
        """;

    internal static readonly string RenamedSource = Edited(RenameSource,
        ("<param name=\"x\">", "<param name=\"value\">"),
        ("Twice(int x)", "Twice(int value)"),
        ("nameof(x)", "nameof(value)"),
        ("return x * 2;", "return value * 2;"),
        ("Twice(x: 3)", "Twice(value: 3)"));

    [Fact]
    public async Task RenameParameter_FollowsBodyNameofParamTagAndNamedArguments()
    {
        var (service, workspace, document) = Load(RenameSource);

        var json = Json(await Change(service, "Calc", "Twice", preview: false, Rename("x", "value")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(RenamedSource);
        json["data"]!["newSignature"]!.Value<string>().Should().Be("int Twice(int value)");
        json["data"]!["callSitesUpdated"]!.Value<int>().Should().Be(1,
            "only Twice(x: 3) names the parameter; Twice(4) is untouched");
        json["data"]!["postApplyErrors"]!.Type.Should().Be(JTokenType.Null);
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string PreserveSource = """
        using System.Runtime.CompilerServices;

        namespace Demo
        {
            public class Other { public class Nested { } public enum Mode { A, B } }

            public static class Sig
            {
                public static string N(Other.Nested n, [CallerMemberName] string caller = "") => caller;
                public static float F(int a, float scale = 1.5f) => a * scale;
                public static decimal D(decimal m = 2.5m, double d = double.NaN, Other.Mode mode = Other.Mode.B) => m;
                public static int Multi(
                    int first,
                    int second)
                {
                    return first - second;
                }

                public static string UseN() => N(new Other.Nested());
                public static float UseF() => F(2);
                public static decimal UseD() => D(1m, 2.0, Other.Mode.A);
                public static int UseMulti() => Multi(7, 3);
            }
        }
        """;

    [Fact]
    public async Task AddAfterCallerInfoParameter_KeepsAttributeAndNestedTypeSyntax_AndNamesTheNewArgument()
    {
        var (service, workspace, document) = Load(PreserveSource);

        var json = Json(await Change(service, "Sig", "N", preview: false, Add("extra", "int", "0")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(PreserveSource,
            ("N(Other.Nested n, [CallerMemberName] string caller = \"\")",
                "N(Other.Nested n, [CallerMemberName] string caller = \"\", int extra = 0)"),
            ("N(new Other.Nested());", "N(new Other.Nested(), extra: 0);")));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task RenameParameter_KeepsTheFloatSuffixOfAnUntouchedDefault()
    {
        var (service, workspace, document) = Load(PreserveSource);

        var json = Json(await Change(service, "Sig", "F", preview: false, Rename("a", "b")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(PreserveSource,
            ("F(int a, float scale = 1.5f) => a * scale;", "F(int b, float scale = 1.5f) => b * scale;")));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reorder_KeepsDecimalNaNAndNestedEnumDefaultsAsWritten()
    {
        var (service, workspace, document) = Load(PreserveSource);

        var json = Json(await Change(service, "Sig", "D", preview: false, Reorder("mode", "d", "m")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(PreserveSource,
            ("D(decimal m = 2.5m, double d = double.NaN, Other.Mode mode = Other.Mode.B)",
                "D(Other.Mode mode = Other.Mode.B, double d = double.NaN, decimal m = 2.5m)"),
            ("D(1m, 2.0, Other.Mode.A);", "D(Other.Mode.A, 2.0, 1m);")));
        json["data"]!["oldSignature"]!.Value<string>().Should().Be(
            "decimal D(decimal m = 2.5m, double d = double.NaN, Other.Mode mode = Other.Mode.B)");
        json["data"]!["newSignature"]!.Value<string>().Should().Be(
            "decimal D(Other.Mode mode = Other.Mode.B, double d = double.NaN, decimal m = 2.5m)");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reorder_KeepsAMultiLineParameterListAndTheBodyBraceOnItsOwnLine()
    {
        var (service, workspace, document) = Load(PreserveSource);

        var json = Json(await Change(service, "Sig", "Multi", preview: false, Reorder("second", "first")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(PreserveSource,
            ("(\n            int first,\n            int second)\n        {", "(\n            int second,\n            int first)\n        {"),
            ("Multi(7, 3);", "Multi(3, 7);")));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string GroupSource = """
        using System;
        using System.Threading;

        namespace Demo
        {
            public static class Groups
            {
                public static int Square(int x) => x * x;
                public static int Apply(Func<int, int> f) => f(3);
                public static int UseSquare() => Apply(Square);
                public static int Lambda() => Apply(v => { Func<int, int> g = Square; return g(v); });
                public static int Direct() => Square(5);
                public static void Work() { }
                public static void Run() { var t = new Thread(Work); t.Start(); }
            }
        }
        """;

    [Fact]
    public async Task MethodGroupReferences_AreNeverRewrittenAsCalls_AndAreReported()
    {
        var (service, workspace, document) = Load(GroupSource);

        var json = Json(await Change(service, "Groups", "Square", preview: false, Add("y", "int", "0")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(GroupSource,
            ("Square(int x) => x * x;", "Square(int x, int y = 0) => x * x;"),
            ("Square(5);", "Square(5, 0);")),
            "Apply(Square) and `g = Square` are method-group conversions, not calls of Square");
        json["data"]!["callSitesUpdated"]!.Value<int>().Should().Be(1);
        var path = Reported(document.FilePath!);
        ((JArray)json["data"]!["unrewrittenReferences"]!).Select(r =>
                $"{r["filePath"]!.Value<string>()}:{r["line"]!.Value<int>()}:{r["column"]!.Value<int>()} {r["reason"]!.Value<string>()}")
            .Should().Equal(
                $"{path}:9:47 method group: a delegate conversion of the method, which no argument edit can update",
                $"{path}:10:70 method group: a delegate conversion of the method, which no argument edit can update");
        ((JArray)json["data"]!["postApplyErrors"]!).Select(e => e.Value<string>()).Should().Equal(
            $"{path} (line 9) CS1503: Argument 1: cannot convert from 'method group' to 'System.Func<int, int>'",
            $"{path} (line 10) CS0123: No overload for 'Square' matches delegate 'Func<int, int>'");
    }

    [Fact]
    public async Task MethodGroupPassedToAConstructor_LeavesTheConstructorCallAlone()
    {
        var (service, workspace, document) = Load(GroupSource);

        var json = Json(await Change(service, "Groups", "Work", preview: true, Add("n", "int", "0")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["callSitesCount"]!.Value<int>().Should().Be(0,
            "new Thread(Work) passes Work as a delegate; it is not a call of Work");
        ((JArray)json["data"]!["transformedCallSites"]!).Should().BeEmpty();
        ((JArray)json["data"]!["unrewrittenReferences"]!).Select(r => $"{r["line"]}:{r["column"]}")
            .Should().Equal("13:54");
        (await TextOf(workspace, document.Id)).Should().Be(GroupSource, "a preview writes nothing");
    }

    internal const string OptionalSource = """
        namespace Demo
        {
            public static class Opts
            {
                public static int Opt(int a, int b = 0) => a + b;
                public static int UseOpt() => Opt(1);
                public static int UseBoth() => Opt(1, 2);
                public static int UseNamed() => Opt(b: 4, a: 3);
            }
        }
        """;

    internal static readonly string OptionalWithC = Edited(OptionalSource,
        ("Opt(int a, int b = 0)", "Opt(int a, int b = 0, int c = 7)"),
        ("Opt(1);", "Opt(1, c: 7);"),
        ("Opt(1, 2);", "Opt(1, 2, 7);"),
        ("Opt(b: 4, a: 3);", "Opt(b: 4, a: 3, c: 7);"));

    [Fact]
    public async Task AddedArgumentAfterASkippedOptionalParameter_IsNamed()
    {
        var (service, workspace, document) = Load(OptionalSource);

        var json = Json(await Change(service, "Opts", "Opt", preview: false, Add("c", "int", "7")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(OptionalWithC,
            "a positional 7 after Opt(1) would bind to b, not c");
        json["data"]!["callSitesUpdated"]!.Value<int>().Should().Be(3);
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string ParamsSource = """
        namespace Demo
        {
            public static class Packs
            {
                public static int P(int a, params int[] rest) => a + rest.Length;
                public static int UseNone() => P(1);
                public static int UseMany() => P(1, 2, 3);
            }
        }
        """;

    [Fact]
    public async Task ParamsCallWithoutSurplusArguments_GetsNoArgumentForTheParamsSlot()
    {
        var (service, workspace, document) = Load(ParamsSource);

        var json = Json(await Change(service, "Packs", "P", preview: false, Add("k", "int", "0", position: 1)));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(ParamsSource,
            ("P(int a, params int[] rest)", "P(int a, int k = 0, params int[] rest)"),
            ("P(1);", "P(1, 0);"),
            ("P(1, 2, 3);", "P(1, 0, 2, 3);")));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task RenameOnAParamsMethod_LeavesEveryCallSiteUntouched()
    {
        var (service, workspace, document) = Load(ParamsSource);

        var json = Json(await Change(service, "Packs", "P", preview: false, Rename("a", "seed")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(ParamsSource,
            ("P(int a, params int[] rest) => a + rest.Length;", "P(int seed, params int[] rest) => seed + rest.Length;")));
        json["data"]!["callSitesUpdated"]!.Value<int>().Should().Be(0);
    }

    internal const string HierarchySource = """
        namespace Demo
        {
            public abstract class Base { public abstract int V(int a, int b); }
            public class Derived : Base { public override int V(int a, int b) => a - b; }
            public static class UseV { public static int Go(Base x, Derived d) => x.V(1, 2) + d.V(3, 4); }

            public interface IWork { int Do(int a, int b); }
            public class Worker : IWork { public int Do(int a, int b) => a; }
            public class Explicit : IWork { int IWork.Do(int a, int b) => a * 2; }
            public static class UseWork { public static int Go(IWork w, Worker k) => w.Do(1, 2) + k.Do(3, 4); }
        }
        """;

    [Fact]
    public async Task ReorderOnAnAbstractMember_ReordersEveryOverrideAndCallSite()
    {
        var (service, workspace, document) = Load(HierarchySource);

        var json = Json(await Change(service, "Base", "V", preview: false, Reorder("b", "a")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(HierarchySource,
            ("public abstract int V(int a, int b);", "public abstract int V(int b, int a);"),
            ("public override int V(int a, int b) => a - b;", "public override int V(int b, int a) => a - b;"),
            ("x.V(1, 2) + d.V(3, 4)", "x.V(2, 1) + d.V(4, 3)")));
        ((JArray)json["data"]!["updatedDeclarations"]!).Select(d => d.Value<string>())
            .Should().Equal("Demo.Base.V(int, int)", "Demo.Derived.V(int, int)");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal static readonly string HierarchyWithoutB = Edited(HierarchySource,
        ("int Do(int a, int b);", "int Do(int a);"),
        ("public int Do(int a, int b) => a;", "public int Do(int a) => a;"),
        ("int IWork.Do(int a, int b) => a * 2;", "int IWork.Do(int a) => a * 2;"),
        ("w.Do(1, 2) + k.Do(3, 4)", "w.Do(1) + k.Do(3)"));

    [Fact]
    public async Task RemoveOnAnImplementation_CascadesToTheInterfaceAndEveryImplementation()
    {
        var (service, workspace, document) = Load(HierarchySource);

        var json = Json(await Change(service, "Worker", "Do", preview: false, Remove("b")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(HierarchyWithoutB);
        ((JArray)json["data"]!["updatedDeclarations"]!).Select(d => d.Value<string>())
            .Should().Equal("Demo.Worker.Do(int, int)", "Demo.IWork.Do(int, int)", "Demo.Explicit.Demo.IWork.Do(int, int)");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string RecursiveSource = """
        namespace Demo
        {
            public static class Rec
            {
                public static int Fact(int n) { return n <= 1 ? 1 : n * Fact(n - 1); }
                public static int Use() => Fact(5);
            }
        }
        """;

    [Fact]
    public async Task AddOnARecursiveMethod_RewritesTheCallInsideItsOwnBody()
    {
        var (service, workspace, document) = Load(RecursiveSource);

        var json = Json(await Change(service, "Rec", "Fact", preview: false, Add("depth", "int", "0")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(RecursiveSource,
            ("Fact(int n) { return n <= 1 ? 1 : n * Fact(n - 1); }", "Fact(int n, int depth = 0) { return n <= 1 ? 1 : n * Fact(n - 1, 0); }"),
            ("Fact(5);", "Fact(5, 0);")));
        json["data"]!["callSitesUpdated"]!.Value<int>().Should().Be(2);
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task RenameOnARecursiveMethod_RenamesEveryUseInTheBody()
    {
        var (service, workspace, document) = Load(RecursiveSource);

        var json = Json(await Change(service, "Rec", "Fact", preview: false, Rename("n", "m")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(RecursiveSource,
            ("Fact(int n) { return n <= 1 ? 1 : n * Fact(n - 1); }", "Fact(int m) { return m <= 1 ? 1 : m * Fact(m - 1); }")));
    }

    internal const string CrefSource = """
        namespace Demo
        {
            public static class Docs
            {
                /// <summary>See <see cref="Pair(int, string)"/>.</summary>
                public static int Pair(int a, string b) => a;
                public static int Use() => Pair(Pair(1, "x"), "y");
            }
        }
        """;

    [Fact]
    public async Task Reorder_RewritesACrefParameterListAndANestedCall()
    {
        var (service, workspace, document) = Load(CrefSource);

        var json = Json(await Change(service, "Docs", "Pair", preview: false, Reorder("b", "a")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(CrefSource,
            ("cref=\"Pair(int, string)\"", "cref=\"Pair(string, int)\""),
            ("Pair(int a, string b)", "Pair(string b, int a)"),
            ("Pair(Pair(1, \"x\"), \"y\")", "Pair(\"y\", Pair(\"x\", 1))")));
        json["data"]!["callSitesUpdated"]!.Value<int>().Should().Be(2);
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string AddEndSource = """
        namespace Demo
        {
            public static class Ends
            {
                public static int AddEnd(int a) => a;
                public static int Use() => AddEnd(1);
            }
        }
        """;

    [Fact]
    public async Task PositionMinusOne_AppendsAtTheEnd()
    {
        var (service, workspace, document) = Load(AddEndSource);

        var json = Json(await Change(service, "Ends", "AddEnd", preview: false, Add("z", "int", "0", position: -1)));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(AddEndSource,
            ("AddEnd(int a)", "AddEnd(int a, int z = 0)"),
            ("AddEnd(1);", "AddEnd(1, 0);")));
    }

    public static IEnumerable<object[]> InvalidChanges => new[]
    {
        new object[] { Array.Empty<SignatureChange>(), "changes must list at least one change" },
        new object[] { new[] { new SignatureChange { Action = "frobnicate", Name = "a" } },
            "changes[0].action must be one of: add, remove, rename, reorder (got 'frobnicate')" },
        new object[] { new[] { Remove("doesNotExist") },
            "changes[0]: 'doesNotExist' is not a parameter of Demo.Ends.AddEnd(int)" },
        new object[] { new[] { Rename("a", "q"), Remove("ghost") },
            "changes[1]: 'ghost' is not a parameter of Demo.Ends.AddEnd(int)" },
        new object[] { new[] { new SignatureChange { Action = "add", Type = "int" } }, "changes[0]: add requires name" },
        new object[] { new[] { new SignatureChange { Action = "add", Name = "q" } }, "changes[0]: add requires type" },
        new object[] { new[] { new SignatureChange { Action = "rename", Name = "a" } }, "changes[0]: rename requires newName" },
        new object[] { new[] { Rename("a", "not valid") }, "changes[0]: 'not valid' is not a valid C# identifier" },
        new object[] { new[] { Add("q", "int", "0", position: -2) },
            "changes[0].position must be -1 (the end) or between 0 and 1 (got -2)" },
        new object[] { new[] { Add("a", "int", "0") }, "changes[0]: the method already has a parameter named 'a'" },
        new object[] { new[] { Add("q", "int x", "0") }, "changes[0]: 'int x' is not a valid C# type" },
        new object[] { new[] { Add("q", "int", "1 +") }, "changes[0]: '1 +' is not a valid C# expression" },
    };

    [Theory]
    [MemberData(nameof(InvalidChanges))]
    public async Task InvalidChange_IsRejectedBeforeAnythingIsComputed(SignatureChange[] changes, string message)
    {
        var (service, workspace, document) = Load(AddEndSource);

        var json = Json(await Change(service, "Ends", "AddEnd", preview: false, changes));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(message);
        (await TextOf(workspace, document.Id)).Should().Be(AddEndSource);
    }

    [Fact]
    public async Task ActionIsMatchedCaseInsensitively()
    {
        var (service, workspace, document) = Load(AddEndSource);

        var json = Json(await Change(service, "Ends", "AddEnd", preview: false,
            new SignatureChange { Action = "Rename", Name = "a", NewName = "q" }));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(AddEndSource, ("AddEnd(int a) => a;", "AddEnd(int q) => q;")));
    }

    [Fact]
    public async Task MultiTargetedMethodWithACrossProjectCallSite_PreviewsOneSite_AndApplies()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);

        var preview = Json(await Change(service, "Multi.Greeter", "Greet", preview: true, Add("times", "int", "1")));
        preview["success"]!.Value<bool>().Should().BeTrue(preview.ToString());
        preview["data"]!["callSitesCount"]!.Value<int>().Should().Be(1, "Consumer calls Greet once");
        ((JArray)preview["data"]!["filesAffected"]!).Select(f => f.Value<string>()).Should().Equal(
            Reported(fixture.MultiFilePath(MultiTargetSources.MultiFileName)),
            Reported(fixture.ConsumerFilePath(MultiTargetSources.ConsumerFileName)));

        var json = Json(await Change(service, "Multi.Greeter", "Greet", preview: false, Add("times", "int", "1")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var expectedMulti = Edited(MultiTargetSources.Multi,
            ("Greet(string name) => Format(name);", "Greet(string name, int times = 1) => Format(name);"));
        foreach (var projectId in new[] { fixture.Net8ProjectId, fixture.Net10ProjectId })
        {
            var greeter = fixture.Workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
            (await greeter.GetTextAsync()).ToString().Should().Be(expectedMulti);
        }
        var consumer = fixture.Workspace.CurrentSolution.GetProject(fixture.ConsumerProjectId)!.Documents.Single();
        (await consumer.GetTextAsync()).ToString().Should().Be(Edited(MultiTargetSources.Consumer,
            ("Greet(\"app\")", "Greet(\"app\", 1)")));
        (await ErrorsOf(fixture.Workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task MultiTargetedCallSites_CountEachSourceSiteOnce_IncludingOneOnlyOneFrameworkCompiles()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);

        var preview = Json(await Change(service, "Multi.Greeter", "Format", preview: true, Add("loud", "bool", "false")));
        preview["success"]!.Value<bool>().Should().BeTrue(preview.ToString());
        preview["data"]!["callSitesCount"]!.Value<int>().Should().Be(2,
            "Greet's call (bound by both frameworks) and Modern's call (net10.0 only) are two source sites");

        var json = Json(await Change(service, "Multi.Greeter", "Format", preview: false, Add("loud", "bool", "false")));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var expected = Edited(MultiTargetSources.Multi,
            ("Format(string name)", "Format(string name, bool loud = false)"),
            ("=> Format(name);", "=> Format(name, false);"),
            ("Format(\"net10\")", "Format(\"net10\", false)"));
        foreach (var projectId in new[] { fixture.Net8ProjectId, fixture.Net10ProjectId })
        {
            var greeter = fixture.Workspace.CurrentSolution.GetProject(projectId)!.Documents.Single();
            (await greeter.GetTextAsync()).ToString().Should().Be(expected);
        }
        (await ErrorsOf(fixture.Workspace)).Should().BeEmpty();
    }
}
