using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpLensMcp;

// Strict readers for array-of-object tool arguments (find_path_between's endpoints,
// change_signature's changes, check_architecture's rules, get_method_source_batch's
// methods). Every shape violation is a JsonRpcInvalidParamsException, which the
// dispatcher returns as -32602 Invalid params; a wrong shape is never read as an empty
// list, so a misshapen barrier or change set cannot silently change a tool's answer.
//
// The contract, per parameter:
//   - absent or JSON null: a required array is a -32602 ("Missing required array
//     parameter"); an optional array reads as null;
//   - present but not an array: -32602 ("must be an array of objects");
//   - an element that is not an object (JSON null included): -32602 naming the index;
//   - inside an element, a required field that is absent or null is a -32602, and a
//     present field of the wrong JSON type is a -32602 naming the full path
//     (e.g. 'changes[0].order[1]'). Absent optional fields read as null.
// Messages name the tool, in the same wording as JsonRpcParameters' scalar accessors.
//
// While JsonRpcParameters.ProbeLog is set (the schema-parity probe), a missing required
// array reads as empty instead of throwing, so one probe call still records a dispatch
// arm's complete read-set.
internal static class NestedArguments
{
    // The elements of a required array-of-object parameter.
    public static IReadOnlyList<JsonObject> RequiredObjectArray(JsonRpcParameters p, string name)
    {
        var elements = OptionalObjectArray(p, name);
        if (elements != null) return elements;
        if (JsonRpcParameters.ProbeLog.Value != null) return Array.Empty<JsonObject>();
        throw new JsonRpcInvalidParamsException(
            $"Missing required array parameter '{name}' for tool '{p.ToolName}'");
    }

    // The elements of an optional array-of-object parameter; null when absent.
    public static IReadOnlyList<JsonObject>? OptionalObjectArray(JsonRpcParameters p, string name)
    {
        var node = p.Raw(name);
        if (node is null) return null;
        if (node is not JsonArray array)
        {
            throw new JsonRpcInvalidParamsException(
                $"Parameter '{name}' for tool '{p.ToolName}' must be an array of objects");
        }

        var elements = new List<JsonObject>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject element)
            {
                throw new JsonRpcInvalidParamsException(
                    $"Parameter '{name}[{i}]' for tool '{p.ToolName}' must be an object");
            }
            elements.Add(element);
        }
        return elements;
    }

    // A required string field of an element; elementPath is the element's own path
    // (e.g. "sources[0]").
    public static string RequiredString(JsonObject element, string field, string elementPath, string toolName) =>
        OptionalString(element, field, elementPath, toolName)
        ?? throw new JsonRpcInvalidParamsException(
            $"Missing required parameter '{elementPath}.{field}' for tool '{toolName}'");

    // An optional string field of an element; null when absent or JSON null.
    public static string? OptionalString(JsonObject element, string field, string elementPath, string toolName)
    {
        if (!element.TryGetPropertyValue(field, out var node) || node is null) return null;
        return StringValue(node, $"{elementPath}.{field}", toolName);
    }

    // An optional integer field of an element; null when absent or JSON null. A
    // fractional or out-of-range number is the wrong type.
    public static int? OptionalInt(JsonObject element, string field, string elementPath, string toolName)
    {
        if (!element.TryGetPropertyValue(field, out var node) || node is null) return null;
        if (node is JsonValue value
            && value.GetValueKind() == JsonValueKind.Number
            && value.TryGetValue<int>(out var number))
        {
            return number;
        }
        throw new JsonRpcInvalidParamsException(
            $"Parameter '{elementPath}.{field}' for tool '{toolName}' must be an integer");
    }

    // An optional array-of-string field of an element; null when absent or JSON null.
    // Every element must be a string (JSON null included in the violation).
    public static List<string>? OptionalStringArray(JsonObject element, string field, string elementPath, string toolName)
    {
        if (!element.TryGetPropertyValue(field, out var node) || node is null) return null;
        var path = $"{elementPath}.{field}";
        if (node is not JsonArray array)
        {
            throw new JsonRpcInvalidParamsException(
                $"Parameter '{path}' for tool '{toolName}' must be an array of strings");
        }

        var values = new List<string>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            values.Add(StringValue(array[i], $"{path}[{i}]", toolName));
        }
        return values;
    }

    private static string StringValue(JsonNode? node, string path, string toolName)
    {
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return value.GetValue<string>();
        }
        throw new JsonRpcInvalidParamsException(
            $"Parameter '{path}' for tool '{toolName}' must be a string");
    }
}
