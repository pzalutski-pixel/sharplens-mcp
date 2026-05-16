namespace SharpLensMcp;

// Thrown by JsonRpcParameters when a required argument is missing or has the wrong
// type. McpServer.HandleToolCallAsync catches this and converts to a -32602
// "Invalid params" error per JSON-RPC 2.0 §5.1.
internal sealed class JsonRpcInvalidParamsException : Exception
{
    public JsonRpcInvalidParamsException(string message) : base(message) { }
}
