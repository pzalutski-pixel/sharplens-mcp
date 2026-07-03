using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpLensMcp;

public class McpServer
{
    // Protocol versions this server can speak, newest first. initialize echoes
    // the client's requested version when supported and otherwise answers with
    // the newest, per MCP version negotiation.
    private static readonly string[] SupportedProtocolVersions =
        { "2025-06-18", "2025-03-26", "2024-11-05" };
    private static readonly string ServerVersion = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly string[] LogLevels = { "Trace", "Debug", "Information", "Warning", "Error" };

    private readonly RoslynService _roslynService;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string _logLevel;
    private readonly TimeSpan _toolCallTimeout;
    // Deterministic cancellation seam for timeout tests: when this token is already
    // cancelled, every tool call's effective token is born cancelled (it is linked
    // into the per-call CTS), so the pre-dispatch check in HandleToolCallAsync forces
    // a TIMEOUT with no timing race. CancellationToken.None in production (never fires).
    private readonly CancellationToken _testToolCallCancellation;

    public McpServer() : this(ReadConfiguredToolCallTimeout())
    {
    }

    // Test seam: injects the per-call timeout directly (and optionally a forced
    // cancellation token). Tests must not mutate SHARPLENS_TIMEOUT_SECONDS —
    // environment variables are process-wide and parallel test classes would race.
    internal McpServer(TimeSpan toolCallTimeout, CancellationToken testToolCallCancellation = default)
    {
        _roslynService = new RoslynService();
        _logLevel = EnvironmentConfig.Get("SHARPLENS_LOG_LEVEL", "ROSLYN_LOG_LEVEL") ?? "Information";
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = { new RequestId.Converter() }
        };
        _toolCallTimeout = toolCallTimeout;
        _testToolCallCancellation = testToolCallCancellation;
    }

    // Test seam: load an in-memory workspace into the underlying service so the
    // dispatcher + result wrapper can be exercised end-to-end against code that
    // wouldn't compile (and so can't live on disk) — without an MSBuild solution.
    // Mirrors RoslynService.LoadFromWorkspaceForTesting; production loads via the
    // load_solution tool.
    internal void LoadFromWorkspaceForTesting(Microsoft.CodeAnalysis.Workspace workspace)
        => _roslynService.LoadFromWorkspaceForTesting(workspace);

    private static TimeSpan ReadConfiguredToolCallTimeout() =>
        TimeSpan.FromSeconds(ParseToolCallTimeoutSeconds(
            EnvironmentConfig.Get("SHARPLENS_TIMEOUT_SECONDS", "ROSLYN_TIMEOUT_SECONDS")));

    // A configured timeout must be a positive integer; anything else (missing,
    // non-numeric, zero, or negative) falls back to the 30s default. Pure so the
    // guard is unit-testable without mutating the shared process environment.
    internal static int ParseToolCallTimeoutSeconds(string? configured) =>
        int.TryParse(configured, out var parsed) && parsed > 0 ? parsed : 30;

    public async Task RunAsync()
    {
        await LogAsync("Information", "SharpLens MCP server starting...");

        // Auto-load solution from environment variable
        var solutionPath = Environment.GetEnvironmentVariable("DOTNET_SOLUTION_PATH");
        if (!string.IsNullOrEmpty(solutionPath))
        {
            try
            {
                // If it's a directory, try to find a .sln or .slnx file
                if (Directory.Exists(solutionPath))
                {
                    var slnFiles = Directory.GetFiles(solutionPath, "*.sln")
                        .Concat(Directory.GetFiles(solutionPath, "*.slnx"))
                        .ToArray();
                    if (slnFiles.Length > 0)
                    {
                        solutionPath = slnFiles[0];
                    }
                }

                if (File.Exists(solutionPath))
                {
                    await LogAsync("Information", $"Auto-loading solution: {solutionPath}");
                    await _roslynService.LoadSolutionAsync(solutionPath);
                }
            }
            catch (Exception ex)
            {
                await LogAsync("Warning", $"Failed to auto-load solution: {ex.Message}");
            }
        }

        // Main event loop - read from stdin, write to stdout
        using var reader = Console.In;
        using var writer = Console.Out;

        while (true)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                if (line == null)
                {
                    await LogAsync("Information", "Received EOF on stdin, shutting down");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                await LogAsync("Debug", $"Received request: {line}");

                var response = await HandleRequestAsync(line);

                if (response != null)
                {
                    var responseJson = JsonSerializer.Serialize(response, _jsonOptions);
                    await writer.WriteLineAsync(responseJson);
                    await writer.FlushAsync();

                    await LogAsync("Debug", $"Sent response: {responseJson}");
                }
            }
            catch (Exception ex)
            {
                await LogAsync("Error", $"Error in main loop: {ex}");
            }
        }
    }

    internal async Task<object?> HandleRequestAsync(string requestJson)
    {
        // Three-state machine for the outer catch's spec compliance:
        //   parsed=false           — exception before we knew if it was a notification → §5.1 parse-error response
        //   parsed=true, hasId=true  — a Request → §5.1 error response with the id
        //   parsed=true, hasId=false — a Notification → §4.3 no response at all
        var parsed = false;
        var hasId = false;
        RequestId? id = null;

        JsonObject? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonObject>(requestJson);
        }
        catch (JsonException ex)
        {
            // Per JSON-RPC 2.0 §5.1, malformed JSON must surface as -32700 Parse error,
            // not -32603 Internal error. Caught here before the main try-block so we
            // don't bury the spec-correct code under a generic exception handler.
            await LogAsync("Error", $"Parse error: {ex.Message}");
            return CreateErrorResponse((RequestId?)null, -32700, $"Parse error: {ex.Message}");
        }
        if (request == null)
        {
            return CreateErrorResponse((RequestId?)null, -32700, "Parse error");
        }

        try
        {

            // Per JSON-RPC 2.0 §4.3: a request is a Notification if and only if the
            // `id` member is absent. Notifications produce NO response (including no
            // error response). The previous heuristic of `method.StartsWith("notifications/")`
            // was incorrect and produced `{"id":null,...}` for valid notifications like
            // `{"jsonrpc":"2.0","method":"initialize"}`, which Zod-validated clients reject.
            hasId = request.ContainsKey("id") && request["id"] != null;
            try
            {
                id = hasId
                    ? JsonSerializer.Deserialize<RequestId>(request["id"]!, _jsonOptions)
                    : null;
            }
            catch (JsonException)
            {
                // Per JSON-RPC 2.0 §4.2 the id MUST be a string, number, or null. A
                // structurally-invalid id (object/array) is an Invalid Request (-32600),
                // not an internal error; we cannot echo the bad id, so respond id:null.
                return CreateErrorResponse((RequestId?)null, -32600,
                    "Invalid Request: id must be a string, number, or null");
            }
            parsed = true;
            // A non-string method (number/object/array) is as invalid as a missing one;
            // read it safely so it routes to the -32600/notification handling below
            // instead of throwing GetValue<string>() into the generic catch (-32603).
            string? method = null;
            if (request["method"] is JsonValue methodValue) methodValue.TryGetValue<string>(out method);
            var paramsNode = request["params"];

            if (string.IsNullOrEmpty(method))
            {
                if (!hasId) return null;
                return CreateErrorResponse(id, -32600, "Invalid Request: missing or non-string method");
            }

            // Per JSON-RPC 2.0 §4.3 a request without `id` is a Notification, which
            // gets no response. The methods we serve (initialize/tools/list/tools/call)
            // all REQUIRE a response, so receiving one as a notification is a client
            // error — do not dispatch it. Dispatching would run a possibly mutating tool
            // whose result the spec forbids us from returning. Genuine notifications
            // (notifications/*) are not handled and simply no-op here.
            if (!hasId)
            {
                await LogAsync("Debug", $"Ignoring notification '{method}': request methods require an id and are not dispatched");
                return null;
            }

            return method switch
            {
                "initialize" => await HandleInitializeAsync(id, paramsNode?.AsObject()),
                "tools/list" => await HandleListToolsAsync(id),
                "tools/call" => await HandleToolCallAsync(id, paramsNode?.AsObject()),
                _ => CreateErrorResponse(id, -32601, $"Method not found: {method}")
            };
        }
        catch (Exception ex)
        {
            await LogAsync("Error", $"Error handling request: {ex}");
            if (parsed && !hasId)
            {
                // Notification path: spec §4.3 forbids any response, even on exception.
                return null;
            }
            return CreateErrorResponse(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private Task<object> HandleInitializeAsync(RequestId? id, JsonObject? requestParams)
    {
        string? requested = null;
        if (requestParams?["protocolVersion"] is JsonValue versionValue &&
            versionValue.TryGetValue<string>(out var versionString))
        {
            requested = versionString;
        }

        var negotiated = requested != null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : SupportedProtocolVersions[0];

        var response = CreateSuccessResponse(id, new
        {
            protocolVersion = negotiated,
            capabilities = new
            {
                tools = new { }
            },
            serverInfo = new
            {
                name = "SharpLensMcp",
                version = ServerVersion
            }
        });
        return Task.FromResult(response);
    }

    private Task<object> HandleListToolsAsync(RequestId? id)
    {
        return Task.FromResult(CreateSuccessResponse(id, new { tools = ToolRegistry.BuildToolSchemas() }));
    }

    private async Task<object> HandleToolCallAsync(RequestId? id, JsonObject? paramsNode)
    {
        // Per-call timeout: SHARPLENS_TIMEOUT_SECONDS bounds every tool call. The
        // token reaches every Roslyn call through the dispatcher, so a runaway
        // compilation or solution-wide search cancels instead of hanging the session.
        // Link the deterministic test-cancellation seam (None in production) and apply
        // the timeout. A pre-cancelled seam token makes the effective token born cancelled.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_testToolCallCancellation);
        timeoutCts.CancelAfter(_toolCallTimeout);
        string? name = null;
        try
        {
            // Read name/arguments defensively, the same way `method` is read upstream: a
            // present-but-wrong-typed value is invalid params (-32602), not an exception
            // that escapes to the generic catch as an in-band INTERNAL_ERROR. A non-string
            // name reads as null and falls into the missing-tool-name path below.
            if (paramsNode?["name"] is JsonValue nameValue) nameValue.TryGetValue<string>(out name);

            if (string.IsNullOrEmpty(name))
            {
                return CreateErrorResponse(id, -32602, "Invalid params: missing tool name");
            }

            JsonObject? arguments = null;
            if (paramsNode?["arguments"] is { } argumentsNode)
            {
                arguments = argumentsNode as JsonObject
                    ?? throw new JsonRpcInvalidParamsException($"Parameter 'arguments' for tool '{name}' must be an object");
            }

            var p = new JsonRpcParameters(arguments, name);

            // Check cancellation before the synchronous prelude / dispatch, so an
            // already-cancelled budget surfaces as TIMEOUT deterministically rather than
            // depending on the first Roslyn call to observe the token.
            timeoutCts.Token.ThrowIfCancellationRequested();

            var result = await ToolRegistry.DispatchAsync(name, p, _roslynService, timeoutCts.Token);

            // A tool that RETURNS a {success:false} envelope (every CreateErrorResponse
            // and every raw error object — not just the exception catches below) is a
            // failed call. Surface it in-band with isError:true, matching
            // CreateToolErrorResult, so the model reads it as an error rather than a
            // successful result. Detected from the serialized envelope so it covers both
            // the typed ToolErrorResponse and the raw anonymous {success:false} returns.
            //
            // An apply tool that introduced compile errors is the second isError trigger:
            // it reports success:true / applied:true (the edit WAS applied — disclosed so
            // the caller can revert) but lists the new errors in data.postApplyErrors. A
            // client keying on isError must still see that broken state, so a non-empty
            // postApplyErrors flips isError too (R4).
            var resultNode = JsonSerializer.SerializeToNode(result, _jsonOptions);
            // A present-but-non-bool (or absent) success is treated as NOT an error
            // rather than throwing on GetValue<bool>() into the generic catch.
            var isError = (resultNode?["success"] is JsonValue successValue
                    && successValue.TryGetValue<bool>(out var success) && !success)
                || resultNode?["data"]?["postApplyErrors"] is JsonArray { Count: > 0 };
            // Reuse the node for the wire text instead of serializing `result` a second time.
            var resultText = resultNode?.ToJsonString(_jsonOptions) ?? "null";
            var content = new[] { new { type = "text", text = resultText } };

            return CreateSuccessResponse(id, isError
                ? (object)new { content, isError = true }
                : new { content });
        }
        catch (JsonRpcInvalidParamsException ex)
        {
            // Per JSON-RPC 2.0 §5.1: -32602 is "Invalid params" — the right code for
            // missing/wrong-type tool arguments. Don't log at Error level because these
            // are caller mistakes, not server failures.
            await LogAsync("Debug", $"Invalid params: {ex.Message}");
            return CreateErrorResponse(id, -32602, ex.Message);
        }
        catch (SolutionNotLoadedException)
        {
            // A caller-ordering mistake, not a server failure: surface the same
            // structured envelope every tool error uses so the agent can recover
            // (call load_solution) instead of seeing an opaque internal error.
            var notLoaded = RoslynService.CreateSolutionNotLoadedResponse();
            return CreateSuccessResponse(id, new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = JsonSerializer.Serialize(notLoaded, _jsonOptions)
                    }
                },
                // Match every other in-band tool error (CreateToolErrorResult): a
                // {success:false} envelope is surfaced with isError so the model reads
                // it as a failed call rather than a successful result.
                isError = true
            });
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            await LogAsync("Error", $"Tool call '{name}' timed out after {_toolCallTimeout.TotalSeconds:0}s");
            return CreateToolErrorResult(id, ErrorCodes.Timeout,
                $"Tool call '{name}' exceeded the {_toolCallTimeout.TotalSeconds:0}-second timeout",
                hint: "Raise SHARPLENS_TIMEOUT_SECONDS, or narrow the request (single file or project, smaller maxResults).");
        }
        catch (FileNotFoundException ex)
        {
            await LogAsync("Error", $"File not found: {ex.Message}");
            return CreateToolErrorResult(id, ErrorCodes.FileNotFound,
                $"File not found: {ex.Message}",
                hint: "Check the file path; use get_project_structure to list known documents.");
        }
        catch (Exception ex)
        {
            await LogAsync("Error", $"Error executing tool: {ex}");
            // An exception escaping a tool handler is a TOOL failure, not a
            // protocol failure: per MCP it belongs in-band as a CallToolResult
            // with isError, where the model can read it and recover. JSON-RPC
            // errors (-32602/-32601/-32700) stay reserved for protocol-level
            // problems like malformed requests and unknown methods.
            return CreateToolErrorResult(id, ErrorCodes.InternalError,
                $"Unhandled error executing tool: {ex.Message}",
                hint: "This is a server-side failure. Retry once; if it persists, adjust the arguments or report the message.");
        }
    }

    // Wraps a structured tool-error envelope as an in-band CallToolResult with
    // isError set, mirroring the {success:false, error:{...}} shape every
    // tool-level error already uses.
    private object CreateToolErrorResult(RequestId? id, string code, string message, string? hint = null)
    {
        var envelope = new
        {
            success = false,
            error = new RoslynError
            {
                Code = code,
                Message = message,
                Hint = hint
            }
        };

        return CreateSuccessResponse(id, new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = JsonSerializer.Serialize(envelope, _jsonOptions)
                }
            },
            isError = true
        });
    }

    private object CreateSuccessResponse(RequestId? id, object result)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            result
        };
    }

    private object CreateErrorResponse(RequestId? id, int code, string message)
    {
        return new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message
            }
        };
    }


    private async Task LogAsync(string level, string message)
    {
        if (ShouldLog(level))
        {
            await Console.Error.WriteLineAsync($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }
    }

    private bool ShouldLog(string messageLevel) => ShouldLog(messageLevel, _logLevel);

    // Pure level comparison, internal for unit tests. An unrecognized configured
    // level falls back to Information — Array.IndexOf's -1 used to make any typo
    // (or the then-missing "Trace") log everything.
    internal static bool ShouldLog(string messageLevel, string configuredLevel)
    {
        var messageIndex = Array.IndexOf(LogLevels, messageLevel);
        var configuredIndex = Array.IndexOf(LogLevels, configuredLevel);
        if (configuredIndex < 0)
        {
            configuredIndex = Array.IndexOf(LogLevels, "Information");
        }
        return messageIndex >= configuredIndex;
    }
}
