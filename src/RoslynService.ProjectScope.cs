using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// Project-scope resolution for project-scoped tools: the RoslynService-side entry point
// of the logical-project model (LogicalProjectModel), with its canonical errors.
public partial class RoslynService
{
    // The loaded solution's logical projects (every per-target-framework Project
    // grouped by project file).
    internal LogicalProjectModel GetLogicalProjects()
    {
        EnsureSolutionLoaded();
        return LogicalProjectModel.Build(_solution!);
    }

    // The Roslyn projects a project-scoped tool scans for an optional projectName:
    //   - null, empty or whitespace: every project of the solution, in logical-project
    //     order (each logical project's members together, ordered by target framework);
    //   - a plain name ("Multi") or a framework-qualified name ("Multi(net8.0)"), matched
    //     case-insensitively: the resolved scope (LogicalProjectModel.Resolve);
    //   - anything else: Error is the canonical envelope (ProjectResolutionError) and
    //     Projects is null.
    internal (IReadOnlyList<Project>? Projects, object? Error) ResolveProjectScope(string? projectName)
    {
        var model = GetLogicalProjects();
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return (model.Projects.SelectMany(p => p.Projects).ToList(), null);
        }

        var resolution = model.Resolve(projectName);
        return resolution.Status == ProjectResolutionStatus.Resolved
            ? (resolution.Projects, null)
            : (null, ProjectResolutionError(resolution));
    }

    // The canonical error for an unresolved project name.
    //   - NotFound: INVALID_PARAMETER "Project not found: <name>", the hint listing every
    //     accepted name, context { projectName, availableProjects }.
    //   - Ambiguous: AMBIGUOUS_MATCH, context { projectName, candidates[{ name, filePath }] }.
    internal object ProjectResolutionError(ProjectResolution resolution)
    {
        switch (resolution.Status)
        {
            case ProjectResolutionStatus.Ambiguous:
                return CreateErrorResponse(
                    ErrorCodes.AmbiguousMatch,
                    $"Project name '{resolution.RequestedName}' matches {resolution.Candidates.Count} projects",
                    hint: "Pass the project name with its exact case; projects that share one name cannot be told apart by name.",
                    context: new
                    {
                        projectName = resolution.RequestedName,
                        candidates = resolution.Candidates
                            .Select(c => new { name = c.Name, filePath = c.FilePath == null ? null : FormatPath(c.FilePath) })
                            .ToList()
                    });
            case ProjectResolutionStatus.NotFound:
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"Project not found: {resolution.RequestedName}",
                    hint: $"Available projects: {string.Join(", ", resolution.ValidNames)}",
                    context: new { projectName = resolution.RequestedName, availableProjects = resolution.ValidNames });
            default:
                throw new ArgumentException("A resolved project name has no error", nameof(resolution));
        }
    }
}
