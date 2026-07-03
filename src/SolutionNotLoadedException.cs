namespace SharpLensMcp;

// Thrown by RoslynService.EnsureSolutionLoaded when a tool runs before
// load_solution. McpServer.HandleToolCallAsync converts it into the standard
// structured error envelope (SOLUTION_NOT_LOADED) instead of the opaque
// -32603 a bare exception would produce.
internal sealed class SolutionNotLoadedException : Exception
{
    public SolutionNotLoadedException()
        : base("No solution loaded. Call load_solution first or set DOTNET_SOLUTION_PATH environment variable.")
    {
    }
}
