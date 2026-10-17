using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Newtonsoft.Json.Linq;

namespace SharpLensMcp.Tests.Mcp;

// One spawned instance of the built server binary, driven over its real stdin/stdout.
// stdin is written as UTF-8 (no BOM) the way MCP clients send it; stderr is drained
// concurrently so an undrained pipe can never deadlock the server, and is kept for
// assertions about what the server logged.
internal sealed class StdioServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderr;

    private StdioServerProcess(Process process)
    {
        _process = process;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static StdioServerProcess Start(IReadOnlyDictionary<string, string?> environment)
    {
        var repoRoot = FindRepoRoot();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        var serverDll = Path.Combine(repoRoot, "src", "bin", configuration, "net8.0", "SharpLensMcp.dll");
        File.Exists(serverDll).Should().BeTrue(
            $"the server must be built in {configuration} before this test runs (building the tests builds src)");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { serverDll },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        // The child inherits the test process environment; strip the variables the server
        // reads so only the ones this test sets apply.
        foreach (var name in new[]
        {
            "DOTNET_SOLUTION_PATH", "SHARPLENS_TIMEOUT_SECONDS", "ROSLYN_TIMEOUT_SECONDS",
            "SHARPLENS_MAX_DIAGNOSTICS", "ROSLYN_MAX_DIAGNOSTICS", "SHARPLENS_ENABLE_SEMANTIC_CACHE",
            "ROSLYN_ENABLE_SEMANTIC_CACHE", "SHARPLENS_ABSOLUTE_PATHS", "ROSLYN_ABSOLUTE_PATHS",
            "SHARPLENS_LOG_LEVEL", "ROSLYN_LOG_LEVEL"
        })
        {
            psi.Environment.Remove(name);
        }
        foreach (var (name, value) in environment)
        {
            psi.Environment[name] = value;
        }

        return new StdioServerProcess(Process.Start(psi)!);
    }

    // Writes one line and reads exactly one response line, asserting stdout purity: the
    // line must parse as a JSON-RPC 2.0 envelope.
    public async Task<JObject> RoundTripAsync(string requestJson, TimeSpan? readTimeout = null)
    {
        await SendAsync(requestJson);
        return await ReadEnvelopeAsync(readTimeout ?? TimeSpan.FromMinutes(6));
    }

    public async Task SendAsync(string line)
    {
        await _process.StandardInput.WriteLineAsync(line);
        await _process.StandardInput.FlushAsync();
    }

    public async Task<JObject> ReadEnvelopeAsync(TimeSpan readTimeout)
    {
        var readTask = _process.StandardOutput.ReadLineAsync();
        var winner = await Task.WhenAny(readTask, Task.Delay(readTimeout));
        winner.Should().BeSameAs(readTask, "the server must answer within the read timeout");

        var line = await readTask;
        line.Should().NotBeNullOrEmpty("EOF or an empty stdout line means the server died or polluted stdout");

        JObject envelope;
        try
        {
            envelope = JObject.Parse(line!);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"stdout line is not JSON (stdout purity violated): {ex.Message}\nLine: {line}");
        }
        envelope["jsonrpc"]!.Value<string>().Should().Be("2.0", "every stdout line must be a JSON-RPC message");
        return envelope;
    }

    // Unwraps result.content[0].text of a tools/call response into the tool envelope.
    public static JObject ParseInner(JObject envelope)
    {
        envelope["error"].Should().BeNull($"tools/call must not fail at the JSON-RPC layer; got {envelope}");
        var text = envelope["result"]!["content"]![0]!["text"]!.Value<string>()!;
        return JObject.Parse(text);
    }

    public static string ToolCall(long id, string tool, object arguments) =>
        new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JObject { ["name"] = tool, ["arguments"] = JObject.FromObject(arguments) }
        }.ToString(Newtonsoft.Json.Formatting.None);

    // Closes stdin, waits for the clean EOF shutdown, and returns everything written to stderr.
    public async Task<(int exitCode, string stderr)> ShutdownAsync()
    {
        _process.StandardInput.Close();
        _process.WaitForExit(60_000).Should().BeTrue("the server must shut down when stdin closes");
        return (_process.ExitCode, await _stderr);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
        await _stderr;
        _process.Dispose();
    }

    public static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "SharpLensMcp.sln")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        dir.Should().NotBeNull("the repo root (containing SharpLensMcp.sln) must be findable from the test output directory");
        return dir!;
    }
}
