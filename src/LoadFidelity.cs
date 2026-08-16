namespace SharpLensMcp;

/// <summary>
/// How faithfully the loaded in-memory solution reflects the solution on disk: how many
/// projects the solution file declares vs. how many actually loaded, plus the MSBuild
/// workspace diagnostics (a failure means a project was dropped — an unresolvable
/// ProjectReference, an unsupported project type, a missing SDK; a warning flags
/// reference/restore problems). Captured at load so a partial load is reported, never silent.
/// </summary>
public sealed record LoadFidelity(
    int? DeclaredProjectCount,
    int LoadedProjectCount,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// A project was dropped (a load failure) or fewer projects loaded than the solution
    /// file declares. Warnings do NOT make a load partial — a healthy solution routinely
    /// emits benign workspace warnings.
    /// </summary>
    public bool IsPartial =>
        Failures.Count > 0 ||
        (DeclaredProjectCount is { } declared && LoadedProjectCount < declared);

    /// <summary>
    /// The clean, complete state for an in-memory workspace (the test seam) that has no
    /// MSBuild load step and therefore no drop diagnostics.
    /// </summary>
    public static LoadFidelity Complete(int loadedProjectCount) =>
        new(loadedProjectCount, loadedProjectCount, Array.Empty<string>(), Array.Empty<string>());
}
