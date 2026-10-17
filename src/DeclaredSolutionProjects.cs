namespace SharpLensMcp;

/// <summary>
/// The projects a solution file declares, as absolute paths, split by whether they compile
/// to .NET code. Compiling projects (.csproj, .vbproj, .fsproj) are what a complete load must
/// contain. Everything else — shared projects (.shproj, whose files are compiled into the
/// projects that import them), C++, SQL, installer, docker-compose, and other project types
/// — is declared but has nothing of its own to load, so it never makes a load partial.
/// </summary>
public sealed record DeclaredSolutionProjects(
    IReadOnlyList<string> CompilingProjectPaths,
    IReadOnlyList<string> OtherProjectPaths);
