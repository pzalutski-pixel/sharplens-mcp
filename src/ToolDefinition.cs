namespace SharpLensMcp;

/// <summary>
/// One record per MCP tool: the published schema object and the handler that
/// serves it live side by side, so a tool cannot be published without a
/// handler, dispatched without a schema, or drift between separate lists.
/// ReadOnly drives the published MCP annotations (readOnlyHint /
/// destructiveHint) and is deliberately required — a new tool must declare
/// whether it mutates state rather than inherit a default.
/// </summary>
internal sealed record ToolDefinition(
    string Name,
    bool ReadOnly,
    object Schema,
    Func<RoslynService, JsonRpcParameters, CancellationToken, Task<object>> Handler);
