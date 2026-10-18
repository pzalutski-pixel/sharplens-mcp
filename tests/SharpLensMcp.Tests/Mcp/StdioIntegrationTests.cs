using System.Diagnostics;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// Out-of-process integration test: spawns the ACTUAL server binary and drives
// real JSON-RPC over stdin/stdout, the way an MCP client consumes it. The
// in-process ViaMcp tests cannot cover this layer — wire serialization,
// stdout purity (every stdout line must be a JSON-RPC message, logs must go
// to stderr), startup solution auto-load via DOTNET_SOLUTION_PATH, and clean
// shutdown on EOF all only exist for a real process.
public class StdioIntegrationTests
{
    // One server process, one protocol session, all contract steps in order —
    // the solution auto-load is paid once.
    [Fact]
    public async Task RealServerOverStdio_FullSessionContract()
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
            WorkingDirectory = repoRoot
        };
        psi.Environment["DOTNET_SOLUTION_PATH"] = Path.Combine(repoRoot, "SharpLensMcp.sln");
        // Match the in-process fixture's generous per-call budget (300s): this runs the
        // real binary over the full SharpLens solution, where the same heavy tools
        // (analyzer-running get_diagnostics, full-solution coverage BFS) can exceed the
        // 30s default and flake with TIMEOUT under CI contention. Child-process env only.
        psi.Environment["SHARPLENS_TIMEOUT_SECONDS"] = "300";
        DotnetChildProcess.Isolated(psi);

        using var process = Process.Start(psi)!;
        // Drain stderr concurrently — the server logs there, and an undrained
        // pipe fills up and deadlocks the process.
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            // 1. initialize: version negotiation + serverInfo, and the version on
            // the wire must match the assembly this test suite was built against.
            var init = await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdio-test","version":"1"} } }""");
            init["result"]!["protocolVersion"]!.Value<string>().Should().Be("2025-06-18",
                "a supported client version must be echoed back");
            init["result"]!["serverInfo"]!["name"]!.Value<string>().Should().Be("SharpLensMcp");
            var expectedVersion = typeof(McpServer).Assembly.GetName().Version!.ToString(3);
            init["result"]!["serverInfo"]!["version"]!.Value<string>().Should().Be(expectedVersion,
                "the spawned binary must be the same version this suite was built against");

            // 2. tools/list: full registry over the wire, MCP-compliant names.
            var list = await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
            var tools = (list["result"]!["tools"] as JArray)!;
            tools.Count.Should().Be(ToolRegistry.Definitions.Count, "every registered tool must be published over the wire");
            foreach (var tool in tools)
            {
                tool["name"]!.Value<string>().Should().MatchRegex("^[a-zA-Z0-9_-]{1,64}$",
                    "wire-published names must satisfy the MCP tool-name pattern");
            }

            // 2b. Golden DeepEquals over the REAL stdio pipe (not just in-process):
            // the wire tool registry must match the committed snapshot exactly. Verbatim
            // multi-line descriptions follow the checkout's line endings, so normalize the
            // escaped CRLF in both payloads (as the in-process golden test does) while every
            // other byte still must match. DeepEquals on JObjects is property-order-insensitive.
            var goldenPath = Path.Combine(AppContext.BaseDirectory, "Mcp", "ToolsListGolden.json");
            File.Exists(goldenPath).Should().BeTrue(
                "the golden asset must be copied to the test output directory");
            var goldenTools = JToken.Parse(
                File.ReadAllText(goldenPath).Trim().Replace("\\r\\n", "\\n"))["result"]!["tools"]!;
            var liveTools = JToken.Parse(tools.ToString().Replace("\\r\\n", "\\n"));
            JToken.DeepEquals(liveTools, goldenTools).Should().BeTrue(
                "the tools/list response over the real stdio pipe must match the golden snapshot exactly; " +
                $"live count {liveTools.Count()}, golden count {goldenTools.Count()}");

            // 3. health_check: DOTNET_SOLUTION_PATH auto-load must have worked. The load runs
            // beside the request loop; a tools/call waits for it to finish.
            var health = ParseInner(await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"health_check","arguments":{} } }"""));
            health["success"]!.Value<bool>().Should().BeTrue($"health_check must succeed; got {health}");
            health["data"]!["solution"]!["loaded"]!.Value<bool>().Should().BeTrue(
                "the first tools/call must see the solution auto-loaded from DOTNET_SOLUTION_PATH");
            health["data"]!["solution"]!["projects"]!.Value<int>().Should().Be(3);

            // 4. A representative query tool end-to-end.
            var search = ParseInner(await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"search_symbols","arguments":{"query":"RoslynService","kind":"Class"} } }"""));
            search["success"]!.Value<bool>().Should().BeTrue($"search_symbols must succeed; got {search}");
            (search["data"]!["results"] as JArray)!.Should().NotBeEmpty(
                "the loaded solution declares the RoslynService class");

            // 5. A preview refactoring end-to-end — and a byte-for-byte proof that
            // preview writes nothing to disk (the claim was prose-only before).
            var targetFile = Path.Combine(repoRoot, "src", "RoslynService.cs");
            var beforeBytes = await File.ReadAllBytesAsync(targetFile);
            var organize = ParseInner(await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"organize_usings","arguments":{"filePath":"src/RoslynService.cs","preview":true} } }"""));
            organize["success"]!.Value<bool>().Should().BeTrue($"organize_usings preview must succeed; got {organize}");
            organize["data"]!["organizedText"]!.Value<string>().Should().Contain("using System",
                "the preview must carry the organized source text");
            (await File.ReadAllBytesAsync(targetFile)).Should().Equal(beforeBytes,
                "a preview refactoring must not write a single byte to disk");

            // 6. One value-pinned tool PER CATEGORY over the real pipe (not just the
            // session contract): each asserts a concrete fixture value, so the whole
            // dispatch+serialization path is proven for a representative of every
            // category, not merely "it returned something".

            // Audit — find_god_objects on the engineered fixture.
            var god = ParseInner(await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"find_god_objects","arguments":{"projectName":"SharpLensMcp.Tests","minEfferentCoupling":20,"minMembers":20} } }"""));
            god["success"]!.Value<bool>().Should().BeTrue($"find_god_objects must succeed; got {god}");
            (god["data"]!["candidates"] as JArray)!
                .First(c => c["typeName"]!.Value<string>()!.EndsWith("GodObjectFixtureClass"))
                ["efferentCoupling"]!.Value<int>().Should().Be(25,
                    "GodObjectFixtureClass has 25 distinct type dependencies");

            // CodeGeneration — generate_test_stub detects xUnit and names the test class.
            var stub = ParseInner(await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"generate_test_stub","arguments":{"typeName":"TestStubTarget","methodName":"Add"} } }"""));
            stub["success"]!.Value<bool>().Should().BeTrue($"generate_test_stub must succeed; got {stub}");
            stub["data"]!["framework"]!.Value<string>().Should().Be("xunit");
            stub["data"]!["testClassName"]!.Value<string>().Should().Be("TestStubTargetTests");

            // ExternalApi — get_external_type_info resolves a BCL type.
            var ext = ParseInner(await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"get_external_type_info","arguments":{"typeName":"System.String"} } }"""));
            ext["success"]!.Value<bool>().Should().BeTrue($"get_external_type_info must succeed; got {ext}");
            ext["data"]!["typeName"]!.Value<string>().Should().Be("string");
            ext["data"]!["typeKind"]!.Value<string>().Should().Be("Class");

            // Compound — get_type_overview on a known source type.
            var overview = ParseInner(await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_type_overview","arguments":{"typeName":"RoslynService"} } }"""));
            overview["success"]!.Value<bool>().Should().BeTrue($"get_type_overview must succeed; got {overview}");
            overview["data"]!["typeKind"]!.Value<string>().Should().Be("Class");

            // Discovery — find_unused_dependencies locks the src package count.
            var deps = ParseInner(await RoundTripAsync(process,
                """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"find_unused_dependencies","arguments":{"projectName":"SharpLensMcp"} } }"""));
            deps["success"]!.Value<bool>().Should().BeTrue($"find_unused_dependencies must succeed; got {deps}");
            (deps["data"]!["projects"] as JArray)![0]["packagesChecked"]!.Value<int>().Should().Be(8,
                "the src csproj declares exactly eight PackageReference entries");

            // 7. EOF on stdin → clean shutdown.
            process.StandardInput.Close();
            var exited = process.WaitForExit(30_000);
            exited.Should().BeTrue("the server must shut down when stdin closes");
            process.ExitCode.Should().Be(0, "EOF shutdown is the normal exit path");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            // Surface server logs on failure diagnostics without failing on content.
            await stderrTask;
        }
    }

    // Writes one request line and reads exactly one response line, asserting
    // stdout purity: the line must parse as a JSON-RPC envelope. The first
    // tools/call absorbs the startup solution load, so the read timeout is
    // generous for cold CI machines.
    private static async Task<JObject> RoundTripAsync(Process process, string requestJson)
    {
        await process.StandardInput.WriteLineAsync(requestJson);
        await process.StandardInput.FlushAsync();

        var readTask = process.StandardOutput.ReadLineAsync();
        // Must exceed the child server's 300s (5-minute) per-call budget (SHARPLENS_TIMEOUT_SECONDS),
        // so a slow heavy call under CI contention loses to the server's own in-band TIMEOUT
        // rather than to this client deadline.
        var winner = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromMinutes(6)));
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

    // Unwraps result.content[0].text into the structured tool envelope.
    private static JObject ParseInner(JObject envelope)
    {
        envelope["error"].Should().BeNull($"tools/call must not fail at the JSON-RPC layer; got {envelope["error"]}");
        var text = envelope["result"]!["content"]![0]!["text"]!.Value<string>()!;
        return JObject.Parse(text);
    }

    private static string FindRepoRoot()
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
