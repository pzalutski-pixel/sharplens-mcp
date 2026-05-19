using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

public class McpServerTests
{
    private readonly McpServer _server = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private JsonObject ParseResponse(object? response)
    {
        response.Should().NotBeNull();
        var json = JsonSerializer.Serialize(response, _jsonOptions);
        return JsonSerializer.Deserialize<JsonObject>(json)!;
    }

    [Fact]
    public async Task HandleRequest_WithIntegerId_EchoesIdBack()
    {
        var request = """{"jsonrpc":"2.0","id":42,"method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(42);
        response["jsonrpc"]!.GetValue<string>().Should().Be("2.0");
    }

    [Fact]
    public async Task HandleRequest_WithStringId_EchoesIdBack()
    {
        var request = """{"jsonrpc":"2.0","id":"abc-123","method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<string>().Should().Be("abc-123");
    }

    [Fact]
    public async Task HandleRequest_WithGuidId_EchoesIdBack()
    {
        var request = """{"jsonrpc":"2.0","id":"550e8400-e29b-41d4-a716-446655440000","method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<string>().Should().Be("550e8400-e29b-41d4-a716-446655440000");
    }

    [Fact]
    public async Task HandleRequest_Initialize_ReturnsProtocolVersionAndSemverServerVersion()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var result = response["result"]!.AsObject();
        result["protocolVersion"]!.GetValue<string>().Should().NotBeEmpty();
        result["serverInfo"]!.AsObject()["name"]!.GetValue<string>().Should().Be("SharpLensMcp");
        // The version must match a semver-like pattern (X.Y.Z), not the literal default
        // "1.0.0". Replaces a non-equality check that would pass for any other string.
        var version = result["serverInfo"]!.AsObject()["version"]!.GetValue<string>();
        version.Should().MatchRegex(@"^\d+\.\d+\.\d+",
            "serverInfo.version must be a semver-like X.Y.Z string");
    }

    [Fact]
    public async Task HandleRequest_ToolsList_ReturnsAllRegisteredTools()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var result = response["result"]!.AsObject();
        var tools = result["tools"]!.AsArray();
        // The dispatcher in McpServer.cs has 67 registered tool names; the list response
        // must surface every one of them. A lower count means a tool was forgotten in
        // HandleListToolsAsync; a higher count means a duplicate or stale entry.
        tools.Count.Should().Be(67, "the dispatcher registers exactly 67 tools as of 1.5.3");
        // Every tool entry must carry name + description, the contract for MCP clients.
        foreach (var tool in tools)
        {
            var t = tool!.AsObject();
            t["name"]!.GetValue<string>().Should().StartWith("roslyn:");
            t["description"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        }
        // Spot-check: a few representative tools across categories must appear.
        var names = tools.Select(t => t!.AsObject()["name"]!.GetValue<string>()).ToList();
        names.Should().Contain("roslyn:health_check");
        names.Should().Contain("roslyn:search_symbols");
        names.Should().Contain("roslyn:extract_method");
        names.Should().Contain("roslyn:get_code_actions_at_position");
    }

    [Fact]
    public async Task HandleRequest_UnknownMethod_ReturnsMethodNotFound()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"unknown/method"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32601);
        error["message"]!.GetValue<string>().Should().Contain("Method not found");
    }

    [Fact]
    public async Task HandleRequest_MissingMethod_ReturnsInvalidRequestWithMessage()
    {
        var request = """{"jsonrpc":"2.0","id":1}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32600);
        error["message"]!.GetValue<string>().Should().Contain("method",
            "the error message must mention the missing 'method' field");
    }

    [Fact]
    public async Task HandleRequest_InvalidJson_Returns32700ParseErrorWithNullId()
    {
        var request = "not valid json";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        // Per JSON-RPC 2.0 §5.1, malformed JSON must surface as -32700 Parse error.
        error["code"]!.GetValue<int>().Should().Be(-32700);
        error["message"]!.GetValue<string>().Should().Contain("Parse error");
        // §5: when the id field cannot be determined (parse failure), id MUST be null.
        // JsonObject indexing collapses missing-key and JSON-null to the same C# null,
        // so distinguish them via ContainsKey.
        response.ContainsKey("id").Should().BeTrue(
            "the envelope must always include the id key (per JSON-RPC §5)");
        response["id"].Should().BeNull(
            "parse-error responses must carry id=null, not an echoed bogus id");
    }

    [Fact]
    public async Task HandleRequest_RequestWithoutId_ReturnsNull()
    {
        // Per JSON-RPC 2.0 §4.3, any request without `id` is a Notification —
        // server must produce no response, even for non-`notifications/`-prefixed methods.
        var request = """{"jsonrpc":"2.0","method":"initialize"}""";
        var response = await _server.HandleRequestAsync(request);

        response.Should().BeNull();
    }

