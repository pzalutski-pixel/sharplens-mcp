using System.Text.Json.Nodes;

namespace SharpLensMcp;

// Typed accessors for JSON-RPC tool arguments. Replaces the ~80 repeated
// `arguments?["x"]?.GetValue<T>() ?? throw new Exception("x required")` blocks
// in McpServer.HandleToolCallAsync. Missing-required and type-mismatch errors
// surface as JsonRpcInvalidParamsException, which the dispatcher converts to
// a structured -32602 response.
internal readonly struct JsonRpcParameters
{
    private readonly JsonObject? _args;
    private readonly string _toolName;

    public JsonRpcParameters(JsonObject? args, string toolName)
    {
        _args = args;
        _toolName = toolName;
    }

    public T Required<T>(string name)
    {
        var node = _args?[name];
        if (node == null)
            throw new JsonRpcInvalidParamsException(
                $"Missing required parameter '{name}' for tool '{_toolName}'");
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
        var node = _args?[name];
        if (node == null) return null;
        if (node is JsonArray arr)
        {
            return arr
                .Select(e => e?.GetValue<string>() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        throw new JsonRpcInvalidParamsException(
            $"Parameter '{name}' for tool '{_toolName}' must be an array of strings");
    }

    public List<string> RequiredStringArray(string name)
    {
        return OptionalStringArray(name)
            ?? throw new JsonRpcInvalidParamsException(
                $"Missing required array parameter '{name}' for tool '{_toolName}'");
    }

    // Raw access for callers that need to parse complex shapes (e.g., array of objects).
    public JsonNode? Raw(string name) => _args?[name];
}
