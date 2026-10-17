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

    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2025-03-26")]
    [InlineData("2025-06-18")]
    public async Task HandleRequest_Initialize_EchoesSupportedClientProtocolVersion(string clientVersion)
    {
        var request = $$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"{{clientVersion}}","capabilities":{},"clientInfo":{"name":"t","version":"1"} } }""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["result"]!.AsObject()["protocolVersion"]!.GetValue<string>().Should().Be(clientVersion,
            "a supported client version must be echoed back, per MCP version negotiation");
    }

    [Fact]
    public async Task HandleRequest_Initialize_UnsupportedClientVersion_AnswersWithLatestSupported()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1999-01-01"}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["result"]!.AsObject()["protocolVersion"]!.GetValue<string>().Should().Be("2025-06-18",
            "an unsupported requested version gets the server's latest supported version");
    }

    [Fact]
    public async Task HandleRequest_Initialize_WithoutParams_AnswersWithLatestSupported()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["result"]!.AsObject()["protocolVersion"]!.GetValue<string>().Should().Be("2025-06-18");
    }

    [Theory]
    [InlineData("Trace", "Trace", true)]
    [InlineData("Debug", "Trace", true)]
    [InlineData("Trace", "Debug", false)]
    [InlineData("Information", "Information", true)]
    [InlineData("Warning", "Error", false)]
    [InlineData("Error", "Error", true)]
    public void ShouldLog_ComparesAgainstTheConfiguredLevel(string message, string configured, bool expected)
    {
        McpServer.ShouldLog(message, configured).Should().Be(expected);
    }

    [Fact]
    public void ShouldLog_UnknownConfiguredLevel_FallsBackToInformation()
    {
        // Array.IndexOf's -1 used to make any unrecognized configured level
        // (including "Trace" before it existed) log EVERYTHING.
        McpServer.ShouldLog("Debug", "garbage-level").Should().BeFalse(
            "an unknown configured level must behave like Information, not log everything");
        McpServer.ShouldLog("Information", "garbage-level").Should().BeTrue();
    }

    [Fact]
    public async Task ToolCall_WithAlreadyCancelledBudget_ReturnsInBandTimeoutErrorDeterministically()
    {
        // Forced-cancellation seam (no timing race): a PRE-cancelled token is linked
        // into every per-call CTS, so the pre-dispatch ThrowIfCancellationRequested
        // yields TIMEOUT regardless of how fast the tool is — unlike the old 1 ms
        // budget vs a slow MSBuild load. The 5-minute budget proves it is the seam
        // cancelling, not the timer. The failure must surface in-band with the TIMEOUT
        // code — a tool failure the model can react to, not a protocol error.
        using var precancelled = new CancellationTokenSource();
        precancelled.Cancel();
        var server = new McpServer(TimeSpan.FromMinutes(5), precancelled.Token);
        var request = """{"jsonrpc":"2.0","id":77,"method":"tools/call","params":{"name":"health_check","arguments":{} } }""";

        var response = ParseResponse(await server.HandleRequestAsync(request));

        response["error"].Should().BeNull("timeouts are tool failures, not JSON-RPC errors");
        response["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        var inner = JsonSerializer.Deserialize<JsonObject>(
            response["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        inner["success"]!.GetValue<bool>().Should().BeFalse();
        inner["error"]!["code"]!.GetValue<string>().Should().Be("TIMEOUT");
        inner["error"]!["hint"]!.GetValue<string>().Should().Contain("SHARPLENS_TIMEOUT_SECONDS",
            "the hint must name the variable that raises the limit");
    }

    [Fact]
    public async Task ToolCall_WithUncancelledBudget_SucceedsWithoutFalseTimeout()
    {
        // The forced-cancellation seam stays inert when its token is never cancelled:
        // a generous, uncancelled budget must let the tool run to completion. Guards
        // against the pre-dispatch check or the linked CTS firing spuriously.
        var server = new McpServer(TimeSpan.FromMinutes(5));
        var request = """{"jsonrpc":"2.0","id":78,"method":"tools/call","params":{"name":"health_check","arguments":{} } }""";

        var response = ParseResponse(await server.HandleRequestAsync(request));

        response["error"].Should().BeNull();
        var inner = JsonSerializer.Deserialize<JsonObject>(
            response["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        inner["success"]!.GetValue<bool>().Should().BeTrue(
            "an uncancelled, generous budget must not fail a tool");
    }

    [Fact]
    public async Task HandleRequest_ToolsList_EveryToolCarriesAnnotationsMatchingItsMutability()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));
        var tools = response["result"]!.AsObject()["tools"]!.AsArray();

        // Derive the expected mutating set from the registry's ReadOnly flag — the
        // source of truth — rather than hand-maintaining a parallel list that drifts.
        // The cross-check is then wire annotation vs registry flag, not list vs list.
        var mutatingTools = ToolRegistry.Definitions
            .Where(d => !d.ReadOnly)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.Ordinal);
        mutatingTools.Should().NotBeEmpty("the registry declares mutating tools");

        foreach (var tool in tools)
        {
            var name = tool!["name"]!.GetValue<string>();
            var annotations = tool["annotations"]!.AsObject();
            var readOnly = annotations["readOnlyHint"]!.GetValue<bool>();

            if (mutatingTools.Contains(name))
            {
                readOnly.Should().BeFalse($"{name} mutates the workspace or disk");
                annotations["destructiveHint"]!.GetValue<bool>().Should().BeTrue(
                    $"{name} mutates workspace or disk state, which is destructive per MCP semantics");
            }
            else
            {
                readOnly.Should().BeTrue($"{name} performs no mutation");
                annotations["destructiveHint"].Should().BeNull(
                    "destructiveHint is meaningless on a read-only tool and must be omitted");
            }
        }
    }

    [Fact]
    public async Task HandleRequest_ToolsList_EveryNameMatchesTheMcpToolNamePattern()
    {
        // MCP requires ^[a-zA-Z0-9_-]{1,64}$ — the 1.5.x "roslyn:" prefix
        // violated it (colon). The published names must all comply.
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var tools = response["result"]!.AsObject()["tools"]!.AsArray();
        foreach (var tool in tools)
        {
            tool!["name"]!.GetValue<string>().Should().MatchRegex("^[a-zA-Z0-9_-]{1,64}$",
                "every published tool name must satisfy the MCP name pattern");
        }
    }

    [Fact]
    public async Task HandleRequest_ToolCall_AcceptsLegacyPrefixedNameAsAlias()
    {
        // 1.5.x clients call "roslyn:health_check"; the prefixed spelling stays
        // accepted as an alias for one release.
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"roslyn:health_check","arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"].Should().BeNull("the legacy alias must dispatch, not error");
        var text = response["result"]!.AsObject()["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
        var inner = JsonSerializer.Deserialize<JsonObject>(text)!;
        inner["success"]!.GetValue<bool>().Should().BeTrue(
            "health_check succeeds without a loaded solution");
    }

    [Fact]
    public async Task HandleRequest_ToolsList_ReturnsAllRegisteredTools()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var result = response["result"]!.AsObject();
        var tools = result["tools"]!.AsArray();
        // The list response must surface every registry record. A lower count
        // means a tool was forgotten in the projection; a higher count means a
        // duplicate or stale entry. The absolute count is pinned once, in
        // ToolsListGoldenTests.
        tools.Count.Should().Be(ToolRegistry.Definitions.Count, "tools/list must surface every registry record");
        // Every tool entry must carry name + description, the contract for MCP clients.
        foreach (var tool in tools)
        {
            var t = tool!.AsObject();
            t["name"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            t["description"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        }
        // Spot-check: a few representative tools across categories must appear.
        var names = tools.Select(t => t!.AsObject()["name"]!.GetValue<string>()).ToList();
        names.Should().Contain("health_check");
        names.Should().Contain("search_symbols");
        names.Should().Contain("extract_method");
        names.Should().Contain("get_code_actions_at_position");
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
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(1);
        // tools/call returns result.content[0].text containing the tool's JSON envelope.
        var result = response["result"]!.AsObject();
        var content = result["content"]!.AsArray();
        content.Count.Should().BeGreaterThan(0, "tools/call always wraps tool output in a content array");
        result["isError"].Should().BeNull("a successful tool result must not carry isError");
        var text = content[0]!.AsObject()["text"]!.GetValue<string>();
        // health_check without a loaded solution reports status "Not Ready".
        text.Should().Contain("\"status\":\"Not Ready\"",
            "an unloaded solution must surface as Not Ready, not as an error");
        text.Should().Contain("load_solution",
            "the message must point the caller at load_solution");
        // Server configuration does not depend on a loaded solution, so it is reported
        // before the first load too (the test process sets none of the variables).
        var inner = JsonSerializer.Deserialize<JsonObject>(text)!;
        inner["data"]!["configuration"]!.ToJsonString().Should().Be(
            """{"maxDiagnostics":100,"timeoutSeconds":30,"semanticCacheEnabled":true,"absolutePaths":false}""");
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
    public async Task HandleRequest_IdlessToolCall_ReturnsNullWithoutDispatching()
    {
        // A tools/call with NO id is a Notification (JSON-RPC 2.0 §4.3): the server must
        // return null WITHOUT dispatching — the notification guard short-circuits before
        // any handler runs. The malformed `params` (a string, not an object) would throw
        // if dispatch were ever reached, so a null response — not an error — confirms the
        // request was never dispatched. (The no-side-effect half is locked by
        // HandleRequest_MutatingToolCallAsNotification_DoesNotExecuteTheTool.)
        var request = """{"jsonrpc":"2.0","method":"tools/call","params":"not-an-object"}""";
        var response = await _server.HandleRequestAsync(request);

        response.Should().BeNull(
            "an id-less request is a notification and must be neither dispatched nor answered");
    }

    [Fact]
    public async Task HandleRequest_MutatingToolCallAsNotification_DoesNotExecuteTheTool()
    {
        // A tools/call without `id` is a notification. The server must NOT dispatch it,
        // because request methods require a response and dispatching would run a
        // possibly mutating tool. load_solution mutates server state, so prove it never
        // ran: the solution must still be unloaded afterward. When the guard works no
        // load happens (cheap); only a regression would actually load and fail here.
        var slnDir = Directory.GetCurrentDirectory();
        while (slnDir != null && !File.Exists(Path.Combine(slnDir, "SharpLensMcp.sln")))
            slnDir = Directory.GetParent(slnDir)?.FullName;
        slnDir.Should().NotBeNull("the test must locate the real solution to attempt a load");

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "load_solution",
                ["arguments"] = new JsonObject
                {
                    ["solutionPath"] = Path.Combine(slnDir!, "SharpLensMcp.sln")
                }
            }
        };
        var notifResponse = await _server.HandleRequestAsync(notification.ToJsonString());
        notifResponse.Should().BeNull("a notification produces no response");

        // health_check reflects load state: still Not Ready means load_solution never ran.
        var check = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check","arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(check));
        var text = response["result"]!["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
        text.Should().Contain("\"status\":\"Not Ready\"",
            "load_solution sent as a notification must not have loaded the solution");
    }

    [Fact]
    public async Task HandleRequest_ToolRequiringSolution_WithoutSolution_ResultIsError()
    {
        // search_symbols calls EnsureSolutionLoaded, which throws SolutionNotLoadedException
        // with no solution. That surfaces as an in-band CallToolResult whose envelope is
        // {success:false}; it must carry isError:true like every other tool error so the
        // model treats it as a failed call.
        var request = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"search_symbols","arguments":{"query":"X"}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var result = response["result"]!.AsObject();
        result["isError"]!.GetValue<bool>().Should().BeTrue(
            "an in-band SOLUTION_NOT_LOADED result must set isError, matching every other tool error");
        var text = result["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
        text.Should().Contain("SOLUTION_NOT_LOADED",
            "the envelope must carry the structured error code");
    }

    [Fact]
    public async Task HandleRequest_ToolReturningErrorEnvelope_ResultIsError()
    {
        // load_solution with a missing path RETURNS a {success:false} envelope (it does
        // not throw), so isError must be set by the value-return path — not by a catch.
        var request = """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"load_solution","arguments":{"solutionPath":"/does/not/exist/Nope.sln"}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        var result = response["result"]!.AsObject();
        result["isError"]!.GetValue<bool>().Should().BeTrue(
            "a tool that RETURNS a {success:false} envelope must surface isError, like the exception paths");
        var text = result["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>();
        text.Should().Contain("FILE_NOT_FOUND", "the envelope must carry the structured error code");
    }

    [Fact]
    public async Task HandleRequest_ApplyToolIntroducingCompileError_ResultIsErrorWithPostApplyErrors()
    {
        // An apply tool that emits broken code reports success:true/applied:true but lists
        // the new errors in data.postApplyErrors; the wrapper must still flag the result
        // isError:true so a client keying on isError sees the broken state. Driven through a
        // fresh server loaded with an in-memory workspace so nothing touches disk.
        const string code = @"
namespace Demo
{
    public class C
    {
        public int Add(int amount) => amount + 1;
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);

        // Removing a body-used parameter leaves the body referencing it (CS0103).
        var request = """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"change_signature","arguments":{"typeName":"C","memberName":"Add","preview":false,"changes":[{"action":"remove","name":"amount"}]}}}""";
        var response = ParseResponse(await server.HandleRequestAsync(request));

        var result = response["result"]!.AsObject();
        result["isError"]!.GetValue<bool>().Should().BeTrue(
            "an applied edit that introduced compile errors must flag isError even though it applied");

        var inner = JsonSerializer.Deserialize<JsonObject>(
            result["content"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>())!;
        inner["success"]!.GetValue<bool>().Should().BeTrue(
            "the surface contract still applies the edit (success:true), it does not refuse");
        inner["data"]!["applied"]!.GetValue<bool>().Should().BeTrue("the edit was applied");
        inner["data"]!["postApplyErrors"]!.AsArray()
            .Select(e => e!.GetValue<string>())
            .Should().ContainSingle(s => s.EndsWith(" (line 5) CS0103: The name 'amount' does not exist in the current context"),
                "the body still references the removed parameter 'amount'");
    }

    [Fact]
    public async Task HandleRequest_MalformedNestedArrayField_ReturnsInvalidParams()
    {
        // change_signature's changes[0].position is a string; the nested-array parser
        // must surface a present-but-wrong-type field as a structured -32602, not an
        // internal -32603.
        var request = """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"change_signature","arguments":{"typeName":"X","memberName":"Y","changes":[{"action":"addParameter","name":"z","type":"int","position":"not-an-int"}]}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32602,
            "a present-but-wrong-type field in a nested array element is invalid params, not an internal error");
        var message = response["error"]!["message"]!.GetValue<string>();
        message.Should().Contain("position");
        message.Should().Contain("change_signature",
            "the nested-array error names the tool, like the scalar -32602 messages");
    }

    [Fact]
    public async Task HandleRequest_MalformedArchitectureRuleField_ReturnsInvalidParams()
    {
        // check_architecture's rules[0].from is a number; the nested-array parser must
        // surface the wrong-type field as a structured -32602 naming the tool.
        var request = """{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"check_architecture","arguments":{"rules":[{"from":123,"deny":"X"}]}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        var message = response["error"]!["message"]!.GetValue<string>();
        message.Should().Contain("from");
        message.Should().Contain("check_architecture");
    }

    [Fact]
    public async Task HandleRequest_MalformedOrderArrayElement_ReturnsInvalidParams()
    {
        // change_signature's changes[0].order[0] is a number; the ElementString parser
        // must surface the wrong-type element as a -32602 naming the tool and the index.
        var request = """{"jsonrpc":"2.0","id":24,"method":"tools/call","params":{"name":"change_signature","arguments":{"typeName":"X","memberName":"Y","changes":[{"action":"reorder","order":[123]}]}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        var message = response["error"]!["message"]!.GetValue<string>();
        message.Should().Contain("order[0]");
        message.Should().Contain("change_signature");
    }

    [Fact]
    public async Task HandleRequest_MalformedMethodBatchField_ReturnsInvalidParams()
    {
        // get_method_source_batch's methods[0].overloadIndex is a string; the parser must
        // surface the wrong-type field as a structured -32602 naming the tool and index.
        var request = """{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"get_method_source_batch","arguments":{"methods":[{"typeName":"X","methodName":"Y","overloadIndex":"not-an-int"}]}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        var message = response["error"]!["message"]!.GetValue<string>();
        message.Should().Contain("overloadIndex");
        message.Should().Contain("get_method_source_batch");
    }

    [Fact]
    public async Task HandleRequest_NonStringToolName_ReturnsInvalidParams()
    {
        // params.name is a number: read defensively (not GetValue<string>() that throws
        // into the generic catch as an in-band INTERNAL_ERROR) so it falls into the
        // missing-tool-name -32602 path with the existing message preserved.
        var request = """{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":123,"arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        response["error"]!["message"]!.GetValue<string>().Should().Contain("missing tool name");
    }

    [Fact]
    public async Task HandleRequest_NonObjectArguments_ReturnsInvalidParams()
    {
        // params.arguments is a string: a present-but-wrong-typed value is invalid params
        // (-32602), not an exception that escapes to an in-band INTERNAL_ERROR.
        var request = """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"health_check","arguments":"not-an-object"}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
        response["error"]!["message"]!.GetValue<string>().Should().Contain("arguments");
    }

    [Fact]
    public async Task HandleRequest_NonStringMethod_ReturnsInvalidRequest()
    {
        // A non-string method (here a number) is an Invalid Request (-32600), not an
        // internal error (-32603) from GetValue<string>() throwing.
        var request = """{"jsonrpc":"2.0","id":7,"method":12345}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32600,
            "a non-string method is Invalid Request, not Internal error");
    }

    [Fact]
    public async Task HandleRequest_NonStringMethodAsNotification_ReturnsNull()
    {
        // No id → notification → no response even though the method is malformed.
        var request = """{"jsonrpc":"2.0","method":12345}""";
        var response = await _server.HandleRequestAsync(request);

        response.Should().BeNull("an id-less malformed request is still a notification");
    }

    [Fact]
    public async Task HandleRequest_StructurallyInvalidId_ReturnsInvalidRequest()
    {
        // Per JSON-RPC 2.0 §4.2 the id must be a string, number, or null. An object id
        // is an Invalid Request (-32600), not an internal error (-32603), and the
        // unusable id is not echoed back.
        var request = """{"jsonrpc":"2.0","id":{"bad":"object"},"method":"initialize"}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["error"]!["code"]!.GetValue<int>().Should().Be(-32600,
            "a structurally-invalid id is Invalid Request, not Internal error");
        (response["id"] == null || response["id"]!.GetValueKind() == JsonValueKind.Null)
            .Should().BeTrue("the invalid id cannot be echoed, so it is null");
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":42,"method":"tools/call","params":"not-an-object"}""", "tools/call")]
    [InlineData("""{"jsonrpc":"2.0","id":42,"method":"tools/call","params":[1,2]}""", "tools/call")]
    [InlineData("""{"jsonrpc":"2.0","id":42,"method":"initialize","params":"x"}""", "initialize")]
    [InlineData("""{"jsonrpc":"2.0","id":42,"method":"tools/list","params":7}""", "tools/list")]
    public async Task HandleRequest_NonObjectParams_ReturnsInvalidParamsWithId(string request, string method)
    {
        // MCP params are always an object. A string/array/number params value is a caller
        // mistake in the params (-32602), not a server failure (-32603).
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(42);
        var error = response["error"]!.AsObject();
        error["code"]!.GetValue<int>().Should().Be(-32602);
        error["message"]!.GetValue<string>().Should().Be($"Invalid params: params for '{method}' must be an object");
    }

    [Fact]
    public async Task HandleRequest_Ping_ReturnsEmptyResult()
    {
        // MCP basic/utilities/ping: the receiver MUST respond promptly with an empty result.
        var response = await _server.HandleRequestAsync("""{"jsonrpc":"2.0","id":"p-1","method":"ping"}""");

        JsonSerializer.Serialize(response, _jsonOptions).Should().Be("""{"jsonrpc":"2.0","id":"p-1","result":{}}""");
    }

    [Fact]
    public async Task HandleRequest_PingAsNotification_ReturnsNull()
    {
        var response = await _server.HandleRequestAsync("""{"jsonrpc":"2.0","method":"ping"}""");

        response.Should().BeNull("an id-less ping is a notification and gets no response");
    }

    [Fact]
    public async Task HandleRequest_NullId_ReturnsInvalidRequestWithNullId()
    {
        // A present id:null is not a notification (only an ABSENT id is); MCP forbids a
        // null id, so the request is invalid and must still be answered, with id null.
        var response = await _server.HandleRequestAsync("""{"jsonrpc":"2.0","id":null,"method":"tools/list"}""");

        JsonSerializer.Serialize(response, _jsonOptions).Should().Be(
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"Invalid Request: id must not be null"}}""");
    }

    [Theory]
    [InlineData("""[{"jsonrpc":"2.0","id":3,"method":"ping"}]""")]
    [InlineData("42")]
    [InlineData("\"hello\"")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task HandleRequest_ValidJsonThatIsNotAnObject_ReturnsInvalidRequest(string request)
    {
        // JSON-RPC 2.0 §5.1: -32700 is reserved for text that is not JSON. Valid JSON that
        // is not a Request object is an Invalid Request (-32600), answered with id null.
        var response = await _server.HandleRequestAsync(request);

        JsonSerializer.Serialize(response, _jsonOptions).Should().Be(
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"Invalid Request: expected a JSON-RPC request object"}}""");
    }

    [Theory]
    [InlineData("1e3")]
    [InlineData("1.5")]
    [InlineData("12345678901234567890")]
    public async Task HandleRequest_NonInt64NumericId_IsAcceptedAndEchoedExactly(string idText)
    {
        var response = await _server.HandleRequestAsync(
            $$"""{"jsonrpc":"2.0","id":{{idText}},"method":"ping"}""");

        JsonSerializer.Serialize(response, _jsonOptions).Should().Be(
            $$$"""{"jsonrpc":"2.0","id":{{{idText}}},"result":{}}""");
    }

    [Theory]
    [InlineData("""{"jsonrpc":"1.0","id":5,"method":"ping"}""", 5)]
    [InlineData("""{"id":6,"method":"ping"}""", 6)]
    [InlineData("""{"jsonrpc":2,"id":7,"method":"ping"}""", 7)]
    public async Task HandleRequest_JsonRpcVersionNotExactly2_0_ReturnsInvalidRequest(string request, long id)
    {
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(id);
        response["error"]!["code"]!.GetValue<int>().Should().Be(-32600);
        response["error"]!["message"]!.GetValue<string>().Should().Be(
            "Invalid Request: jsonrpc must be exactly \"2.0\"");
    }

    [Fact]
    public async Task HandleRequest_NotificationWithWrongJsonRpcVersion_ReturnsNull()
    {
        var response = await _server.HandleRequestAsync("""{"jsonrpc":"1.0","method":"notifications/initialized"}""");

        response.Should().BeNull("a notification is never answered, even when invalid");
    }

    [Fact]
    public async Task HandleRequest_ToolsCall_UnknownToolName_Returns32602InvalidParams()
    {
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"does_not_exist_xyz","arguments":{}}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(1);
        var error = response["error"]!.AsObject();
        // Unknown tool maps to -32602 (Invalid params) per the dispatcher contract;
        // the message must mention "Unknown tool" and echo the bad name.
        error["code"]!.GetValue<int>().Should().Be(-32602);
        error["message"]!.GetValue<string>().Should().Contain("Unknown tool");
        error["message"]!.GetValue<string>().Should().Contain("does_not_exist_xyz");
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
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"sync_documents","arguments":{"filePaths":"not-an-array"}}}""";
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
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"sync_documents","arguments":{"filePaths":[42]}}}""";
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
        var request = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"health_check"}}""";
        var response = ParseResponse(await _server.HandleRequestAsync(request));

        response["id"]!.GetValue<long>().Should().Be(1);
        response["error"].Should().BeNull("missing arguments must not produce an error for parameterless tools");
        var content = response["result"]!.AsObject()["content"]!.AsArray();
        content.Count.Should().BeGreaterThan(0);
    }
}
