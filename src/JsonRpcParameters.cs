using System.Text.Json.Nodes;

namespace SharpLensMcp;

// Typed accessors for JSON-RPC tool arguments. Replaces the ~80 repeated
// `arguments?["x"]?.GetValue<T>() ?? throw new Exception("x required")` blocks
// in McpServer.HandleToolCallAsync. Missing-required and type-mismatch errors
// surface as JsonRpcInvalidParamsException, which the dispatcher converts to
// a structured -32602 response.
internal readonly struct JsonRpcParameters
{
    // Parameter-access probe for the schema⇄dispatcher parity test: when set,
    // every accessor records the parameter name it was asked for, and Required
    // accessors return defaults instead of throwing so one probe call records a
    // dispatch arm's COMPLETE read-set. Null in production — behavior unchanged.
    internal static readonly AsyncLocal<HashSet<string>?> ProbeLog = new();

    private readonly JsonObject? _args;
    private readonly string _toolName;

    public JsonRpcParameters(JsonObject? args, string toolName)
    {
        _args = args;
        _toolName = toolName;
    }

    // The tool these arguments belong to, so nested-array parsers can name it in
    // their -32602 messages the same way the scalar accessors here do.
    public string ToolName => _toolName;

    public T Required<T>(string name)
    {
        ProbeLog.Value?.Add(name);
        var node = _args?[name];
        if (node == null)
        {
            if (ProbeLog.Value != null) return default!;
            throw new JsonRpcInvalidParamsException(
                $"Missing required parameter '{name}' for tool '{_toolName}'");
        }
        try
        {
            return node.GetValue<T>();
        }
        catch (Exception ex)
        {
            throw new JsonRpcInvalidParamsException(
                $"Parameter '{name}' for tool '{_toolName}' has wrong type: {ex.Message}");
        }
    }

    public T? Optional<T>(string name) where T : class
    {
        ProbeLog.Value?.Add(name);
        var node = _args?[name];
        if (node == null) return null;
        try
        {
            return node.GetValue<T>();
        }
        catch (Exception ex)
        {
            throw new JsonRpcInvalidParamsException(
                $"Parameter '{name}' for tool '{_toolName}' has wrong type: {ex.Message}");
        }
    }

    public T? OptionalStruct<T>(string name) where T : struct
    {
        ProbeLog.Value?.Add(name);
        var node = _args?[name];
        if (node == null) return null;
        try
        {
            return node.GetValue<T>();
        }
        catch (Exception ex)
        {
            throw new JsonRpcInvalidParamsException(
                $"Parameter '{name}' for tool '{_toolName}' has wrong type: {ex.Message}");
        }
    }

    public T Optional<T>(string name, T fallback) where T : struct
    {
        ProbeLog.Value?.Add(name);
        var node = _args?[name];
        if (node == null) return fallback;
        try
        {
            return node.GetValue<T>();
        }
        catch (Exception ex)
        {
            throw new JsonRpcInvalidParamsException(
                $"Parameter '{name}' for tool '{_toolName}' has wrong type: {ex.Message}");
        }
    }

    public string Optional(string name, string fallback)
    {
        return Optional<string>(name) ?? fallback;
    }

    public List<string>? OptionalStringArray(string name)
    {
        ProbeLog.Value?.Add(name);
        var node = _args?[name];
        if (node == null) return null;
        if (node is not JsonArray arr)
        {
            throw new JsonRpcInvalidParamsException(
                $"Parameter '{name}' for tool '{_toolName}' must be an array of strings");
        }
        // Per-element validation must use the SAME error shape as the outer
        // wrong-shape check. The prior `e?.GetValue<string>() ?? ""` handled
        // null elements but let a non-string element (e.g. {"kinds":[42]})
        // throw an uncaught System.Text.Json.JsonException, which the
        // dispatcher's catch-all wrapped as -32603 Internal error rather than
        // the structured -32602 the rest of this class emits.
        var result = new List<string>(arr.Count);
        foreach (var e in arr)
        {
            if (e == null) continue; // null entries are silently filtered
            if (e is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.String)
            {
                throw new JsonRpcInvalidParamsException(
                    $"Parameter '{name}' for tool '{_toolName}' must be an array of strings");
            }
            var s = v.GetValue<string>();
            if (!string.IsNullOrEmpty(s)) result.Add(s);
        }
        return result;
    }

    public List<string> RequiredStringArray(string name)
    {
        var result = OptionalStringArray(name);
        if (result != null) return result;
        if (ProbeLog.Value != null) return new List<string>();
        throw new JsonRpcInvalidParamsException(
            $"Missing required array parameter '{name}' for tool '{_toolName}'");
    }

    // Raw access for callers that need to parse complex shapes (e.g., array of objects).
    public JsonNode? Raw(string name)
    {
        ProbeLog.Value?.Add(name);
        return _args?[name];
    }
}
