namespace SharpLensMcp;

// One package reference of an evaluated project (MSBuildProjectEvaluator.PackageReferences):
// its id, its version (VersionOverride, Version, or the central PackageVersion; null when
// none is set) and its asset metadata, each null when not set.
internal sealed record EvaluatedPackageReference(
    string Name, string? Version, string? PrivateAssets, string? ExcludeAssets, string? IncludeAssets)
{
    // Whether the reference keeps its compile assets out of the compilation
    // (ExcludeAssets lists compile or all, or IncludeAssets lists neither).
    public bool ExcludesCompileAssets =>
        ListsAsset(ExcludeAssets, "compile")
        || (IncludeAssets != null && !ListsAsset(IncludeAssets, "compile"));

    private static bool ListsAsset(string? assets, string asset) =>
        assets != null && assets.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(a => a.Equals(asset, StringComparison.OrdinalIgnoreCase) || a.Equals("all", StringComparison.OrdinalIgnoreCase));
}
