namespace SharpLensMcp;

/// <summary>
/// Environment-variable lookup with legacy-name fallback. SHARPLENS_* is the
/// branded spelling; the 1.5.x ROSLYN_* spellings remain readable as a
/// migration fallback for one release.
/// </summary>
internal static class EnvironmentConfig
{
    public static string? Get(string name, string legacyName) =>
        Environment.GetEnvironmentVariable(name)
        ?? Environment.GetEnvironmentVariable(legacyName);
}
