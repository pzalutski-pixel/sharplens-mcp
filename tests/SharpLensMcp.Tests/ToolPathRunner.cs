using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;

namespace SharpLensMcp.Tests;

// Runs one tool call at either level of ToolPath over one in-memory workspace, so a test
// states its expected answer once and asserts it at both levels (testing charter C4):
//   - Service: the RoslynService method, its response read with Newtonsoft;
//   - Dispatcher: tools/call through McpServer's JSON-RPC dispatcher, content[0].text
//     parsed as the client sees it.
// Both responses are returned in the wire's casing: the in-process envelope's
// PascalCase meta (TotalCount, ReturnedCount, Truncated) and error (Code, Message, Hint,
// Context) keys are renamed to camelCase, so one assertion reads both. data is never
// renamed: its anonymous-type keys are camelCase at both levels already.
internal sealed class ToolPathRunner
{
    private readonly ToolPath _path;
    private readonly RoslynService _service;
    private readonly McpHarnessOps? _dispatcher;

    public ToolPathRunner(ToolPath path, Workspace workspace)
    {
        _path = path;
        if (path == ToolPath.Service)
        {
            _service = new RoslynService();
            _service.LoadFromWorkspaceForTesting(workspace);
        }
        else
        {
            var server = new McpServer();
            server.LoadFromWorkspaceForTesting(workspace);
            _dispatcher = new McpHarnessOps(server);
            _service = null!;
        }
    }

    // The whole response envelope { success, data | error, meta } in wire casing.
    public async Task<JObject> CallAsync(string tool, object arguments, Func<RoslynService, Task<object>> serviceCall)
    {
        if (_path == ToolPath.Dispatcher) return await _dispatcher!.CallToolAsync(tool, arguments);

        var envelope = JObject.FromObject(await serviceCall(_service));
        Camelize(envelope, "meta");
        Camelize(envelope, "error");
        return envelope;
    }

    // The data of a call that must succeed.
    public async Task<JToken> DataAsync(string tool, object arguments, Func<RoslynService, Task<object>> serviceCall)
    {
        var envelope = await CallAsync(tool, arguments, serviceCall);
        envelope["success"].Should().NotBeNull("every response envelope carries success");
        envelope["success"]!.Value<bool>().Should().BeTrue($"{tool} must succeed at the {_path} level; got: {envelope}");
        return envelope["data"]!;
    }

    // The error of a call that must fail.
    public async Task<JToken> ErrorAsync(string tool, object arguments, Func<RoslynService, Task<object>> serviceCall)
    {
        var envelope = await CallAsync(tool, arguments, serviceCall);
        envelope["success"].Should().NotBeNull("every response envelope carries success");
        envelope["success"]!.Value<bool>().Should().BeFalse($"{tool} must fail at the {_path} level; got: {envelope}");
        envelope["error"].Should().NotBeNull("a failed call carries an error");
        return envelope["error"]!;
    }

    private static void Camelize(JObject envelope, string key)
    {
        if (envelope[key] is not JObject section) return;
        var renamed = new JObject();
        foreach (var property in section.Properties())
        {
            renamed[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.Value;
        }
        envelope[key] = renamed;
    }
}
