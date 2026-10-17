using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

/// <summary>
/// Decides how complete a solution load is from what actually loaded — a pure function over
/// the declared projects, the loaded projects' state, the workspace diagnostics, and the
/// NuGet restore logs. A workspace diagnostic's kind cannot be trusted (MSBuildWorkspace
/// reports every relayed MSBuild message, warning or error, as a Failure), so:
/// <list type="bullet">
/// <item>a relayed MSBuild message is a failure when its project is absent or an empty
/// placeholder, and a warning when its project loaded completely;</item>
/// <item>a workspace-originated Failure (project file not found, exceptions) is a failure,
/// except "cannot open" for a declared non-compiling project type, which is a warning;</item>
/// <item>a workspace Warning is a warning;</item>
/// <item>each declared compiling project that did not load, each placeholder, and each loaded
/// project whose references do not define System.Object is a failure;</item>
/// <item>a restore log Error is a failure and a restore Warning a warning — and supersedes
/// the relayed copy of the same message, which lost its severity and code.</item>
/// </list>
/// </summary>
internal static class LoadFidelityClassifier
{
    /// <summary>File-system path comparison: case-insensitive on Windows, ordinal elsewhere.</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static LoadFidelity Classify(
        DeclaredSolutionProjects? declared,
        IReadOnlyList<LoadedProjectState> loaded,
        IReadOnlyList<WorkspaceDiagnostic> diagnostics,
        IReadOnlyList<RestoreLogEntry> restoreLog)
    {
        var failures = new List<string>();
        var warnings = new List<string>();

        var projectsByPath = loaded
            .GroupBy(p => Normalize(p.FilePath), PathComparer)
            .ToDictionary(g => g.Key, g => g.ToList(), PathComparer);
        bool LoadedCompletely(string path) =>
            projectsByPath.TryGetValue(Normalize(path), out var instances) && instances.All(p => !p.IsPlaceholder);

        // Relayed messages superseded by the restore log entry they copy (same project, same text).
        var restoreMessagesByProject = restoreLog
            .Where(e => e.Message.Length > 0)
            .GroupBy(e => Normalize(e.ProjectPath), PathComparer)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToList(), PathComparer);

        foreach (var diagnostic in diagnostics)
        {
            if (MSBuildRelayMessage.TryParse(diagnostic.Message, out var projectPath, out var innerMessage))
            {
                if (!LoadedCompletely(projectPath))
                {
                    failures.Add(diagnostic.Message);
                }
                else if (!(restoreMessagesByProject.TryGetValue(Normalize(projectPath), out var restoreMessages)
                           && restoreMessages.Any(m => innerMessage.Contains(m, StringComparison.Ordinal))))
                {
                    warnings.Add(diagnostic.Message);
                }
            }
            else if (diagnostic.Kind == WorkspaceDiagnosticKind.Warning)
            {
                warnings.Add(diagnostic.Message);
            }
            else if (declared != null && declared.OtherProjectPaths.Any(p => diagnostic.Message.Contains(p, PathComparison)))
            {
                warnings.Add(diagnostic.Message);
            }
            else
            {
                failures.Add(diagnostic.Message);
            }
        }

        if (declared != null)
        {
            foreach (var path in declared.CompilingProjectPaths.Where(p => !projectsByPath.ContainsKey(Normalize(p))))
            {
                failures.Add($"Project '{path}' is declared in the solution but did not load.");
            }
        }

        foreach (var (path, instances) in projectsByPath)
        {
            if (instances.Any(p => p.IsPlaceholder))
            {
                failures.Add($"Project '{path}' did not load: MSBuild could not evaluate it, so it is an empty placeholder with no documents.");
                continue;
            }
            foreach (var project in instances)
            {
                if (project.MetadataReferenceCount == 0)
                {
                    failures.Add($"Project '{project.Name}' ({path}) loaded without any metadata references, so none of its code binds (not even System.Object); run 'dotnet restore' and reload.");
                }
                else if (!project.ResolvesCoreLibrary)
                {
                    failures.Add($"Project '{project.Name}' ({path}) loaded with {project.MetadataReferenceCount} metadata references, but none defines System.Object, so none of its code binds; run 'dotnet restore' and reload.");
                }
            }
        }

        foreach (var entry in restoreLog)
        {
            var text = $"Restore {entry.Code} in '{entry.ProjectPath}': {entry.Message}";
            if (string.Equals(entry.Level, "Error", StringComparison.OrdinalIgnoreCase)) failures.Add(text);
            else if (string.Equals(entry.Level, "Warning", StringComparison.OrdinalIgnoreCase)) warnings.Add(text);
        }

        var loadedProjectCount = projectsByPath.Count(kv => kv.Value.All(p => !p.IsPlaceholder));
        return new LoadFidelity(declared?.CompilingProjectPaths.Count, loadedProjectCount, failures, warnings);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
