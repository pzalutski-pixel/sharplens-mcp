using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpLensMcp;

/// <summary>
/// Handles stdin/stdout JSON-RPC communication in worker mode.
/// Receives tool invocation requests from the main process and routes them to RoslynService.
/// </summary>
public class WorkerHost
{
    private readonly RoslynService _roslynService;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly CancellationTokenSource _shutdownCts = new();

    public WorkerHost(RoslynService roslynService)
    {
        _roslynService = roslynService;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public async Task RunAsync()
    {
        Log(LogLevel.Information, "Worker process starting...");

        using var reader = Console.In;
        using var writer = Console.Out;

        while (!_shutdownCts.Token.IsCancellationRequested)
        {
            try
            {
                var line = await reader.ReadLineAsync(_shutdownCts.Token);
                if (line == null)
                {
                    Log(LogLevel.Information, "Received EOF on stdin, worker shutting down");
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                Log(LogLevel.Debug, $"Worker received: {line}");

                var response = await HandleRequestAsync(line);

                var responseJson = JsonSerializer.Serialize(response, _jsonOptions);
                await writer.WriteLineAsync(responseJson);
                await writer.FlushAsync();

                Log(LogLevel.Debug, $"Worker sent: {responseJson}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Worker error: {ex}");
            }
        }

        Log(LogLevel.Information, "Worker process exiting");
    }

    private async Task<object> HandleRequestAsync(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<JsonObject>(requestJson);
            if (request == null)
            {
                return CreateErrorResponse(null, -32700, "Parse error");
            }

            var id = request["id"];
            var method = request["method"]?.GetValue<string>();
            var paramsNode = request["params"]?.AsObject();

            if (string.IsNullOrEmpty(method))
            {
                return CreateErrorResponse(id, -32600, "Invalid Request: missing method");
            }

            return method switch
            {
                "invoke_tool" => await HandleInvokeToolAsync(id, paramsNode),
                "shutdown" => HandleShutdown(id),
                "ping" => CreateSuccessResponse(id, new { pong = true, timestamp = DateTime.UtcNow }),
                _ => CreateErrorResponse(id, -32601, $"Method not found: {method}")
            };
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Error handling request: {ex}");
            return CreateErrorResponse(null, -32603, $"Internal error: {ex.Message}");
        }
    }

    private async Task<object> HandleInvokeToolAsync(JsonNode? id, JsonObject? parameters)
    {
        if (parameters == null)
        {
            return CreateErrorResponse(id, -32602, "Invalid params: missing parameters");
        }

        var toolName = parameters["tool"]?.GetValue<string>();
        var arguments = parameters["arguments"]?.AsObject();

        if (string.IsNullOrEmpty(toolName))
        {
            return CreateErrorResponse(id, -32602, "Invalid params: missing tool name");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await InvokeRoslynToolAsync(toolName, arguments);
            sw.Stop();
            Log(LogLevel.Debug, $"Tool '{toolName}' completed in {sw.ElapsedMilliseconds}ms");
            return CreateSuccessResponse(id, result);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log(LogLevel.Error, $"Tool '{toolName}' failed after {sw.ElapsedMilliseconds}ms: {ex}");
            return CreateErrorResponse(id, -32603, $"Tool error: {ex.Message}");
        }
    }

    private object HandleShutdown(JsonNode? id)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(100); // Give time for response to be sent
            _shutdownCts.Cancel();
        });
        return CreateSuccessResponse(id, new { message = "Shutting down" });
    }

    private async Task<object> InvokeRoslynToolAsync(string toolName, JsonObject? arguments)
    {
        // Route tool calls to RoslynService methods
        // This mirrors the switch statement in McpServer.HandleToolCallAsync
        return toolName switch
        {
            "health_check" => await _roslynService.GetHealthCheckAsync(),
            "load_solution" => await _roslynService.LoadSolutionAsync(
                arguments?["solutionPath"]?.GetValue<string>() ?? throw new ArgumentException("solutionPath required")),
            "sync_documents" => await _roslynService.SyncDocumentsAsync(
                ParseStringArray(arguments?["filePaths"])),
            "get_symbol_info" => await _roslynService.GetSymbolInfoAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required")),
            "go_to_definition" => await _roslynService.GoToDefinitionAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required")),
            "find_references" => await _roslynService.FindReferencesAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["maxResults"]?.GetValue<int>() ?? 100),
            "find_implementations" => await _roslynService.FindImplementationsAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["maxResults"]?.GetValue<int>() ?? 50),
            "get_type_hierarchy" => await _roslynService.GetTypeHierarchyAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["maxDerivedTypes"]?.GetValue<int>() ?? 50),
            "search_symbols" => await _roslynService.SearchSymbolsAsync(
                arguments?["query"]?.GetValue<string>() ?? throw new ArgumentException("query required"),
                arguments?["kind"]?.GetValue<string>(),
                arguments?["maxResults"]?.GetValue<int>() ?? 50,
                arguments?["namespaceFilter"]?.GetValue<string>(),
                arguments?["offset"]?.GetValue<int>() ?? 0),
            "semantic_query" => await _roslynService.SemanticQueryAsync(
                ParseStringArray(arguments?["kinds"]),
                arguments?["isAsync"]?.GetValue<bool>(),
                arguments?["namespaceFilter"]?.GetValue<string>(),
                arguments?["accessibility"]?.GetValue<string>(),
                arguments?["isStatic"]?.GetValue<bool>(),
                arguments?["type"]?.GetValue<string>(),
                arguments?["returnType"]?.GetValue<string>(),
                ParseStringArray(arguments?["attributes"]),
                ParseStringArray(arguments?["parameterIncludes"]),
                ParseStringArray(arguments?["parameterExcludes"]),
                arguments?["maxResults"]?.GetValue<int>() ?? 100),
            "get_diagnostics" => await _roslynService.GetDiagnosticsAsync(
                arguments?["filePath"]?.GetValue<string>(),
                arguments?["projectPath"]?.GetValue<string>(),
                arguments?["severity"]?.GetValue<string>(),
                arguments?["includeHidden"]?.GetValue<bool>() ?? false),
            "get_code_fixes" => await _roslynService.GetCodeFixesAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["diagnosticId"]?.GetValue<string>() ?? throw new ArgumentException("diagnosticId required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required")),
            "apply_code_fix" => await _roslynService.ApplyCodeFixAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["diagnosticId"]?.GetValue<string>() ?? throw new ArgumentException("diagnosticId required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["fixIndex"]?.GetValue<int>(),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "get_project_structure" => await _roslynService.GetProjectStructureAsync(
                arguments?["includeReferences"]?.GetValue<bool>() ?? true,
                arguments?["includeDocuments"]?.GetValue<bool>() ?? false,
                arguments?["projectNamePattern"]?.GetValue<string>(),
                arguments?["maxProjects"]?.GetValue<int>(),
                arguments?["summaryOnly"]?.GetValue<bool>() ?? false),
            "organize_usings" => await _roslynService.OrganizeUsingsAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required")),
            "organize_usings_batch" => await _roslynService.OrganizeUsingsBatchAsync(
                arguments?["projectName"]?.GetValue<string>(),
                arguments?["filePattern"]?.GetValue<string>(),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "format_document_batch" => await _roslynService.FormatDocumentBatchAsync(
                arguments?["projectName"]?.GetValue<string>(),
                arguments?["includeTests"]?.GetValue<bool>() ?? true,
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "get_method_overloads" => await _roslynService.GetMethodOverloadsAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required")),
            "get_containing_member" => await _roslynService.GetContainingMemberAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required")),
            "find_callers" => await _roslynService.FindCallersAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["maxResults"]?.GetValue<int>() ?? 100),
            "find_unused_code" => await _roslynService.FindUnusedCodeAsync(
                arguments?["projectName"]?.GetValue<string>(),
                arguments?["includePrivate"]?.GetValue<bool>() ?? true,
                arguments?["includeInternal"]?.GetValue<bool>() ?? false,
                arguments?["symbolKindFilter"]?.GetValue<string>(),
                arguments?["maxResults"]?.GetValue<int>() ?? 50),
            "rename_symbol" => await _roslynService.RenameSymbolAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["newName"]?.GetValue<string>() ?? throw new ArgumentException("newName required"),
                arguments?["preview"]?.GetValue<bool>() ?? true,
                arguments?["maxFiles"]?.GetValue<int>() ?? 20,
                arguments?["verbosity"]?.GetValue<string>() ?? "summary"),
            "extract_interface" => await _roslynService.ExtractInterfaceAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["interfaceName"]?.GetValue<string>() ?? throw new ArgumentException("interfaceName required"),
                ParseStringArray(arguments?["includeMemberNames"])),
            "dependency_graph" => await _roslynService.GetDependencyGraphAsync(
                arguments?["format"]?.GetValue<string>() ?? "json"),
            "get_type_members" => await _roslynService.GetTypeMembersAsync(
                arguments?["typeName"]?.GetValue<string>() ?? throw new ArgumentException("typeName required"),
                arguments?["includeInherited"]?.GetValue<bool>() ?? false,
                arguments?["memberKind"]?.GetValue<string>(),
                arguments?["verbosity"]?.GetValue<string>() ?? "compact",
                arguments?["maxResults"]?.GetValue<int>() ?? 100),
            "get_method_signature" => await _roslynService.GetMethodSignatureAsync(
                arguments?["typeName"]?.GetValue<string>() ?? throw new ArgumentException("typeName required"),
                arguments?["methodName"]?.GetValue<string>() ?? throw new ArgumentException("methodName required"),
                arguments?["overloadIndex"]?.GetValue<int>() ?? 0),
            "get_attributes" => await _roslynService.GetAttributesAsync(
                arguments?["attributeName"]?.GetValue<string>() ?? throw new ArgumentException("attributeName required"),
                arguments?["scope"]?.GetValue<string>() ?? "solution",
                true, // parseGodotHints
                arguments?["maxResults"]?.GetValue<int>() ?? 100),
            "get_derived_types" => await _roslynService.GetDerivedTypesAsync(
                arguments?["baseTypeName"]?.GetValue<string>() ?? throw new ArgumentException("baseTypeName required"),
                arguments?["includeTransitive"]?.GetValue<bool>() ?? true,
                arguments?["maxResults"]?.GetValue<int>() ?? 100),
            "get_base_types" => await _roslynService.GetBaseTypesAsync(
                arguments?["typeName"]?.GetValue<string>() ?? throw new ArgumentException("typeName required")),
            "analyze_data_flow" => await _roslynService.AnalyzeDataFlowAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["startLine"]?.GetValue<int>() ?? throw new ArgumentException("startLine required"),
                arguments?["endLine"]?.GetValue<int>() ?? throw new ArgumentException("endLine required")),
            "analyze_control_flow" => await _roslynService.AnalyzeControlFlowAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["startLine"]?.GetValue<int>() ?? throw new ArgumentException("startLine required"),
                arguments?["endLine"]?.GetValue<int>() ?? throw new ArgumentException("endLine required")),
            "get_type_overview" => await _roslynService.GetTypeOverviewAsync(
                arguments?["typeName"]?.GetValue<string>() ?? throw new ArgumentException("typeName required")),
            "analyze_method" => await _roslynService.AnalyzeMethodAsync(
                arguments?["typeName"]?.GetValue<string>() ?? throw new ArgumentException("typeName required"),
                arguments?["methodName"]?.GetValue<string>() ?? throw new ArgumentException("methodName required"),
                arguments?["includeCallers"]?.GetValue<bool>() ?? true,
                arguments?["includeOutgoingCalls"]?.GetValue<bool>() ?? false,
                arguments?["maxCallers"]?.GetValue<int>() ?? 20,
                arguments?["maxOutgoingCalls"]?.GetValue<int>() ?? 50),
            "get_file_overview" => await _roslynService.GetFileOverviewAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required")),
            "get_missing_members" => await _roslynService.GetMissingMembersAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required")),
            "get_outgoing_calls" => await _roslynService.GetOutgoingCallsAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["maxDepth"]?.GetValue<int>() ?? 1),
            "validate_code" => await _roslynService.ValidateCodeAsync(
                arguments?["code"]?.GetValue<string>() ?? throw new ArgumentException("code required"),
                arguments?["contextFilePath"]?.GetValue<string>(),
                arguments?["standalone"]?.GetValue<bool>() ?? false),
            "check_type_compatibility" => await _roslynService.CheckTypeCompatibilityAsync(
                arguments?["sourceType"]?.GetValue<string>() ?? throw new ArgumentException("sourceType required"),
                arguments?["targetType"]?.GetValue<string>() ?? throw new ArgumentException("targetType required")),
            "get_instantiation_options" => await _roslynService.GetInstantiationOptionsAsync(
                arguments?["typeName"]?.GetValue<string>() ?? throw new ArgumentException("typeName required")),
            "analyze_change_impact" => await _roslynService.AnalyzeChangeImpactAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["changeType"]?.GetValue<string>() ?? throw new ArgumentException("changeType required"),
                arguments?["newValue"]?.GetValue<string>()),
            "get_method_source" => await _roslynService.GetMethodSourceAsync(
                arguments?["typeName"]?.GetValue<string>() ?? throw new ArgumentException("typeName required"),
                arguments?["methodName"]?.GetValue<string>() ?? throw new ArgumentException("methodName required"),
                arguments?["overloadIndex"]?.GetValue<int>() ?? 0),
            "get_method_source_batch" => await _roslynService.GetMethodSourceBatchAsync(
                ParseMethodBatchRequestsAsDictList(arguments?["methods"]),
                arguments?["maxMethods"]?.GetValue<int>() ?? 20),
            "generate_constructor" => await _roslynService.GenerateConstructorAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["includeProperties"]?.GetValue<bool>() ?? false,
                arguments?["initializeToDefault"]?.GetValue<bool>() ?? false),
            "change_signature" => await _roslynService.ChangeSignatureAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                ParseSignatureChanges(arguments?["changes"]),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "extract_method" => await _roslynService.ExtractMethodAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["startLine"]?.GetValue<int>() ?? throw new ArgumentException("startLine required"),
                arguments?["endLine"]?.GetValue<int>() ?? throw new ArgumentException("endLine required"),
                arguments?["methodName"]?.GetValue<string>() ?? throw new ArgumentException("methodName required"),
                arguments?["accessibility"]?.GetValue<string>() ?? "private",
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "get_code_actions_at_position" => await _roslynService.GetCodeActionsAtPositionAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["endLine"]?.GetValue<int>(),
                arguments?["endColumn"]?.GetValue<int>(),
                arguments?["includeRefactorings"]?.GetValue<bool>() ?? true,
                arguments?["includeCodeFixes"]?.GetValue<bool>() ?? true),
            "apply_code_action_by_title" => await _roslynService.ApplyCodeActionByTitleAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["title"]?.GetValue<string>() ?? throw new ArgumentException("title required"),
                arguments?["endLine"]?.GetValue<int>(),
                arguments?["endColumn"]?.GetValue<int>(),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "implement_missing_members" => await _roslynService.ImplementMissingMembersAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "encapsulate_field" => await _roslynService.EncapsulateFieldAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "inline_variable" => await _roslynService.InlineVariableAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "extract_variable" => await _roslynService.ExtractVariableAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["endLine"]?.GetValue<int>(),
                arguments?["endColumn"]?.GetValue<int>(),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "get_complexity_metrics" => await _roslynService.GetComplexityMetricsAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>(),
                arguments?["column"]?.GetValue<int>(),
                ParseStringArray(arguments?["metrics"])),
            "add_null_checks" => await _roslynService.AddNullChecksAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "generate_equality_members" => await _roslynService.GenerateEqualityMembersAsync(
                arguments?["filePath"]?.GetValue<string>() ?? throw new ArgumentException("filePath required"),
                arguments?["line"]?.GetValue<int>() ?? throw new ArgumentException("line required"),
                arguments?["column"]?.GetValue<int>() ?? throw new ArgumentException("column required"),
                arguments?["includeOperators"]?.GetValue<bool>() ?? true,
                arguments?["preview"]?.GetValue<bool>() ?? true),
            "get_type_members_batch" => await _roslynService.GetTypeMembersBatchAsync(
                ParseStringArray(arguments?["typeNames"]) ?? throw new ArgumentException("typeNames required"),
                arguments?["includeInherited"]?.GetValue<bool>() ?? false,
                arguments?["memberKind"]?.GetValue<string>(),
                arguments?["verbosity"]?.GetValue<string>() ?? "compact",
                arguments?["maxResultsPerType"]?.GetValue<int>() ?? 50),
            _ => throw new ArgumentException($"Unknown tool: {toolName}")
        };
    }

    #region Helper Methods

    private static List<string>? ParseStringArray(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonArray arr)
        {
            return arr.Select(n => n?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
        }
        return null;
    }

    private static List<Dictionary<string, object>> ParseMethodBatchRequestsAsDictList(JsonNode? node)
    {
        var result = new List<Dictionary<string, object>>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject obj)
                {
                    var dict = new Dictionary<string, object>();
                    var typeName = obj["typeName"]?.GetValue<string>();
                    var methodName = obj["methodName"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(typeName) && !string.IsNullOrEmpty(methodName))
                    {
                        dict["typeName"] = typeName;
                        dict["methodName"] = methodName;
                        if (obj["overloadIndex"] != null)
                        {
                            dict["overloadIndex"] = obj["overloadIndex"]!.GetValue<int>();
                        }
                        result.Add(dict);
                    }
                }
            }
        }
        return result;
    }

    private static List<SignatureChange> ParseSignatureChanges(JsonNode? node)
    {
        var result = new List<SignatureChange>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject obj)
                {
                    var action = obj["action"]?.GetValue<string>() ?? "";
                    result.Add(new SignatureChange
                    {
                        Action = action,
                        Name = obj["name"]?.GetValue<string>(),
                        Type = obj["type"]?.GetValue<string>(),
                        DefaultValue = obj["defaultValue"]?.GetValue<string>(),
                        Position = obj["position"]?.GetValue<int>(),
                        NewName = obj["newName"]?.GetValue<string>(),
                        Order = action == "reorder" && obj["order"] is JsonArray orderArr
                            ? orderArr.Select(n => n?.GetValue<string>() ?? "").ToList()
                            : null
                    });
                }
            }
        }
        return result;
    }

    private static object CreateSuccessResponse(JsonNode? id, object result)
    {
        return new
        {
            jsonrpc = "2.0",
            id = id,
            result
        };
    }

    private static object CreateErrorResponse(JsonNode? id, int code, string message)
    {
        return new
        {
            jsonrpc = "2.0",
            id = id,
            error = new { code, message }
        };
    }

    private static void Log(LogLevel level, string message) => Logger.Log("Worker", level, message);

    #endregion
}
