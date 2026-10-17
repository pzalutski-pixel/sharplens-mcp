using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// The DOTNET_SOLUTION_PATH startup auto-load runs beside the request loop: the protocol
// handshake is answered at once, tools/call waits for the load, and a load that fails or
// exceeds its budget is reported by health_check rather than only logged. Driven in-process
// through RunAsync with a deterministic gate that holds the auto-load until the test says.
public class McpServerAutoLoadTests : IDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(20);
    private readonly TempSolutionDirectory _dir = new("autoload");

    public void Dispose() => _dir.Dispose();

    private string WriteSolution()
    {
        _dir.Write("Lib/Lib.csproj", TempSolutionDirectory.MinimalCsproj);
        _dir.Write("Lib/Widget.cs", "namespace Lib;\npublic class Widget { }\n");
        return _dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));
    }

    private static JObject InnerOf(string responseLine) =>
        JObject.Parse(JObject.Parse(responseLine)["result"]!["content"]![0]!["text"]!.Value<string>()!);

    [Fact]
    public async Task Initialize_IsAnsweredWhileTheAutoLoadIsStillRunning_AndToolCallsWaitForIt()
    {
        var sln = WriteSolution();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new McpServer(TimeSpan.FromMinutes(5), testAutoLoadGate: ct => gate.Task.WaitAsync(ct));
        var input = new LineChannelReader();
        var output = new LineChannelWriter();
        var run = server.RunAsync(input, output, sln);

        input.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""");
        var init = JObject.Parse(await output.ReadLineAsync(ResponseTimeout,
            "initialize must be answered while the auto-load is held at its gate"));
        init["id"]!.Value<long>().Should().Be(1);
        init["result"]!["serverInfo"]!["name"]!.Value<string>().Should().Be("SharpLensMcp");

        input.Send("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        JObject.Parse(await output.ReadLineAsync(ResponseTimeout, "ping is answered during the auto-load"))["id"]!
            .Value<long>().Should().Be(2);

        // A tools/call sent now waits for the auto-load instead of seeing no solution.
        input.Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""");
        output.TryReadLine(out _).Should().BeFalse("the tools/call is held until the auto-load finishes");
        gate.SetResult();

        var health = InnerOf(await output.ReadLineAsync(ResponseTimeout, "health_check answers once the auto-load completes"));
        health["data"]!["status"]!.Value<string>().Should().Be("Ready");
        health["data"]!["solution"]!["path"]!.Value<string>().Should().Be(sln);

        input.Complete();
        await run;
    }

    [Fact]
    public async Task AutoLoadExceedingItsBudget_IsReportedByHealthCheck()
    {
        var sln = WriteSolution();
        var server = new McpServer(TimeSpan.FromMinutes(5),
            testAutoLoadGate: ct => Task.Delay(Timeout.Infinite, ct),
            autoLoadTimeout: TimeSpan.FromMilliseconds(200));
        var input = new LineChannelReader();
        var output = new LineChannelWriter();
        var run = server.RunAsync(input, output, sln);

        input.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""");
        var health = InnerOf(await output.ReadLineAsync(ResponseTimeout, "health_check answers after the auto-load times out"));

        health["data"]!["status"]!.Value<string>().Should().Be("Not Ready");
        var failure = health["data"]!["lastLoadFailure"]!;
        failure["solutionPath"]!.Value<string>().Should().Be(sln);
        failure["code"]!.Value<string>().Should().Be(ErrorCodes.Timeout);
        failure["message"]!.Value<string>().Should().Be(
            $"The startup auto-load of {sln} (DOTNET_SOLUTION_PATH) exceeded its 0.2-second budget. Raise SHARPLENS_TIMEOUT_SECONDS, or call load_solution.");

        input.Complete();
        await run;
    }

    [Fact]
    public async Task AutoLoadOfAnAmbiguousDirectory_IsReportedByHealthCheck_AndNothingIsLoaded()
    {
        _dir.Write("A.sln", "");
        _dir.Write("B.sln", "");
        var server = new McpServer(TimeSpan.FromMinutes(5));
        var input = new LineChannelReader();
        var output = new LineChannelWriter();
        var run = server.RunAsync(input, output, _dir.Root);

        input.Send("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""");
        var health = InnerOf(await output.ReadLineAsync(ResponseTimeout, "health_check answers after the auto-load resolution fails"));

        health["data"]!["status"]!.Value<string>().Should().Be("Not Ready");
        var failure = health["data"]!["lastLoadFailure"]!;
        failure["solutionPath"]!.Value<string>().Should().Be(_dir.Root);
        failure["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        failure["message"]!.Value<string>().Should().Be(
            $"DOTNET_SOLUTION_PATH is a directory with 2 solution files (A.sln, B.sln); set it to one of them: {_dir.Root}");

        input.Complete();
        await run;
    }
}
