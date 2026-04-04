namespace SharpLensMcp;

/// <summary>
/// Structured error with recovery guidance for AI agents.
/// </summary>
public class RoslynError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Hint { get; set; }
    public object? Context { get; set; }
}
