namespace SharpLensMcp;

/// <summary>
/// How faithfully the loaded in-memory solution reflects the solution on disk, captured at
/// load so a partial load is reported, never silent. Failures are what makes the load
/// incomplete: a declared project that did not load, a project MSBuild could not evaluate
/// (an empty placeholder), a project without usable references (unrestored), a NuGet
/// restore error, or a workspace-level failure. Warnings are messages about projects that
/// did load completely — MSBuild warnings and errors from their design-time build, NuGet
/// restore warnings, non-compiling project types the workspace skips.
/// </summary>
/// <param name="DeclaredProjectCount">Compiling projects the solution file declares; null when it could not be read.</param>
/// <param name="LoadedProjectCount">Distinct project files that loaded completely (a multi-targeted project counts once).</param>
public sealed record LoadFidelity(
    int? DeclaredProjectCount,
    int LoadedProjectCount,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Something the solution declares is missing or unusable in the loaded workspace.
    /// Warnings never make a load partial.
    /// </summary>
    public bool IsPartial => Failures.Count > 0;

    /// <summary>
    /// The clean, complete state for an in-memory workspace (the test seam) that has no
    /// MSBuild load step and therefore no drop diagnostics.
    /// </summary>
    public static LoadFidelity Complete(int loadedProjectCount) =>
        new(loadedProjectCount, loadedProjectCount, Array.Empty<string>(), Array.Empty<string>());
}
