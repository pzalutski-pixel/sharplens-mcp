using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Out-of-process contracts of the server process itself — environment parsing, stdin
// decoding, stdout purity, startup auto-load — that only exist for a real spawned binary.
// Each test drives its own process against a small temp solution.
public class StdioProtocolTests
{
    private static string WriteOneProjectSolution(TempSolutionDirectory dir, string typeSource)
    {
        dir.Write("Lib/Lib.csproj", TempSolutionDirectory.MinimalCsproj);
        dir.Write("Lib/Widget.cs", typeSource);
        return dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));
    }

    [Fact]
    public async Task EnvironmentVariables_ParseConsistently_AcrossEveryVariable()
    {
        // Every variable goes through one parser: booleans are case-insensitive, an integer
        // below its minimum falls back to the default, a timeout above the timer maximum is
        // clamped (and tools/call keeps working), and the ROSLYN_* legacy spelling is
        // honored for SHARPLENS_ABSOLUTE_PATHS like every other variable.
        using var dir = new TempSolutionDirectory("stdio-env");
        var sln = WriteOneProjectSolution(dir, "namespace Lib;\npublic class Widget { }\n");

        await using var server = StdioServerProcess.Start(new Dictionary<string, string?>
        {
            ["SHARPLENS_ENABLE_SEMANTIC_CACHE"] = "False",
            ["SHARPLENS_MAX_DIAGNOSTICS"] = "-5",
            ["SHARPLENS_TIMEOUT_SECONDS"] = "99999999",
            ["ROSLYN_ABSOLUTE_PATHS"] = "true",
        });

        var load = StdioServerProcess.ParseInner(await server.RoundTripAsync(
            StdioServerProcess.ToolCall(1, "load_solution", new { solutionPath = sln })));
        load["success"]!.Value<bool>().Should().BeTrue($"load_solution must succeed; got {load}");

        var health = StdioServerProcess.ParseInner(await server.RoundTripAsync(
            StdioServerProcess.ToolCall(2, "health_check", new { })));
        var configuration = health["data"]!["configuration"]!;
        configuration["semanticCacheEnabled"]!.Value<bool>().Should().BeFalse("'False' disables the cache");
        configuration["maxDiagnostics"]!.Value<int>().Should().Be(100, "-5 is below the minimum, so the default applies");
        configuration["timeoutSeconds"]!.Value<int>().Should().Be(2147483, "99999999 is clamped to the timer maximum");
        configuration["absolutePaths"]!.Value<bool>().Should().BeTrue("ROSLYN_ABSOLUTE_PATHS is the legacy spelling");

        var search = StdioServerProcess.ParseInner(await server.RoundTripAsync(
            StdioServerProcess.ToolCall(3, "search_symbols", new { query = "Widget" })));
        search["data"]!["results"]![0]!["location"]!["filePath"]!.Value<string>().Should().Be(
            dir.PathOf("Lib/Widget.cs").Replace('\\', '/'),
            "absolute paths are on, so locations are not solution-relative");

        var (exitCode, stderr) = await server.ShutdownAsync();
        exitCode.Should().Be(0);
        stderr.Should().Contain("SHARPLENS_MAX_DIAGNOSTICS='-5' is below the minimum 1; using the default 100");
        stderr.Should().Contain("SHARPLENS_TIMEOUT_SECONDS='99999999' is above the maximum 2147483; using the maximum 2147483",
            Exactly.Once(), "the variable is read by the dispatcher and by health_check, but reported once");
    }

    [Fact]
    public async Task Stdin_IsDecodedAsUtf8_SoNonAsciiPathsArriveIntact()
    {
        // MCP stdio messages are UTF-8. The path is sent as raw UTF-8 bytes (not \u
        // escapes); decoding stdin with the console code page would mangle it into a
        // path that does not exist.
        using var dir = new TempSolutionDirectory("stdio-utf8");
        dir.Write("Ünïcödé/Lib/Lib.csproj", TempSolutionDirectory.MinimalCsproj);
        dir.Write("Ünïcödé/Lib/Widget.cs", "namespace Lib;\npublic class Widget { }\n");
        var sln = dir.WriteSolution("Ünïcödé/€x.sln", ("Lib", "Lib\\Lib.csproj"));

        await using var server = StdioServerProcess.Start(new Dictionary<string, string?>());
        var request = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JObject
            {
                ["name"] = "load_solution",
                ["arguments"] = new JObject { ["solutionPath"] = sln }
            }
        }.ToString(Newtonsoft.Json.Formatting.None);
        request.Should().Contain("Ünïcödé", "the request line must carry the raw characters, not escapes");

        var load = StdioServerProcess.ParseInner(await server.RoundTripAsync(request));

        load["success"]!.Value<bool>().Should().BeTrue($"the UTF-8 path must resolve to the real file; got {load}");
        load["data"]!["solutionPath"]!.Value<string>().Should().Be(sln);
        load["data"]!["projectCount"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task InProcessCodeWritingToConsoleOut_DoesNotCorruptTheProtocolStream()
    {
        // Analyzers and generators run inside the server process. One that writes to
        // Console.Out must not inject a line into the JSON-RPC stream: the protocol owns
        // the real stdout, and Console.Out is routed to stderr.
        using var dir = new TempSolutionDirectory("stdio-stdout");
        var analyzerPath = StdoutWritingAnalyzer.Emit(dir.PathOf("analyzer"));
        dir.Write("Lib/Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n" +
            $"  <ItemGroup>\n    <Analyzer Include=\"{analyzerPath}\" />\n  </ItemGroup>\n</Project>\n");
        dir.Write("Lib/Widget.cs", "namespace Lib;\npublic class Widget { public void Spin() { } }\n");
        var sln = dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));
        TempSolutionDirectory.Restore(sln);

        await using var server = StdioServerProcess.Start(new Dictionary<string, string?>());
        StdioServerProcess.ParseInner(await server.RoundTripAsync(
            StdioServerProcess.ToolCall(1, "load_solution", new { solutionPath = sln })))["success"]!.Value<bool>()
            .Should().BeTrue();

        // RoundTripAsync fails if the next stdout line is not a JSON-RPC message.
        var diagnostics = StdioServerProcess.ParseInner(await server.RoundTripAsync(
            StdioServerProcess.ToolCall(2, "get_diagnostics", new { runAnalyzers = true })));
        diagnostics["data"]!["analyzersRan"]!.Value<bool>().Should().BeTrue("the probe analyzer must actually run");
        ((JArray)diagnostics["data"]!["diagnostics"]!).Select(d => d["id"]!.Value<string>())
            .Should().Contain(StdoutWritingAnalyzer.DiagnosticId, "the probe analyzer reports on the compilation");

        // The ping after it proves the stream is still line-aligned.
        var ping = await server.RoundTripAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        ping["id"]!.Value<long>().Should().Be(3);

        var (_, stderr) = await server.ShutdownAsync();
        stderr.Should().Contain(StdoutWritingAnalyzer.ProbeText, "Console.Out output is kept, on stderr");
    }
}
