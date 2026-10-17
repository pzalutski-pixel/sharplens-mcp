using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// load_solution failure paths through the JSON-RPC dispatcher. Each test uses its own
// McpServer, so a failing load can never disturb the shared fixture's solution.
public class SolutionLoadLifecycleViaMcpTests : IDisposable
{
    private readonly TempSolutionDirectory _dir = new("loadlifecycle-mcp");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task NonSolutionFile_IsInvalidParameter_AndHealthStillReportsThePreviousSolution()
    {
        _dir.Write("Lib/Lib.csproj", TempSolutionDirectory.MinimalCsproj);
        var good = _dir.WriteSolution("Good.sln", ("Lib", "Lib\\Lib.csproj"));
        var ini = _dir.Write("win.ini", "[fonts]\n");
        var harness = new McpHarnessOps(new McpServer());
        (await harness.CallToolAsync("load_solution", new { solutionPath = good }))["success"]!.Value<bool>().Should().BeTrue();

        var failed = await harness.CallToolAsync("load_solution", new { solutionPath = ini });

        failed["success"]!.Value<bool>().Should().BeFalse();
        failed["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        failed["error"]!["message"]!.Value<string>().Should().Be(
            $"Not a solution file: {ini}. load_solution takes a .sln, .slnx or .slnf file.");

        var health = await harness.CallToolAsync("health_check");
        health["data"]!["status"]!.Value<string>().Should().Be("Ready");
        health["data"]!["solution"]!["path"]!.Value<string>().Should().Be(good,
            "a failed load must leave the previously loaded solution in place");
        health["data"]!["lastLoadFailure"]!["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        health["data"]!["lastLoadFailure"]!["solutionPath"]!.Value<string>().Should().Be(ini);
    }

    [Fact]
    public async Task LoadSolutionTimeout_HintSpeaksToLoading_NotToNarrowingTheRequest()
    {
        using var precancelled = new CancellationTokenSource();
        precancelled.Cancel();
        var harness = new McpHarnessOps(new McpServer(TimeSpan.FromMinutes(5), precancelled.Token));

        var inner = await harness.CallToolAsync("load_solution", new { solutionPath = _dir.PathOf("Any.sln") });

        inner["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.Timeout);
        inner["error"]!["hint"]!.Value<string>().Should().Be(
            "Raise SHARPLENS_TIMEOUT_SECONDS: loading runs a design-time build of every project, so a large solution needs a larger budget. The previously loaded solution, if any, is unchanged.");
    }
}
