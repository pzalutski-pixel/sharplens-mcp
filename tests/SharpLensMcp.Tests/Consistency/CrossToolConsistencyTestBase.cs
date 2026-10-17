using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;

namespace SharpLensMcp.Tests.Consistency;

// Base for cross-tool consistency tests: the same question asked through different
// tools must get the same answer. A test loads one deterministic in-memory workspace
// (RetargetedSolutionFixture, MultiTargetSolutionFixture, TestHelpers) into a fresh
// server, asks each tool THROUGH THE JSON-RPC DISPATCHER (the wire shape, camelCase),
// projects each answer onto a common normalized form with the helpers below, and
// asserts the projections are equal — and equal to the exact expected answer, so two
// tools agreeing on a wrong answer still fails.
//
// Normalized forms:
//   - Locations: "path:line:column" strings in the canonical location order
//     (ResultLocationComparer), from any array of results carrying a location object
//     { filePath, line, column };
//   - Names: a sorted, distinct set of strings projected from any array of results.
public abstract class CrossToolConsistencyTestBase
{
    private McpHarnessOps? _dispatcher;

    // Loads the workspace into a fresh server; later calls in the test go to it.
    protected void Load(Workspace workspace)
    {
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        _dispatcher = new McpHarnessOps(server);
    }

    private McpHarnessOps Dispatcher =>
        _dispatcher ?? throw new InvalidOperationException("Call Load(workspace) before calling a tool");

    // Calls a tool and returns its data, asserting the call succeeded.
    protected async Task<JToken> CallDataAsync(string tool, object arguments)
    {
        var inner = await Dispatcher.CallToolAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeTrue($"{tool} must succeed; got: {inner}");
        return inner["data"]!;
    }

    // Calls a tool expecting the dispatcher to reject the arguments with -32602 Invalid
    // params, and returns the error message.
    protected async Task<string> CallInvalidParamsAsync(string tool, object arguments)
    {
        try
        {
            var inner = await Dispatcher.CallToolAsync(tool, arguments);
            throw new Xunit.Sdk.XunitException($"Expected JSON-RPC -32602 from '{tool}' but the tool returned: {inner}");
        }
        catch (McpJsonRpcException ex)
        {
            ex.Code.Should().Be(-32602, $"envelope: {ex.Envelope}");
            return ex.Envelope["error"]!["message"]!.Value<string>()!;
        }
    }

    // "path:line:column" of each item's location, in canonical location order.
    protected static List<string> Locations(IEnumerable<JToken> items, Func<JToken, JToken> locationOf) =>
        CanonicalOrder.ByLocation(
                items.Select(locationOf).Select(l => new ResultLocation(
                    l["filePath"]!.Value<string>()!, l["line"]!.Value<int>(), l["column"]!.Value<int>())),
                l => l)
            .Select(l => $"{l.FilePath.Replace('\\', '/')}:{l.Line}:{l.Column}")
            .ToList();

    // The distinct projected names, ordinally sorted.
    protected static List<string> Names(IEnumerable<JToken> items, Func<JToken, string> nameOf) =>
        items.Select(nameOf).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
}
