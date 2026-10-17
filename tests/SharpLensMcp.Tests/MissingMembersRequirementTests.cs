using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;
using static SharpLensMcp.Tests.MultiTargetNavigationSources;

namespace SharpLensMcp.Tests;

// get_missing_members reports exactly the members the compiler requires: one entry per
// unimplemented interface member (CS0535) and per unoverridden abstract member (CS0534)
// — properties and events once (never their accessors), static abstract interface
// members included, nested types, static members with a body and default interface
// implementations excluded. The expected counts are the compiler's own diagnostics.
public class MissingMembersRequirementTests
{
    internal const string Code = """
        namespace Lib
        {
            public interface IShape
            {
                double Area { get; }
                void Draw();
                event System.EventHandler Fired;
                class Nested { }
            }

            public interface IParse<TSelf> where TSelf : IParse<TSelf>
            {
                static abstract TSelf Parse(string s);
                static int Helper() => 1;
                void WithDefault() { }
            }

            public abstract class ShapeBase
            {
                public abstract double Perimeter { get; }
                public abstract void Scale(double f);
                public virtual string Describe() => "";
            }

            public class Incomplete : ShapeBase, IShape { }

            public class Parsed : IParse<Parsed> { }
        }
        """;

    private static List<string> Entries(JToken data) =>
        ((JArray)data["missingMembers"]!).Select(m =>
            $"{m["fromInterface"] ?? m["fromAbstractClass"]}|{m["memberName"]}|{m["kind"]}|{m["isStatic"]}").ToList();

    private static async Task<int> CompilerCountAsync(Document document, string typeName, params string[] ids)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        var (line, _) = Pos(Code, $"public class {typeName}", typeName);
        return compilation.GetDiagnostics()
            .Count(d => ids.Contains(d.Id) && d.Location.GetLineSpan().StartLinePosition.Line == line);
    }

    [Fact]
    public async Task Incomplete_ReportsEachRequiredMemberOnce_MatchingTheCompiler()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        var service = ToolResult.Load(workspace);
        var (line, column) = Pos(Code, "public class Incomplete", "Incomplete");

        var data = ToolResult.Data(await service.GetMissingMembersAsync(document.FilePath!, line, column));

        Entries(data).Should().Equal(
            "Lib.IShape|Area|Property|False",
            "Lib.IShape|Draw|Method|False",
            "Lib.IShape|Fired|Event|False",
            "Lib.ShapeBase|Perimeter|Property|False",
            "Lib.ShapeBase|Scale|Method|False");
        (await CompilerCountAsync(document, "Incomplete", "CS0535", "CS0534")).Should().Be(5);
    }

    [Fact]
    public async Task StaticAbstractInterfaceMember_IsRequired_ButStaticAndDefaultBodiesAreNot()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        var service = ToolResult.Load(workspace);
        var (line, column) = Pos(Code, "public class Parsed", "Parsed");

        var data = ToolResult.Data(await service.GetMissingMembersAsync(document.FilePath!, line, column));

        Entries(data).Should().Equal("Lib.IParse<Lib.Parsed>|Parse|Method|True");
        (await CompilerCountAsync(document, "Parsed", "CS0535")).Should().Be(1);
    }

    [Fact]
    public async Task Requirements_ViaMcp()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Shapes.cs");
        var host = new InMemoryToolHost(workspace);
        var (line, column) = Pos(Code, "public class Incomplete", "Incomplete");

        var data = await host.DataAsync("get_missing_members", new { filePath = document.FilePath, line, column });

        Entries(data).Should().Equal(
            "Lib.IShape|Area|Property|False",
            "Lib.IShape|Draw|Method|False",
            "Lib.IShape|Fired|Event|False",
            "Lib.ShapeBase|Perimeter|Property|False",
            "Lib.ShapeBase|Scale|Method|False");
    }
}
