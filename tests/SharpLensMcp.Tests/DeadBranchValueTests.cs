using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// find_dead_branches reports an unreachable block whose only content is its branch value
// (a `return 2;` or `throw` after a return) at the statement that carries it, names a
// constructor by its type ("Dead.Dead", not "Dead..ctor"), reports each finding once for a
// multi-targeted project, and resolves project names through the logical project model.
public class DeadBranchValueTests
{
    private const string Source = """
        namespace Lib
        {
            public class Dead
            {
                public int ReturnAfterReturn()
                {
                    return 1;
                    return 2;
                }

                public void ThrowAfterReturn()
                {
                    return;
                    throw new System.InvalidOperationException();
                }

                public int StatementAfterReturn()
                {
                    return 1;
                    System.Console.WriteLine("dead");
                }

                public Dead()
                {
                    return;
                    System.Console.WriteLine("dead ctor");
                }
            }
        }
        """;

    private static string Row(string method, string statement)
    {
        var (line, column) = RetargetedSources.Locate(Source, statement);
        return $"{method} Lib/Dead.cs:{line}:{column} {statement}";
    }

    private static readonly string[] Expected =
    {
        Row("Dead.ReturnAfterReturn", "return 2;"),
        Row("Dead.ThrowAfterReturn", "throw new System.InvalidOperationException();"),
        Row("Dead.StatementAfterReturn", "System.Console.WriteLine(\"dead\");"),
        Row("Dead.Dead", "System.Console.WriteLine(\"dead ctor\");"),
    };

    private static List<string> Rows(JToken data) =>
        ((JArray)data["deadBranches"]!).Select(d =>
            $"{d["method"]} {d["location"]!["filePath"]}:{d["location"]!["line"]}:{d["location"]!["column"]} {d["snippet"]}").ToList();

    private static QualitySolutionBuilder Build(bool multiTargeted, out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        if (multiTargeted) builder.AddMultiTargeted("Lib", new[] { ("Dead.cs", Source) });
        else builder.AddProject("Lib", new[] { ("Dead.cs", Source) });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    [Fact]
    public async Task BranchValueOnlyBlocks_AreReported_AtTheirStatement()
    {
        using var builder = Build(multiTargeted: false, out var service, out _);

        var data = JObject.FromObject(await service.FindDeadBranchesAsync(builder.FilePath("Lib", "Dead.cs")))["data"]!;

        Rows(data).Should().Equal(Expected);
        data["methodsAnalyzed"]!.Value<int>().Should().Be(4);
    }

    [Fact]
    public async Task MultiTargetedProject_ByPlainName_ReportsEachBlockOnce()
    {
        using var builder = Build(multiTargeted: true, out var service, out _);

        var json = JObject.FromObject(await service.FindDeadBranchesAsync(projectName: "Lib"));
        var data = json["data"]!;

        Rows(data).Should().Equal(Expected);
        ((JArray)data["deadBranches"]!).Select(d => string.Join(",", d["targetFrameworks"]!.Values<string>()))
            .Should().AllBe("net10.0,net8.0");
        data["methodsAnalyzed"]!.Value<int>().Should().Be(4, "each method is one method, whatever the framework count");
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(4);
    }

    [Fact]
    public async Task ViaDispatcher_BranchValueBlocks_AndUnknownProject()
    {
        using var builder = Build(multiTargeted: true, out _, out var dispatcher);

        var data = await dispatcher.DataAsync("find_dead_branches", new { projectName = "Lib" });
        Rows(data).Should().Equal(Expected);

        var error = await dispatcher.ErrorAsync("find_dead_branches", new { projectName = "Nope" }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("Project not found: Nope");
    }
}
