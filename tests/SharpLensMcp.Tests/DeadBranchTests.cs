using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_dead_branches over an IN-MEMORY workspace: the dead-code source lives
// in a string because an on-disk fixture with unreachable statements would
// emit CS0162 build warnings (and suppression pragmas are forbidden in this
// repo). Each test pins the flagged statement by resolving its line from the
// source string.
public class DeadBranchTests
{
    private const string DeadCodeSource = """
        namespace DeadBranchProbe;

        public class FlowHost
        {
            public int AfterReturn()
            {
                return 1;
                System.Console.WriteLine("after-return");
            }

            public void ConstantFalse()
            {
                if (false)
                {
                    System.Console.WriteLine("constant-false");
                }
            }

            public void AfterInfiniteLoop()
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(1);
                }
                System.Console.WriteLine("after-loop");
            }

            public int FullyReachable(int seed)
            {
                if (seed > 0)
                {
                    return seed;
                }
                return -seed;
            }
        }
        """;

    private static int LineOf(string marker)
    {
        var lines = DeadCodeSource.Replace("\r\n", "\n").Split('\n');
        var index = Array.FindIndex(lines, l => l.Contains(marker));
        index.Should().BeGreaterThan(-1, $"the probe source must contain '{marker}'");
        return index;
    }

    private static (RoslynService service, string filePath) CreateService()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(DeadCodeSource, "FlowHost.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, document.FilePath!);
    }

    [Fact]
    public async Task FlagsAllThreeDeadShapes_WithExactLines()
    {
        var (service, filePath) = CreateService();

        var result = await service.FindDeadBranchesAsync(filePath: filePath);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["scope"]!.Value<string>().Should().Be("file");
        data["methodsAnalyzed"]!.Value<int>().Should().Be(4);

        var branches = (data["deadBranches"] as JArray)!;
        var byMethod = branches.GroupBy(b => b["method"]!.Value<string>())
            .ToDictionary(g => g.Key!, g => g.ToList());

        byMethod.Keys.Should().BeEquivalentTo(
            new[] { "FlowHost.AfterReturn", "FlowHost.ConstantFalse", "FlowHost.AfterInfiniteLoop" },
            "exactly the three dead shapes must be flagged and FullyReachable must not");

        byMethod["FlowHost.AfterReturn"].Should().Contain(b =>
            b["location"]!["line"]!.Value<int>() == LineOf("after-return"));
        byMethod["FlowHost.ConstantFalse"].Should().Contain(b =>
            b["location"]!["line"]!.Value<int>() == LineOf("constant-false"));
        byMethod["FlowHost.AfterInfiniteLoop"].Should().Contain(b =>
            b["location"]!["line"]!.Value<int>() == LineOf("after-loop"));

        branches.Should().OnlyContain(b =>
            b["snippet"]!.Value<string>()!.Contains("Console") ||
            b["snippet"]!.Value<string>()!.Contains("WriteLine"),
            "each flagged entry must carry the unreachable statement itself");
    }

    [Fact]
    public async Task FlagsDeadArm_InExpressionBodiedProperty()
    {
        const string source = """
            namespace DeadBranchProbe;

            public class ExprHost
            {
                private static int Compute() => 99;
                public int Dead => true ? 1 : Compute();
            }
            """;
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(source, "ExprHost.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.FindDeadBranchesAsync(filePath: document.FilePath!);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;

        // The expression-bodied property is now analyzed alongside the method.
        data["methodsAnalyzed"]!.Value<int>().Should().Be(2,
            "both Compute()'s body and the expression-bodied Dead property must be analyzed");

        var branches = (data["deadBranches"] as JArray)!;
        branches.Should().Contain(b => b["method"]!.Value<string>() == "ExprHost.Dead",
            "the unreachable ': Compute()' arm of the constant-true ternary in the expression body must be flagged");
    }

    [Fact]
    public async Task BothScopes_ReturnsInvalidParameter()
    {
        var (service, filePath) = CreateService();

        var result = await service.FindDeadBranchesAsync(filePath: filePath, projectName: "TestProject");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Contain("exactly one");
    }

    [Fact]
    public async Task NeitherScope_ReturnsInvalidParameter()
    {
        var (service, _) = CreateService();

        var result = await service.FindDeadBranchesAsync();

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task UnknownProject_ReturnsInvalidParameterListingProjects()
    {
        var (service, _) = CreateService();

        var result = await service.FindDeadBranchesAsync(projectName: "NoSuchProject");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter,
            "an unknown project name is an invalid argument, routed through ProjectNotFoundError");
        json["error"]!["Hint"]!.Value<string>().Should().Contain("TestProject",
            "the error must list the available project names");
    }

    [Fact]
    public async Task ProjectScope_FindsTheSameBranches()
    {
        var (service, _) = CreateService();

        var result = await service.FindDeadBranchesAsync(projectName: "TestProject");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["scope"]!.Value<string>().Should().Be("project");
        (data["deadBranches"] as JArray)!.Count.Should().Be(3);
    }
}
