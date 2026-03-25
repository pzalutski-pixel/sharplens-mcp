using Microsoft.Build.Locator;
using SharpLensMcp;

// Register MSBuild before any Roslyn code runs
try
{
    MSBuildLocator.RegisterDefaults();
}
catch (InvalidOperationException ex) when (ex.Message.Contains("No instances"))
{
    Console.Error.WriteLine("Error: No compatible MSBuild/SDK found.");
    Console.Error.WriteLine("SharpLensMcp requires .NET 8.0 SDK or later.");
    Console.Error.WriteLine($"Details: {ex.Message}");
    return;
}

// Create and run the MCP server
var server = new McpServer();
await server.RunAsync();
