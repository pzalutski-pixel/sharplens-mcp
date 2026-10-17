using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;

namespace SharpLensMcp.Tests.Mcp;

// Drives tools through the JSON-RPC dispatcher of a fresh McpServer loaded with an
// in-memory workspace: the wire shape, System.Text.Json camelCase everywhere (data,
// meta.totalCount/returnedCount/truncated, error.code/message/hint/context).
internal sealed class InMemoryToolHost
{
    private readonly McpHarnessOps _ops;

    public InMemoryToolHost(Workspace workspace)
    {
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        _ops = new McpHarnessOps(server);
    }

    // The inner tool response (success + data/meta or error).
    public Task<JObject> CallAsync(string tool, object arguments) => _ops.CallToolAsync(tool, arguments);

    public async Task<JToken> DataAsync(string tool, object arguments)
    {
        var inner = await CallAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeTrue($"{tool} must succeed; got: {inner}");
        return inner["data"]!;
    }

    // The error object of a failed call, after asserting its code.
    public async Task<JToken> ErrorAsync(string tool, object arguments, string expectedCode)
    {
        var inner = await CallAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeFalse($"{tool} must fail; got: {inner}");
        inner["error"]!["code"].Should().NotBeNull("an error carries its code");
        inner["error"]!["code"]!.Value<string>().Should().Be(expectedCode, inner.ToString());
        return inner["error"]!;
    }
}
