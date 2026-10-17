using System.Text;
using Microsoft.Build.Locator;
using SharpLensMcp;

// The JSON-RPC stream owns the process's real stdin/stdout, opened as raw streams and
// coded as UTF-8 (MCP stdio is UTF-8; the console code page would mangle non-ASCII).
// Console.Out is then routed to stderr, so anything else running in this process — an
// analyzer, a source generator, a library — that writes to Console.Out lands in the log
// instead of corrupting the protocol stream.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var protocolInput = new StreamReader(Console.OpenStandardInput(), utf8);
var protocolOutput = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
var log = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
Console.SetError(log);
Console.SetOut(log);

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
await server.RunAsync(protocolInput, protocolOutput, Environment.GetEnvironmentVariable("DOTNET_SOLUTION_PATH"));
