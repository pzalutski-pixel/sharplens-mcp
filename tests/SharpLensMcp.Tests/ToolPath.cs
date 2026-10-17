namespace SharpLensMcp.Tests;

// The two levels a tool test drives a tool at (testing charter C4): the RoslynService
// method itself, or the MCP dispatcher over JSON-RPC (tools/call, content[0].text).
public enum ToolPath
{
    Service,
    Dispatcher
}