    [Fact]
    public async Task HandleRequest_ToolCallHealthCheckWithoutSolution_ReportsNotReady()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roslyn:health_check","arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(1);
        // tools/call returns result.content[0].text containing the tool's JSON envelope.
        var result = response["result"]!.AsObject();
        var content = result["content"]!.AsArray();
        content.Count.Should().BeGreaterThan(0, "tools/call always wraps tool output in a content array");
        var text = content[0]!.AsObject()["text"]!.GetValue<string>();
        // health_check without a loaded solution reports status "Not Ready" per RoslynService.cs:474-480.
        text.Should().Contain("\"status\":\"Not Ready\"",
            "an unloaded solution must surface as Not Ready, not as an error");
        text.Should().Contain("load_solution",
            "the message must point the caller at load_solution");
    }

    [Fact]
    public async Task HandleRequest_NotificationInitialized_ReturnsNull()
    {
        var request = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
        var response = await _server.HandleRequestAsync(request);

        response.Should().BeNull();
    }

    [Fact]
    public async Task HandleRequest_NotificationWithNoId_ReturnsNull()
    {
        var request = """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"abc"}}""";
        var response = await _server.HandleRequestAsync(request);

        response.Should().BeNull();
    }

    [Fact]
    public async Task HandleRequest_NotificationThatTriggersInternalException_ReturnsNull()
    {
        // tools/call expects params.name to be a string; passing a non-object `params`
        // (here a string literal) trips `paramsNode?.AsObject()` and throws
        // InvalidOperationException — that exception escapes the dispatcher and
        // hits the outer catch. With NO `id` (notification), per JSON-RPC 2.0
        // §4.3, the response MUST be null even on internal error.
        var request = """{"jsonrpc":"2.0","method":"tools/call","params":"not-an-object"}""";
        var response = await _server.HandleRequestAsync(request);

        response.Should().BeNull(
            "notifications must produce no response, even when an exception escapes dispatch");
    }

    [Fact]
    public async Task HandleRequest_RequestThatTriggersInternalException_ReturnsErrorWithId()
    {
        // Same shape but WITH an id — must produce a structured -32603 error.
        var request = """{"jsonrpc":"2.0","id":42,"method":"tools/call","params":"not-an-object"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(42);
        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32603);
    }

    [Fact]
    public async Task HandleRequest_ToolsCall_UnknownToolName_Returns32602InvalidParams()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roslyn:does_not_exist_xyz","arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(1);
        var error = response["error"]!.AsObject();
        // Unknown tool maps to -32602 (Invalid params) per the dispatcher contract;
        // the message must mention "Unknown tool" and echo the bad name.
        error["code"]!.GetValue<int>().Should().Be(-32602);
        error["message"]!.GetValue<string>().Should().Contain("Unknown tool");
        error["message"]!.GetValue<string>().Should().Contain("roslyn:does_not_exist_xyz");
    }

    [Fact]
    public async Task HandleRequest_ToolsCall_MissingParamsName_Returns32602InvalidParams()
    {
        // tools/call requires params.name. Missing it must surface as -32602.
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32602);
        error["message"]!.GetValue<string>().Should().Contain("missing tool name",
            "the dispatcher must point at the missing tool name specifically");
    }

    [Fact]
    public async Task HandleRequest_ToolsCall_SyncDocumentsWithNonArrayFilePaths_Returns32602()
    {
        // sync_documents takes filePaths as a JSON array of strings. A scalar
        // value passed instead must surface as -32602 with the canonical "must
        // be an array of strings" wording. The prior ParseStringArray helper
        // silently treated non-array as null → sync_documents synced ALL files,
        // hiding the caller's mistake. Now routed through p.OptionalStringArray
        // which validates shape.
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roslyn:sync_documents","arguments":{"filePaths":"not-an-array"}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32602);
        error["message"]!.GetValue<string>().Should().Contain("filePaths");
        error["message"]!.GetValue<string>().Should().Contain("must be an array of strings");
    }

    [Fact]
    public async Task HandleRequest_ToolsCall_SyncDocumentsWithNonStringElement_Returns32602()
    {
        // The element-level type check: filePaths must contain only strings.
        // A number in the array (e.g., {"filePaths":[42]}) previously triggered
        // an uncaught System.Text.Json.JsonException via ParseStringArray, which
        // the outer catch wrapped as -32603 Internal error. After replacing the
        // helper with p.OptionalStringArray, it surfaces as the structured
        // -32602 with the canonical wording.
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roslyn:sync_documents","arguments":{"filePaths":[42]}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32602,
            "non-string element must produce -32602, not -32603 Internal error");
        error["message"]!.GetValue<string>().Should().Contain("filePaths");
        error["message"]!.GetValue<string>().Should().Contain("must be an array of strings");
    }

    [Fact]
    public async Task HandleRequest_ToolsCall_MissingArgumentsObject_HealthCheckStillWorks()
    {
        // params.arguments is optional — for tools with no required params (like
        // health_check), omitting it must not be an error. The dispatcher's
        // JsonRpcParameters(args: null) constructor handles this (verified separately
        // in JsonRpcParametersTests).
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roslyn:health_check"}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(1);
        response["error"].Should().BeNull("missing arguments must not produce an error for parameterless tools");
        var content = response["result"]!.AsObject()["content"]!.AsArray();
        content.Count.Should().BeGreaterThan(0);
    }
}
