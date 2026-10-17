using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The result of LogicalProjectModel.Resolve for one user-supplied project name.
//   - Resolved: Project is the addressed logical project and Projects the Roslyn
//     projects to scan — every target framework for a plain name ("Multi"), the one
//     member for a framework-qualified name ("Multi(net8.0)").
//   - NotFound: Projects is empty and Project null.
//   - Ambiguous: Projects is empty, Project null, and Candidates lists the logical
//     projects the name matched (ordered as the model orders them).
// ValidNames is every name the model accepts, for the error a tool returns.
internal sealed class ProjectResolution
{
    private ProjectResolution(
        ProjectResolutionStatus status, string requestedName, LogicalProject? project,
        IReadOnlyList<Project> projects, IReadOnlyList<LogicalProject> candidates, IReadOnlyList<string> validNames)
    {
        Status = status;
        RequestedName = requestedName;
        Project = project;
        Projects = projects;
        Candidates = candidates;
        ValidNames = validNames;
    }

    public ProjectResolutionStatus Status { get; }
    public string RequestedName { get; }
    public LogicalProject? Project { get; }
    public IReadOnlyList<Project> Projects { get; }
    public IReadOnlyList<LogicalProject> Candidates { get; }
    public IReadOnlyList<string> ValidNames { get; }

    public static ProjectResolution Resolved(
        string requestedName, LogicalProject project, IReadOnlyList<Project> projects, IReadOnlyList<string> validNames) =>
        new(ProjectResolutionStatus.Resolved, requestedName, project, projects, Array.Empty<LogicalProject>(), validNames);

    public static ProjectResolution NotFound(string requestedName, IReadOnlyList<string> validNames) =>
        new(ProjectResolutionStatus.NotFound, requestedName, null, Array.Empty<Project>(), Array.Empty<LogicalProject>(), validNames);

    public static ProjectResolution Ambiguous(
        string requestedName, IReadOnlyList<LogicalProject> candidates, IReadOnlyList<string> validNames) =>
        new(ProjectResolutionStatus.Ambiguous, requestedName, null, Array.Empty<Project>(), candidates, validNames);
}
