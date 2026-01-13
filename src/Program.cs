using Microsoft.Build.Locator;
using SharpLensMcp;

// Check for worker mode flag
if (args.Contains("--worker"))
{
    // Worker mode: handles Roslyn analysis in a separate process
    // Register MSBuild before any Roslyn code runs
    MSBuildLocator.RegisterDefaults();

    // Create and run the worker host (stdin/stdout JSON-RPC)
    var roslynService = new RoslynService();
    var workerHost = new WorkerHost(roslynService);
    await workerHost.RunAsync();
}
else
{
    // MCP server mode: spawns worker process, proxies tool calls
    // Note: MSBuildLocator not registered here - worker handles that
    using var processManager = new ProcessManager();
    var server = new McpServer(processManager);
    await server.RunAsync();
}
