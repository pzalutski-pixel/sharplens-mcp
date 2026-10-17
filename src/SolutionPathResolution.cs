namespace SharpLensMcp;

/// <summary>
/// The outcome of resolving DOTNET_SOLUTION_PATH: the solution file to load, or why there
/// is none. Exactly one of the two is non-null.
/// </summary>
public sealed record SolutionPathResolution(string? Path, string? Error);
