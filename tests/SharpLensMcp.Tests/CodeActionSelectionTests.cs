using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// The code-action wrappers apply exactly the refactoring they name, choose the variant
// whose result compiles, and work at the positions their descriptions document; fix_all
// runs the fix it was asked for through that fix's own provider and counts a
// multi-targeted file's diagnostic once.
public class CodeActionSelectionTests
{
    private static (int line, int column) PositionOf(string source, string marker, int offset = 0)
    {
        var lines = source.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
        line.Should().BeGreaterThan(-1, $"marker '{marker}' must exist");
        return (line, lines[line].IndexOf(marker, StringComparison.Ordinal) + offset);
    }

    private const string CounterSource = """
        namespace CA;

        public class Counter
        {
            public int count;
            public void Inc() { Bump(ref count); }
            public int Read() => count;
            static void Bump(ref int x) => x++;
        }

        """;

    [Fact]
    public async Task EncapsulateField_PassedByRef_KeepsTheFieldAtTheRefSite_AndCompiles()
    {
        var (service, workspace, document) = Load(CounterSource, "Counter.cs");
        var (line, column) = PositionOf(CounterSource, "count;");

        var json = Json(await service.EncapsulateFieldAsync(document.FilePath!, line, column, preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be("""
            namespace CA;

            public class Counter
            {
                private int count;

                public int Count { get => count; set => count = value; }

                public void Inc() { Bump(ref count); }
                public int Read() => Count;
                static void Bump(ref int x) => x++;
            }

            """);
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    private const string FieldInitSource = """
        namespace CA;

        public class Fld
        {
            public int F = Compute(1) + 2;
            public static int Compute(int x) => x;
        }

        """;

    [Fact]
    public async Task ExtractVariable_WhereOnlyAFieldCouldBeIntroduced_IsRefused()
    {
        var (service, workspace, document) = Load(FieldInitSource, "Fld.cs");
        var (line, column) = PositionOf(FieldInitSource, "Compute(1)");

        var json = Json(await service.ExtractVariableAsync(document.FilePath!, line, column,
            line, column + "Compute(1)".Length, preview: false));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        json["error"]!["Message"]!.Value<string>().Should().Be("No 'extract variable' action found at this position");
        (await TextOf(workspace, document.Id)).Should().Be(FieldInitSource);
    }

    private const string InlineSource = """
        namespace CA;

        public class Calc
        {
            public int Sum(int a, int b)
            {
                int t = a * b;
                return t + t;
            }

            public int Twice(int a) => Helper(a) * 2;

            private static int Helper(int v) => v + 1;
        }

        """;

    [Fact]
    public async Task InlineVariable_OnAMethodCall_DoesNotInlineTheMethod()
    {
        var (service, workspace, document) = Load(InlineSource, "Calc.cs");
        var (line, column) = PositionOf(InlineSource, "Helper(a)");

        var json = Json(await service.InlineVariableAsync(document.FilePath!, line, column, preview: false));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        json["error"]!["Message"]!.Value<string>().Should().Be("No 'inline variable' action found at this position");
        (await TextOf(workspace, document.Id)).Should().Be(InlineSource);
    }

    [Fact]
    public async Task InlineVariable_OnATemporary_InlinesIt()
    {
        var (service, workspace, document) = Load(InlineSource, "Calc.cs");
        var (line, column) = PositionOf(InlineSource, "t = a * b;");

        var json = Json(await service.InlineVariableAsync(document.FilePath!, line, column, preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(InlineSource,
            ("        int t = a * b;\n        return t + t;", "        return a * b + a * b;")));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    private const string ShapesSource = """
        using System;

        namespace CA;

        public interface IShape { double Area(); string Name { get; } }

        public class Circle : IShape
        {
        }

        """;

    [Fact]
    public async Task ImplementMissingMembers_AtTheClassDeclaration_ImplementsTheInterface()
    {
        var (service, workspace, document) = Load(ShapesSource, "Shapes.cs");
        var (line, column) = PositionOf(ShapesSource, "Circle");

        var json = Json(await service.ImplementMissingMembersAsync(document.FilePath!, line, column, preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(ShapesSource, ("""
            public class Circle : IShape
            {
            }
            """, """
            public class Circle : IShape
            {
                public string Name => throw new NotImplementedException();

                public double Area()
                {
                    throw new NotImplementedException();
                }
            }
            """)));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    private const string FixAllSource = """
        namespace FA
        {
            public class A
            {
                public StringBuilder S1() { return new StringBuilder(); }
                public StringBuilder S2() { return new StringBuilder(); }
            }
        }

        """;

    private static async Task<List<(int index, string title)>> FixesFor(RoslynService service, string filePath, string source)
    {
        var (line, column) = PositionOf(source, "StringBuilder S1");
        var fixes = Json(await service.GetCodeFixesAsync(filePath, "CS0246", line, column));
        return ((JArray)fixes["data"]!["fixes"]!)
            .Select(f => (f["index"]!.Value<int>(), f["title"]!.Value<string>()!))
            .ToList();
    }

    [Fact]
    public async Task FixAll_WithAMenuFix_ReturnsItsVariantsInsteadOfFailing()
    {
        var (service, _, document) = Load(FixAllSource, "A.cs");
        var menu = (await FixesFor(service, document.FilePath!, FixAllSource))
            .Single(f => f.title.StartsWith("Generate type", StringComparison.Ordinal));

        var json = Json(await service.FixAllAsync("CS0246", fixIndex: menu.index));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousMatch);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            $"Fix {menu.index} ('{menu.title}') is a menu of variants; fix_all applies a single fix");
    }

    [Fact]
    public async Task FixAll_RunsTheChosenFixThroughItsOwnProvider()
    {
        var (service, _, document) = Load(FixAllSource, "A.cs");
        var qualify = (await FixesFor(service, document.FilePath!, FixAllSource))
            .Single(f => f.title == "System.Text.StringBuilder");

        var json = Json(await service.FixAllAsync("CS0246", fixIndex: qualify.index));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["fixTitle"]!.Value<string>().Should().Be("System.Text.StringBuilder");
        json["data"]!["fallbackMode"]!.Value<bool>().Should().BeTrue(
            "the fully-qualify provider has no FixAll of its own, so the fix runs site by site - never through another provider's FixAll");
        json["data"]!["diagnosticsMatched"]!.Value<int>().Should().Be(4);
        ((JArray)json["data"]!["files"]!).Single()["updatedContent"]!.Value<string>().Should().Be(
            FixAllSource.Replace("StringBuilder", "System.Text.StringBuilder"));
    }

    private const string MultiUnused = """
        namespace Multi
        {
            public class Holder
            {
                public int One()
                {
                    int unused = 5;
                    return 1;
                }
            }
        }
        """;

    [Fact]
    public async Task FixAll_OnAMultiTargetedFile_ListsItOnceAndCountsEachDiagnosticOnce()
    {
        using var fixture = new MultiTargetSolutionFixture(MultiUnused, MultiTargetSources.Consumer.Replace(
            "new Multi.Greeter().Greet(\"app\")", "\"app\""));
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);

        var json = Json(await service.FixAllAsync("CS0219"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["diagnosticsMatched"]!.Value<int>().Should().Be(1);
        var files = (JArray)json["data"]!["files"]!;
        files.Select(f => f["filePath"]!.Value<string>()).Should().Equal(
            Reported(fixture.MultiFilePath(MultiTargetSources.MultiFileName)));
        files[0]["updatedContent"]!.Value<string>().Should().Be(
            MultiUnused.Replace("            int unused = 5;\n", ""));
    }

    [Fact]
    public async Task FixAll_ProjectScope_AcceptsTheLogicalProjectName()
    {
        using var fixture = new MultiTargetSolutionFixture(MultiUnused, MultiTargetSources.Consumer.Replace(
            "new Multi.Greeter().Greet(\"app\")", "\"app\""));
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);

        var json = Json(await service.FixAllAsync("CS0219", scope: "project", projectName: "Multi"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["diagnosticsMatched"]!.Value<int>().Should().Be(1);
        ((JArray)json["data"]!["files"]!).Single()["updatedContent"]!.Value<string>().Should().Be(
            MultiUnused.Replace("            int unused = 5;\n", ""));
    }
}
