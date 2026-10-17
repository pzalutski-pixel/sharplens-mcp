using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// find_naming_violations reproduces the compiler's own IDE1006 verdicts. The expected rows
// below are the IDE1006 warnings `dotnet build` (EnforceCodeStyleInBuild) reports for this
// exact source and configuration, converted to zero-based positions. They pin:
//   - rule order: required modifiers (a const counting as static too), then accessibility
//     subsets, then symbol-kind subsets, then the rule name — so a private field takes the
//     private-field rule, a const the const rule, and a static field the (severity none)
//     static rule;
//   - a severity-none rule silences the symbols it applies to instead of letting another
//     rule match; a rule without a severity is ignored;
//   - required_modifiers, all_upper capitalization and word separators;
//   - pascal_case/camel_case accept underscores inside a word but reject a common prefix
//     ("_", "m_") the style does not require;
//   - the symbols IDE1006 checks: namespaces, type parameters, interface and abstract
//     members, parameters of overrides/implementations/delegates/indexers/operators/
//     class primary constructors, every local (for, using, catch, foreach, out, pattern,
//     deconstruction), const locals, local functions and lambda parameters — but not
//     overriding or implementing members' names, discards, record primary-constructor
//     parameters, or enum members; an @-escaped name is checked without its '@'.
public class NamingRoslynParityTests
{
    private const string Config = """
        is_global = true
        dotnet_naming_rule.members_pascal.symbols = members
        dotnet_naming_rule.members_pascal.style = pascal
        dotnet_naming_rule.members_pascal.severity = warning
        dotnet_naming_symbols.members.applicable_kinds = property, method, field, event, delegate, local_function
        dotnet_naming_style.pascal.capitalization = pascal_case

        dotnet_naming_rule.private_fields_underscore.symbols = private_fields
        dotnet_naming_rule.private_fields_underscore.style = underscore_camel
        dotnet_naming_rule.private_fields_underscore.severity = warning
        dotnet_naming_symbols.private_fields.applicable_kinds = field
        dotnet_naming_symbols.private_fields.applicable_accessibilities = private
        dotnet_naming_style.underscore_camel.capitalization = camel_case
        dotnet_naming_style.underscore_camel.required_prefix = _

        dotnet_naming_rule.consts_upper.symbols = consts
        dotnet_naming_rule.consts_upper.style = upper
        dotnet_naming_rule.consts_upper.severity = warning
        dotnet_naming_symbols.consts.applicable_kinds = field, local
        dotnet_naming_symbols.consts.required_modifiers = const
        dotnet_naming_style.upper.capitalization = all_upper
        dotnet_naming_style.upper.word_separator = _

        dotnet_naming_rule.statics_off.symbols = statics
        dotnet_naming_rule.statics_off.style = pascal
        dotnet_naming_rule.statics_off.severity = none
        dotnet_naming_symbols.statics.applicable_kinds = field
        dotnet_naming_symbols.statics.required_modifiers = static

        dotnet_naming_rule.locals_camel.symbols = locals
        dotnet_naming_rule.locals_camel.style = camel
        dotnet_naming_rule.locals_camel.severity = warning
        dotnet_naming_symbols.locals.applicable_kinds = local, parameter
        dotnet_naming_style.camel.capitalization = camel_case

        dotnet_naming_rule.types_pascal.symbols = types
        dotnet_naming_rule.types_pascal.style = pascal
        dotnet_naming_rule.types_pascal.severity = warning
        dotnet_naming_symbols.types.applicable_kinds = class, struct, interface, enum, namespace, type_parameter

        dotnet_naming_rule.nosev.symbols = types
        dotnet_naming_rule.nosev.style = camel
        """;

