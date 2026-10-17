namespace SharpLensMcp;

/// <summary>
/// The most recent solution load that did not complete — rejected input, an unreadable
/// solution file, a cancelled or timed-out load, or a failed startup auto-load. Kept so
/// health_check can explain why the loaded solution (if any) is not the one last requested.
/// Cleared by the next successful load.
/// </summary>
public sealed record LoadFailure(string SolutionPath, string Code, string Message, DateTime FailedAtUtc);
