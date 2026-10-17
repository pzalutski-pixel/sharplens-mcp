using System.Text.Json;

namespace SharpLensMcp;

// Which NuGet package a restored reference assembly comes from, read from the project's
// restore assets (project.assets.json): every package library's folder ("path") under
// every package folder the restore used ("packageFolders" — the global packages folder
// wherever RestorePackagesPath / NUGET_PACKAGES put it, and any fallback folders). A
// reference path inside a package's folder belongs to that package.
internal sealed class RestoredPackageMap
{
    // Paths compare as the file system does: case-insensitively on Windows.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly List<(string Directory, string PackageId)> _packageDirectories;

    private RestoredPackageMap(List<(string Directory, string PackageId)> packageDirectories)
    {
        // Longest first, so a package folder nested in another's resolves to the inner one.
        _packageDirectories = packageDirectories.OrderByDescending(p => p.Directory.Length).ToList();
    }

    // The map of an assets file; null when the file does not exist or cannot be read
    // (the warning says why for an unreadable file).
    public static (RestoredPackageMap? Map, string? Warning) Load(string? assetsPath)
    {
        if (assetsPath == null || !File.Exists(assetsPath)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            var root = document.RootElement;
            var folders = root.TryGetProperty("packageFolders", out var packageFolders) && packageFolders.ValueKind == JsonValueKind.Object
                ? packageFolders.EnumerateObject().Select(f => f.Name).ToList()
                : new List<string>();
            var directories = new List<(string, string)>();
            if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
                    if (!library.Value.TryGetProperty("path", out var path) || path.GetString() is not { Length: > 0 } relative) continue;
                    var id = library.Name.Split('/')[0];
                    foreach (var folder in folders)
                    {
                        directories.Add((Normalize(Path.Combine(folder, relative)), id));
                    }
                }
            }
            return (new RestoredPackageMap(directories), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"{Path.GetFileName(assetsPath)}: {ex.Message}");
        }
    }

    // The id (as the assets file spells it) of the package the file is in; null when it is
    // in no restored package (a framework reference pack, a project output).
    public string? PackageIdOf(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var normalized = Normalize(filePath);
        foreach (var (directory, id) in _packageDirectories)
        {
            if (normalized.StartsWith(directory, PathComparison)) return id;
        }
        return null;
    }

    // A full path with forward slashes and a trailing separator, so a prefix test matches
    // whole directory names only.
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        return full.EndsWith('/') ? full : full + "/";
    }
}
