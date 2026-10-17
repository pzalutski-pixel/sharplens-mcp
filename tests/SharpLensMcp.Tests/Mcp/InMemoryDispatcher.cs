using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;

namespace SharpLensMcp.Tests.Mcp;

// A fresh McpServer over an in-memory workspace, driven through the JSON-RPC dispatcher:
// what a client sees on the wire (camelCase, content[0].text unwrapped), without an
// MSBuild solution load.
internal sealed class InMemoryDispatcher
{
    private readonly McpHarnessOps _ops;

    public InMemoryDispatcher(Workspace workspace)
    {
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        _ops = new McpHarnessOps(server);
    }

    public Task<JObject> CallAsync(string tool, object arguments) => _ops.CallToolAsync(tool, arguments);

    // The data of a successful call.
    public async Task<JToken> DataAsync(string tool, object arguments)
    {
        var inner = await _ops.CallToolAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeTrue($"{tool} must succeed; got: {inner}");
        return inner["data"]!;
    }

    // The error object of a failed call, with its code asserted.
    public async Task<JToken> ErrorAsync(string tool, object arguments, string code)
    {
        var inner = await _ops.CallToolAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeFalse($"{tool} must fail; got: {inner}");
        var error = inner["error"]!;
        error["code"]!.Value<string>().Should().Be(code, inner.ToString());
        return error;
    }
}
