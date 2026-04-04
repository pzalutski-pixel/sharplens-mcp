namespace SharpLensMcp;

/// <summary>
/// Response metadata for AI workflow guidance.
/// </summary>
public class ResponseMetadata
{
    public int? TotalCount { get; set; }
    public int? ReturnedCount { get; set; }
    public bool? Truncated { get; set; }
    public string[]? SuggestedNextTools { get; set; }
    public string? Verbosity { get; set; }
}