    private const string Source = """
        namespace n1.lower_ns;

        public interface IBase { void base_impl(int Param1); }
        public abstract class AbstractBase { public abstract void abs_member(int Param2); }

        public class C<tParam> : AbstractBase, IBase
        {
            private int _count;
            private int Wrong;
            private int m_Prefixed;
            private int _two_words;
            public int Bad_Name;
            public int my_Prop => 1;
            public int _publicUnderscore;
            private const int MAX_ITEMS = 3;
            private const int MaxItems2 = 3;
            private static int StaticField;
            private static int static_field2;
            private static readonly int ReadonlyStatic = 1;
            public event System.EventHandler? my_event;
            public delegate void my_delegate(int DelegateParam);
            public int this[int IndexParam] => IndexParam;
            public static C<tParam> operator +(C<tParam> LeftOp, C<tParam> b) => LeftOp;
            public C(int CtorParam) { }
            public void base_impl(int Param1) { }
            public override void abs_member(int Param2) { }
            void IBase_Explicit_Unused() { }

            public void Method(int GoodOrBad, int _discardish, int _)
            {
                var LocalVar = 1;
                for (int ForVar = 0; ForVar < 1; ForVar++) { }
                using (var UsingVar = new System.IO.MemoryStream()) { }
                try { } catch (System.Exception CatchVar) { }
                foreach (var EachVar in new int[0]) { }
                var @event = 1;
                const int Limit = 5;
                const int LIMIT_OK = 5;
                int.TryParse("1", out var OutVar);
                object o = 1;
                if (o is int PatternVar) { }
                var (DeconA, deconB) = (1, 2);
                System.Action<int> lam = LambdaParam => { };
                System.Func<int, int, int> lam2 = (_, __) => 1;
                void local_func(int LocalFuncParam) { }
                static void StaticLocal() { }
                local_func(1);
                StaticLocal();
            }

            public void GenericMethod<TMethod, tLowerMethod>() { }
        }

        public record Rec(int PrimaryParam);
        public class Prim(int PrimaryClassParam) { public int P => PrimaryClassParam; }
        public struct lower_struct { }
        public enum lower_enum { member_value }
        """;

    private const string Upper = "These words must begin with upper case characters: ";
    private static string FirstLower(string word) => $"The first word, '{word}', must begin with a lower case character";
    private const string NoLower = "These words cannot contain lower case characters: ";

    // kind name line:column rule | reason — zero-based, in location order: the IDE1006 set.
    private static readonly string[] Expected =
    {
        $"namespace n1 0:10 types_pascal | {Upper}n1",
        $"namespace lower_ns 0:13 types_pascal | {Upper}lower_ns",
        $"method base_impl 2:30 members_pascal | {Upper}base_impl",
        $"parameter Param1 2:44 locals_camel | {FirstLower("Param1")}",
        $"method abs_member 3:58 members_pascal | {Upper}abs_member",
        $"parameter Param2 3:73 locals_camel | {FirstLower("Param2")}",
        $"type_parameter tParam 5:15 types_pascal | {Upper}tParam",
        "field Wrong 8:16 private_fields_underscore | Missing prefix: '_'",
        "field m_Prefixed 9:16 private_fields_underscore | Missing prefix: '_'",
        $"property my_Prop 12:15 members_pascal | {Upper}my_Prop",
        "field _publicUnderscore 13:15 members_pascal | Prefix '_' is not expected",
        $"field MaxItems2 15:22 consts_upper | {NoLower}MaxItems2",
        $"event my_event 19:38 members_pascal | {Upper}my_event",
        $"delegate my_delegate 20:25 members_pascal | {Upper}my_delegate",
        $"parameter DelegateParam 20:41 locals_camel | {FirstLower("DelegateParam")}",
        $"parameter IndexParam 21:24 locals_camel | {FirstLower("IndexParam")}",
        $"parameter LeftOp 22:49 locals_camel | {FirstLower("LeftOp")}",
        $"parameter CtorParam 23:17 locals_camel | {FirstLower("CtorParam")}",
        $"parameter Param1 24:30 locals_camel | {FirstLower("Param1")}",
        $"parameter Param2 25:40 locals_camel | {FirstLower("Param2")}",
        $"parameter GoodOrBad 28:27 locals_camel | {FirstLower("GoodOrBad")}",
        "parameter _discardish 28:42 locals_camel | Prefix '_' is not expected",
        $"local LocalVar 30:12 locals_camel | {FirstLower("LocalVar")}",
        $"local ForVar 31:17 locals_camel | {FirstLower("ForVar")}",
        $"local UsingVar 32:19 locals_camel | {FirstLower("UsingVar")}",
        $"local CatchVar 33:40 locals_camel | {FirstLower("CatchVar")}",
        $"local EachVar 34:21 locals_camel | {FirstLower("EachVar")}",
        $"local Limit 36:18 consts_upper | {NoLower}Limit",
        $"local OutVar 38:34 locals_camel | {FirstLower("OutVar")}",
        $"local PatternVar 40:21 locals_camel | {FirstLower("PatternVar")}",
        $"local DeconA 41:13 locals_camel | {FirstLower("DeconA")}",
        $"parameter LambdaParam 42:33 locals_camel | {FirstLower("LambdaParam")}",
        $"local_function local_func 44:13 members_pascal | {Upper}local_func",
        $"parameter LocalFuncParam 44:28 locals_camel | {FirstLower("LocalFuncParam")}",
        $"type_parameter tLowerMethod 50:39 types_pascal | {Upper}tLowerMethod",
        $"parameter PrimaryClassParam 54:22 locals_camel | {FirstLower("PrimaryClassParam")}",
        $"struct lower_struct 55:14 types_pascal | {Upper}lower_struct",
        $"enum lower_enum 56:12 types_pascal | {Upper}lower_enum",
    };

