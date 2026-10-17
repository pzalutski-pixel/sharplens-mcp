using System.Text.Json;

namespace SharpLensMcp;

/// <summary>
/// Reads the NuGet restore log of a PackageReference project from its project.assets.json.
/// The file sits in the project's MSBuildProjectExtensionsPath — obj/ by default, or an
/// ancestor of the intermediate output directory under a redirected layout (artifacts
/// output, a custom BaseIntermediateOutputPath) — so both are searched, and a file is used
/// only when its project.restore.projectPath is this project.
/// </summary>
internal static class ProjectAssetsReader
{
    private const string AssetsFileName = "project.assets.json";

    // How far up from the intermediate output directory (obj/Debug/net8.0/) to look.
    private const int MaxAncestorLevels = 4;

    /// <summary>
    /// The project's restore log entries, empty when it has no assets file (an unrestored or
    /// non-PackageReference project). Warning is set when an assets file exists but cannot be read.
    /// </summary>
    public static (IReadOnlyList<RestoreLogEntry> Entries, string? Warning) Read(
        string projectPath, string? intermediateAssemblyPath)
    {
        foreach (var assetsPath in CandidateAssetsPaths(projectPath, intermediateAssemblyPath))
        {
            if (!File.Exists(assetsPath)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
                var root = document.RootElement;
                if (!IsAssetsFileOf(root, projectPath)) continue;
                return (ReadLogs(root, projectPath), null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return (Array.Empty<RestoreLogEntry>(),
                    $"Could not read the restore log '{assetsPath}' of '{projectPath}': {ex.Message}");
            }
        }
        return (Array.Empty<RestoreLogEntry>(), null);
    }

    private static IEnumerable<string> CandidateAssetsPaths(string projectPath, string? intermediateAssemblyPath)
    {
        var seen = new HashSet<string>(LoadFidelityClassifier.PathComparer);
        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (projectDirectory != null)
        {
            var defaultPath = Path.Combine(projectDirectory, "obj", AssetsFileName);
            if (seen.Add(defaultPath)) yield return defaultPath;
        }

        var directory = intermediateAssemblyPath == null ? null : Path.GetDirectoryName(intermediateAssemblyPath);
        for (var level = 0; directory != null && level <= MaxAncestorLevels; level++)
        {
            var candidate = Path.Combine(directory, AssetsFileName);
            if (seen.Add(candidate)) yield return candidate;
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static bool IsAssetsFileOf(JsonElement root, string projectPath) =>
        root.TryGetProperty("project", out var project)
        && project.TryGetProperty("restore", out var restore)
        && restore.TryGetProperty("projectPath", out var restoredPath)
        && restoredPath.ValueKind == JsonValueKind.String
        && LoadFidelityClassifier.PathComparer.Equals(
            Path.GetFullPath(restoredPath.GetString()!), Path.GetFullPath(projectPath));

    private static IReadOnlyList<RestoreLogEntry> ReadLogs(JsonElement root, string projectPath)
    {
        if (!root.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RestoreLogEntry>();
        }

        var entries = new List<RestoreLogEntry>();
        foreach (var log in logs.EnumerateArray())
        {
            entries.Add(new RestoreLogEntry(
                projectPath,
                StringProperty(log, "code"),
                StringProperty(log, "level"),
                StringProperty(log, "message")));
        }
        return entries;
    }

    private static string StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "";
}
