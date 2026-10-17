using System.Text.Json;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace SharpLensMcp;

/// <summary>
/// Reads the projects a solution file declares, the same way MSBuildWorkspace does: .sln and
/// .slnx through Microsoft.VisualStudio.SolutionPersistence (the reader MSBuildWorkspace
/// itself uses), with project paths resolved against the solution directory; a .slnf
/// solution filter narrows its solution to the projects it lists. Solution folders are not
/// projects and never appear.
/// </summary>
internal static class SolutionProjectReader
{
    private static readonly string[] CompilingProjectExtensions = { ".csproj", ".vbproj", ".fsproj" };

    /// <summary>The declared projects, or null when the solution file cannot be read.</summary>
    public static async Task<DeclaredSolutionProjects?> ReadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        try
        {
            var solutionFile = Path.GetFullPath(solutionPath);
            HashSet<string>? filter = null;
            if (solutionFile.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
            {
                (solutionFile, filter) = ReadSolutionFilter(solutionFile);
            }

            var serializer = SolutionSerializers.GetSerializerByMoniker(solutionFile);
            if (serializer == null) return null;
            var model = await serializer.OpenAsync(solutionFile, cancellationToken);
            var baseDirectory = Path.GetDirectoryName(solutionFile)!;

            var compiling = new List<string>();
            var other = new List<string>();
            foreach (var project in model.SolutionProjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveProjectPath(baseDirectory, project.FilePath);
                if (filter != null && !filter.Contains(path)) continue;
                (IsCompilingProject(path) ? compiling : other).Add(path);
            }
            return new DeclaredSolutionProjects(compiling, other);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException
                                       or Microsoft.VisualStudio.SolutionPersistence.Model.SolutionException)
        {
            // An unreadable solution file: the declared set is unknown, and the caller reports
            // the load without the declared cross-check.
            return null;
        }
    }

    public static bool IsCompilingProject(string projectPath) =>
        CompilingProjectExtensions.Contains(Path.GetExtension(projectPath), StringComparer.OrdinalIgnoreCase);

    // A solution's project paths are relative to its directory and written with the
    // solution author's separator; normalize to an absolute path for this platform.
    private static string ResolveProjectPath(string baseDirectory, string projectFilePath) =>
        Path.GetFullPath(Path.Combine(baseDirectory,
            projectFilePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    // A solution filter is JSON: { "solution": { "path": "<sln relative to the .slnf>",
    // "projects": [ "<project relative to the sln>", ... ] } }.
    private static (string SolutionPath, HashSet<string> Projects) ReadSolutionFilter(string filterPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filterPath));
        var solution = document.RootElement.GetProperty("solution");
        var solutionPath = ResolveProjectPath(Path.GetDirectoryName(filterPath)!, solution.GetProperty("path").GetString()!);
        var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        var projects = new HashSet<string>(LoadFidelityClassifier.PathComparer);
        if (solution.TryGetProperty("projects", out var projectArray))
        {
            foreach (var project in projectArray.EnumerateArray())
            {
                projects.Add(ResolveProjectPath(solutionDirectory, project.GetString()!));
            }
        }
        return (solutionPath, projects);
    }
}