    private static List<string> Rows(JToken data) =>
        ((JArray)data["violations"]!).Select(v =>
            $"{v["kind"]} {v["name"]} {v["location"]!["line"]}:{v["location"]!["column"]} {v["rule"]} | {v["reason"]}").ToList();

    internal static AdhocWorkspace WithConfig(AdhocWorkspace workspace, string config)
    {
        var solution = workspace.CurrentSolution;
        foreach (var project in solution.Projects.ToList())
        {
            var directory = Path.GetDirectoryName(project.FilePath ?? project.Documents.First().FilePath!)!;
            solution = solution.AddAnalyzerConfigDocument(
                DocumentId.CreateNewId(project.Id), ".globalconfig", SourceText.From(config),
                filePath: Path.Combine(directory, ".globalconfig"));
        }
        workspace.TryApplyChanges(solution).Should().BeTrue();
        return workspace;
    }

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        builder.AddProject("N1", new[] { ("Cases.cs", Source) });
        var workspace = WithConfig(builder.Build(), Config);
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    [Fact]
    public async Task EditorConfigRules_ReproduceTheCompilersIde1006Verdicts()
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.FindNamingViolationsAsync(maxResults: 1000))["data"]!;

        Rows(data).Should().Equal(Expected);
        data["violationCount"]!.Value<int>().Should().Be(38);
        data["rulesEvaluated"]!.Value<int>().Should().Be(6);
        ((JArray)data["unsupportedRules"]!).Select(u => $"{u["ruleName"]}: {u["reason"]}").Should().Equal(
            "nosev: severity is not set - the rule is ignored, as the compiler ignores it");
    }

    [Fact]
    public async Task DefaultsMode_ChecksEveryLocalKind_AndExemptsConstLocalsLikeConstFields()
    {
        const string source = """
            namespace D
            {
                public class Host
                {
                    private const int MaxItems = 3;
                    public void Run(int[] values)
                    {
                        for (int Index = 0; Index < values.Length; Index++) { }
                        using (var Stream = new System.IO.MemoryStream()) { }
                        try { } catch (System.Exception Ex) { }
                        var @event = 1;
                        const int Limit = 5;
                        void local_helper(int LocalParam) { }
                        System.Action<int> a = LambdaParam => { };
                    }
                    public int Bad_Name() => 1;
                }
            }
            """;
        using var builder = new QualitySolutionBuilder();
        builder.AddProject("D", new[] { ("Host.cs", source) });
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(builder.Build());

        var data = JObject.FromObject(await service.FindNamingViolationsAsync("D"))["data"]!;

        string Row(string kind, string name, string rule, string reason, string? marker = null)
        {
            var (line, column) = RetargetedSources.Locate(source, marker ?? name);
            return $"{kind} {name} {line}:{column} {rule} | {reason}";
        }
        Rows(data).Should().Equal(
            Row("local", "Index", "default_parameters_and_locals", FirstLower("Index")),
            Row("local", "Stream", "default_parameters_and_locals", FirstLower("Stream")),
            Row("local", "Ex", "default_parameters_and_locals", FirstLower("Ex"), marker: "Ex)"),
            Row("local_function", "local_helper", "default_methods", Upper + "local_helper"),
            Row("parameter", "LocalParam", "default_parameters_and_locals", FirstLower("LocalParam")),
            Row("parameter", "LambdaParam", "default_parameters_and_locals", FirstLower("LambdaParam")));
    }

    [Fact]
    public async Task ViaDispatcher_EditorConfigRules_AndUnknownProject()
    {
        using var builder = Build(out _, out var dispatcher);

        var data = await dispatcher.DataAsync("find_naming_violations", new { maxResults = 1000 });
        Rows(data).Should().Equal(Expected);

        var error = await dispatcher.ErrorAsync("find_naming_violations", new { projectName = "Nope" }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("Project not found: Nope");
    }
}
