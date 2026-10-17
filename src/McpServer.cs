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
    // Deterministic fault seam for the dispatcher's exception-wrapping contract: when
    // non-null, HandleToolCallAsync throws it in place of dispatch, exercising the generic
    // catch's in-band INTERNAL_ERROR path without coupling the test to any one tool's
    // failure mode. Null in production (never fires).
    private readonly Exception? _testToolCallFault;
    // Deterministic seam for the startup auto-load: when non-null, the auto-load awaits it
    // (with the auto-load's own token) before loading, so a test controls exactly when the
    // load proceeds — or makes it outlast its budget. Null in production (never awaited).
    private readonly Func<CancellationToken, Task>? _testAutoLoadGate;
    // Budget for the DOTNET_SOLUTION_PATH auto-load: the per-call timeout in production,
    // since a load_solution call gets that same budget.
    private readonly TimeSpan _autoLoadTimeout;
    // The running (or finished) startup auto-load; null when DOTNET_SOLUTION_PATH is unset.
    private Task? _autoLoad;

    public McpServer() : this(ReadConfiguredToolCallTimeout())
    {
    }

    // Test seam: injects the per-call timeout directly (and optionally a forced
    // cancellation token). Tests must not mutate SHARPLENS_TIMEOUT_SECONDS —
    // environment variables are process-wide and parallel test classes would race.
    internal McpServer(TimeSpan toolCallTimeout, CancellationToken testToolCallCancellation = default,
        Exception? testToolCallFault = null, Func<CancellationToken, Task>? testAutoLoadGate = null,
        TimeSpan? autoLoadTimeout = null)
    {
        _testAutoLoadGate = testAutoLoadGate;
        _autoLoadTimeout = autoLoadTimeout ?? toolCallTimeout;
        _roslynService = new RoslynService();
        _logLevel = ParseLogLevel(EnvironmentConfig.Get("SHARPLENS_LOG_LEVEL", "ROSLYN_LOG_LEVEL"));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Converters = { new RequestId.Converter() }
        };
        _toolCallTimeout = toolCallTimeout;
        _testToolCallCancellation = testToolCallCancellation;
        _testToolCallFault = testToolCallFault;
    }

    // Test seam: load an in-memory workspace into the underlying service so the
    // dispatcher + result wrapper can be exercised end-to-end against code that
    // wouldn't compile (and so can't live on disk) — without an MSBuild solution.
    // Mirrors RoslynService.LoadFromWorkspaceForTesting; production loads via the
    // load_solution tool.
    internal void LoadFromWorkspaceForTesting(Microsoft.CodeAnalysis.Workspace workspace)
        => _roslynService.LoadFromWorkspaceForTesting(workspace);

    private static TimeSpan ReadConfiguredToolCallTimeout() =>
        TimeSpan.FromSeconds(EnvironmentConfig.GetToolCallTimeoutSeconds());

    // A configured timeout must be a positive integer; anything else (missing,
    // non-numeric, zero, or negative) falls back to the 30s default, and a value above
    // the timer maximum is clamped to it. Pure so the guard is unit-testable without
    // mutating the shared process environment.
    internal static int ParseToolCallTimeoutSeconds(string? configured) =>
        EnvironmentConfig.ParseInt("SHARPLENS_TIMEOUT_SECONDS", configured,
            EnvironmentConfig.DefaultToolCallTimeoutSeconds, minimum: 1,
            maximum: EnvironmentConfig.MaxToolCallTimeoutSeconds).Value;

    // The configured log level matched case-insensitively against the known levels; an
    // unknown or missing value is Information.
    internal static string ParseLogLevel(string? configured) =>
        LogLevels.FirstOrDefault(level => string.Equals(level, configured?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? "Information";

    // Serves JSON-RPC messages, one per line, from input to output until input ends.
    // autoLoadPath is DOTNET_SOLUTION_PATH: a solution file, or a directory holding one.
    // Its load runs beside the request loop, so initialize/ping/tools/list are answered
    // at once however large the solution; every tools/call waits for it to finish.
    public async Task RunAsync(TextReader input, TextWriter output, string? autoLoadPath)
    {
        await LogAsync("Information", "SharpLens MCP server starting...");

        using var shutdown = new CancellationTokenSource();
        if (!string.IsNullOrWhiteSpace(autoLoadPath))
        {
            _autoLoad = Task.Run(() => AutoLoadAsync(autoLoadPath, shutdown.Token));
        }

        while (true)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // stdin is gone (broken pipe): there is no client left to serve.
                await LogAsync("Error", $"stdin failed, shutting down: {ex.Message}");
                break;
            }
            if (line == null)
            {
                await LogAsync("Information", "Received EOF on stdin, shutting down");
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                await LogAsync("Debug", $"Received request: {line}");

                var response = await HandleRequestAsync(line);

                if (response != null)
                {
                    var responseJson = JsonSerializer.Serialize(response, _jsonOptions);
                    await output.WriteLineAsync(responseJson);
                    await output.FlushAsync();

                    await LogAsync("Debug", $"Sent response: {responseJson}");
                }
            }
            catch (Exception ex)
            {
                await LogAsync("Error", $"Error in main loop: {ex}");
            }
        }

        // An auto-load still running at shutdown is cancelled and awaited, so its workspace
        // (and build host processes) are disposed rather than abandoned.
        if (_autoLoad != null)
        {
            shutdown.Cancel();
            await _autoLoad;
        }
    }

    // The startup auto-load. Never throws: every outcome is logged, and one that leaves the
    // requested solution unloaded is recorded for health_check (lastLoadFailure), so it is
    // visible to the client and not only on stderr. Bounded by the same budget a
    // load_solution call gets.
    private async Task AutoLoadAsync(string configuredPath, CancellationToken shutdownToken)
    {
        var resolution = SolutionPathResolver.Resolve(configuredPath);
        if (resolution.Path is not { } solutionPath)
        {
            _roslynService.RecordLoadFailure(new LoadFailure(configuredPath, ErrorCodes.InvalidParameter,
                resolution.Error!, DateTime.UtcNow));
            await LogAsync("Warning", $"Auto-load skipped: {resolution.Error}");
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        budget.CancelAfter(_autoLoadTimeout);
        try
        {
            if (_testAutoLoadGate != null) await _testAutoLoadGate(budget.Token);
            await LogAsync("Information", $"Auto-loading solution: {solutionPath}");
            var result = JsonSerializer.SerializeToNode(
                await _roslynService.LoadSolutionAsync(solutionPath, budget.Token), _jsonOptions);

            if (result?["success"]?.GetValue<bool>() != true)
            {
                await LogAsync("Warning", $"Auto-load of {solutionPath} failed: {result?["error"]?["message"]}");
            }
            else if (result["data"]?["partial"]?.GetValue<bool>() == true)
            {
                await LogAsync("Warning",
                    $"Auto-loaded {solutionPath} is PARTIAL: {result["data"]?["loadFailures"]?.ToJsonString()}");
            }
            else
            {
                await LogAsync("Information", $"Auto-loaded {solutionPath}: {result["data"]?["projectCount"]} projects");
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            var message = shutdownToken.IsCancellationRequested
                ? $"The startup auto-load of {solutionPath} (DOTNET_SOLUTION_PATH) was cancelled at shutdown."
                : $"The startup auto-load of {solutionPath} (DOTNET_SOLUTION_PATH) exceeded its " +
                  $"{_autoLoadTimeout.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}-second budget. " +
                  "Raise SHARPLENS_TIMEOUT_SECONDS, or call load_solution.";
            _roslynService.RecordLoadFailure(new LoadFailure(solutionPath, ErrorCodes.Timeout, message, DateTime.UtcNow));
            await LogAsync("Warning", message);
        }
        catch (Exception ex)
        {
            var message = $"The startup auto-load of {solutionPath} (DOTNET_SOLUTION_PATH) failed: {ex.Message}";
            _roslynService.RecordLoadFailure(new LoadFailure(solutionPath, ErrorCodes.InternalError, message, DateTime.UtcNow));
            await LogAsync("Error", $"{message}\n{ex}");
        }
    }

    // The request methods this server answers. Anything else is -32601 Method not found.
    private static readonly HashSet<string> ServedMethods =
        new(StringComparer.Ordinal) { "initialize", "ping", "tools/list", "tools/call" };

    internal async Task<object?> HandleRequestAsync(string requestJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestJson);
        }
        catch (JsonException ex)
        {
            // JSON-RPC 2.0 §5.1: text that is not JSON is -32700 Parse error, answered with
            // id null because no id can be read from it.
            await LogAsync("Error", $"Parse error: {ex.Message}");
            return CreateErrorResponse((RequestId?)null, -32700, $"Parse error: {ex.Message}");
        }

        // Valid JSON that is not a single Request object (a scalar, null, or an array) is an
        // Invalid Request, not a parse error. An array is a JSON-RPC batch, which MCP
        // 2025-06-18 removed; it is rejected as one Invalid Request.
        if (root is not JsonObject request)
        {
            return CreateErrorResponse((RequestId?)null, -32600, "Invalid Request: expected a JSON-RPC request object");
        }

        // JSON-RPC 2.0 §4.1: a request is a Notification if and only if the `id` member is
        // ABSENT. A present id is read even when it is JSON null: MCP forbids a null id, so
        // that request is invalid and is answered (with id null) rather than ignored.
        var isNotification = !request.ContainsKey("id");
        RequestId? id = null;
        if (!isNotification)
        {
            var idNode = request["id"];
            if (idNode is null)
            {
                return CreateErrorResponse((RequestId?)null, -32600, "Invalid Request: id must not be null");
            }
            try
            {
                id = idNode.Deserialize<RequestId>(_jsonOptions);
            }
            catch (JsonException)
            {
                // §4: the id is a string or a number. A bool/object/array id cannot be
                // echoed, so the Invalid Request is answered with id null.
                return CreateErrorResponse((RequestId?)null, -32600, "Invalid Request: id must be a string or number");
            }
        }

        // §4: jsonrpc MUST be exactly "2.0".
        var isVersion2 = request["jsonrpc"] is JsonValue versionValue
            && versionValue.TryGetValue<string>(out var version) && version == "2.0";
        if (!isVersion2)
        {
            return isNotification
                ? null
                : CreateErrorResponse(id, -32600, "Invalid Request: jsonrpc must be exactly \"2.0\"");
        }

        // A non-string method (number/object/array) is as invalid as a missing one.
        string? method = null;
        if (request["method"] is JsonValue methodValue) methodValue.TryGetValue<string>(out method);

        // §4.1: a Notification gets no response, not even an error. The methods served
        // here (initialize/ping/tools/list/tools/call) all REQUIRE a response, so one sent
        // as a notification is a client error and is not dispatched — dispatching would run
        // a possibly mutating tool whose result the spec forbids returning. Genuine
        // notifications (notifications/*) have no handler and are ignored.
        if (isNotification)
        {
            await LogAsync("Debug", $"Ignoring notification '{method}': request methods require an id and are not dispatched");
            return null;
        }

        if (string.IsNullOrEmpty(method))
        {
            return CreateErrorResponse(id, -32600, "Invalid Request: missing or non-string method");
        }

        if (!ServedMethods.Contains(method))
        {
            return CreateErrorResponse(id, -32601, $"Method not found: {method}");
        }

        // MCP params are always an object. A JSON null is treated as absent; any other
        // non-object value is invalid params.
        var paramsNode = request["params"];
        if (paramsNode is not null and not JsonObject)
        {
            return CreateErrorResponse(id, -32602, $"Invalid params: params for '{method}' must be an object");
        }
        var requestParams = paramsNode as JsonObject;

        try
        {
            return method switch
            {
                "initialize" => await HandleInitializeAsync(id, requestParams),
                "ping" => CreateSuccessResponse(id, new { }),
                "tools/list" => await HandleListToolsAsync(id),
                _ => await HandleToolCallAsync(id, requestParams),
            };
        }
        catch (Exception ex)
        {
            await LogAsync("Error", $"Error handling request: {ex}");
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

            // Every tool sees the solution the startup auto-load produces, so a call that
            // arrives while it is still running waits for it, within this call's budget.
            if (_autoLoad is { IsCompleted: false } autoLoad)
            {
                await LogAsync("Debug", $"Tool call '{name}' waits for the startup auto-load to finish");
                await autoLoad.WaitAsync(timeoutCts.Token);
            }

            // Check cancellation before the synchronous prelude / dispatch, so an
            // already-cancelled budget surfaces as TIMEOUT deterministically rather than
            // depending on the first Roslyn call to observe the token.
            timeoutCts.Token.ThrowIfCancellationRequested();

            // Deterministic fault seam (null in production): simulate an unexpected
            // exception escaping the handler so the generic catch's in-band INTERNAL_ERROR
            // wrapping is testable without depending on a specific tool's failure mode.
            if (_testToolCallFault != null) throw _testToolCallFault;

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
                hint: TimeoutHint(name));
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

    // A load cannot be narrowed — it builds every project — so its timeout advice is only
    // the budget; every other tool can also be asked for less. A call that timed out while
    // waiting for the startup auto-load is told so.
    private string TimeoutHint(string? toolName)
    {
        if (_autoLoad is { IsCompleted: false })
        {
            return "The startup auto-load of DOTNET_SOLUTION_PATH is still running and this call waited for it; retry once it finishes (health_check reports the outcome), or raise SHARPLENS_TIMEOUT_SECONDS.";
        }
        return toolName is "load_solution" or "roslyn:load_solution"
            ? "Raise SHARPLENS_TIMEOUT_SECONDS: loading runs a design-time build of every project, so a large solution needs a larger budget. The previously loaded solution, if any, is unchanged."
            : "Raise SHARPLENS_TIMEOUT_SECONDS, or narrow the request (single file or project, smaller maxResults).";
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
            await Console.Error.WriteLineAsync(
                $"[{DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}] [{level}] {message}");
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
