using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// Dependency graphs and their cycles:
//   - DetectCycles reports every elementary cycle (each closed, starting at its ordinally
//     smallest node, ordered by length then sequence) and the strongly connected
//     components, so a cycle through a node already on another cycle is not missed;
//   - the namespace graph is built from the types each namespace's code actually uses
//     (qualified names and aliases included), never from using directives;
//   - dependency_graph and find_circular_dependencies key projects by logical project (a
//     multi-targeted project is one node with its target frameworks), give every node a
//     unique Mermaid-valid id, and never merge same-named or same-sanitized projects.
public class DependencyCycleTests
{
    [Fact]
    public void DetectCycles_FindsEveryElementaryCycle_AndTheComponents()
    {
        var service = new RoslynService();
        var graph = new Dictionary<string, List<string>>
        {
            ["N1"] = new() { "N2", "N3" },
            ["N2"] = new() { "N3" },
            ["N3"] = new() { "N1" },
            ["N4"] = new() { "N1" }
        };

        var analysis = service.DetectCycles(graph);

        analysis.Cycles.Select(c => string.Join(">", c)).Should().Equal("N1>N3>N1", "N1>N2>N3>N1");
        analysis.StronglyConnectedComponents.Select(c => string.Join(",", c)).Should().Equal("N1,N2,N3");
        analysis.Truncated.Should().BeFalse();
    }

    internal const string Namespaces = """
        namespace N1
        {
            public class A
            {
                public N2.B? Bee;
                public N3.C? Cee;
            }
        }

        namespace N2
        {
            public class B { public N3.C? Cee; }
        }

        namespace N3
        {
            public class C { public N1.A? Aye; }
        }

        namespace N4
        {
            using static System.Math;
            using W = N1.A;

            public class D
            {
                public double R => Abs(-1.0);
                public W? Wid;
            }
        }

        namespace N5
        {
            using N1;

            public class E { }
        }
        """;

    private static List<string> Edges(JToken data) =>
        ((JArray)data["edges"]!).Select(e => $"{e["from"]}>{e["to"]}").ToList();

    [Fact]
    public async Task NamespaceGraph_FromTypeUses_FindsEveryCycle()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithMultipleDocuments(
            (Namespaces, "Namespaces.cs"), ("global using N2;", "Globals.cs"));
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.FindCircularDependenciesAsync("namespace"));

        ((JArray)data["nodes"]!).Select(n => n["id"]!.Value<string>()).Should().Equal("N1", "N2", "N3", "N4", "N5");
        Edges(data).Should().Equal("N1>N2", "N1>N3", "N2>N3", "N3>N1", "N4>N1");
        ((JArray)data["cycles"]!).Select(c => string.Join(">", c.Values<string>())).Should().Equal("N1>N3>N1", "N1>N2>N3>N1");
        ((JArray)data["stronglyConnectedComponents"]!).Select(c => string.Join(",", c.Values<string>())).Should().Equal("N1,N2,N3");
        data["hasCycles"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ProjectGraph_MultiTargetedProjectIsOneNode_WithMermaidValidIds()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var service = ToolResult.Load(fixture.Workspace);

        var json = ToolResult.Data(await service.GetDependencyGraphAsync("json"));
        var mermaid = ToolResult.Data(await service.GetDependencyGraphAsync("mermaid"));

        var nodes = (JArray)json["nodes"]!;
        nodes.Select(n => $"{n["id"]}|{n["name"]}").Should().Equal("Consumer|Consumer", "Multi|Multi");
        nodes[1]["targetFrameworks"]!.Values<string>().Should().Equal(MultiTargetSolutionFixture.Net10, MultiTargetSolutionFixture.Net8);
        Edges(json).Should().Equal("Consumer>Multi");
        mermaid["graph"]!.Value<string>().Should().Be("graph TD\n  Consumer[\"Consumer\"]\n  Multi[\"Multi\"]\n  Consumer --> Multi");
    }

    // Two projects named Lib (different project files), and My.Lib / My_Lib, which
    // sanitize to the same identifier; App references all four.
    private static AdhocWorkspace CollidingProjects()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"depgraph-{Guid.NewGuid():N}");
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var references = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var ids = new List<ProjectId>();
        foreach (var (name, folder) in new[] { ("Lib", "A"), ("Lib", "B"), ("My.Lib", "C"), ("My_Lib", "D") })
        {
            var id = ProjectId.CreateNewId();
            ids.Add(id);
            solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, $"{name}{folder}", LanguageNames.CSharp,
                filePath: Path.Combine(directory, folder, $"{name}.csproj"), metadataReferences: references));
        }
        solution = solution.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
            filePath: Path.Combine(directory, "App", "App.csproj"), metadataReferences: references,
            projectReferences: ids.Select(id => new ProjectReference(id))));
        workspace.TryApplyChanges(solution);
        return workspace;
    }

    [Fact]
    public async Task ProjectGraph_SameNamedAndSameSanitizedProjects_StayDistinct()
    {
        var service = ToolResult.Load(CollidingProjects());

        var json = ToolResult.Data(await service.GetDependencyGraphAsync("json"));
        var cycles = ToolResult.Data(await service.FindCircularDependenciesAsync("project"));

        var nodes = (JArray)json["nodes"]!;
        nodes.Select(n => $"{n["id"]}|{n["name"]}").Should().Equal("App|App", "Lib|Lib", "Lib_2|Lib", "My_Lib|My.Lib", "My_Lib_2|My_Lib");
        nodes.Select(n => n["id"]!.Value<string>()).Should().OnlyContain(id => System.Text.RegularExpressions.Regex.IsMatch(id!, "^[A-Za-z_][A-Za-z0-9_]*$"));
        Edges(json).Should().Equal("App>Lib", "App>Lib_2", "App>My_Lib", "App>My_Lib_2");
        ((JArray)cycles["nodes"]!).Count.Should().Be(5);
        cycles["hasCycles"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Format_And_Level_AreValidated()
    {
        var service = ToolResult.Load(CollidingProjects());

        ToolResult.Error(await service.GetDependencyGraphAsync("svg"), ErrorCodes.InvalidParameter)
            ["Context"]!["parameter"]!.Value<string>().Should().Be("format");
        ToolResult.Error(await service.FindCircularDependenciesAsync("filesystem"), ErrorCodes.InvalidParameter)
            ["Context"]!["parameter"]!.Value<string>().Should().Be("level");
    }

    [Fact]
    public async Task Graphs_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithMultipleDocuments((Namespaces, "Namespaces.cs"));
        var host = new InMemoryToolHost(workspace);
        var projects = new InMemoryToolHost(CollidingProjects());

        var namespaces = await host.DataAsync("find_circular_dependencies", new { level = "namespace" });
        var graph = await projects.DataAsync("dependency_graph", new { format = "json" });
        await projects.ErrorAsync("dependency_graph", new { format = "svg" }, ErrorCodes.InvalidParameter);

        ((JArray)namespaces["cycles"]!).Select(c => string.Join(">", c.Values<string>())).Should().Equal("N1>N3>N1", "N1>N2>N3>N1");
        ((JArray)graph["nodes"]!).Select(n => n["id"]!.Value<string>()).Should().Equal("App", "Lib", "Lib_2", "My_Lib", "My_Lib_2");
    }
}
