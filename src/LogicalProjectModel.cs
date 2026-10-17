using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The solution's projects as the user knows them: every Roslyn Project grouped by its
// project file into a LogicalProject (see LogicalProject for the multi-targeting shape).
// Projects with the same FilePath (compared case-insensitively on Windows, ordinally
// elsewhere, after full-path normalization) form one logical project; a project without
// a FilePath is a logical project of its own.
//
// Projects is ordered by Name (ordinal ignoring case, then ordinal), then FilePath
// (ordinal), so a solution-wide scan that walks it visits projects in a reproducible
// order. A solution-wide scan walks every member of every logical project (each target
// framework compiles its own #if branches) and collapses the per-framework duplicates
// with TargetFrameworkDeduplication.
//
// Name resolution (Resolve) accepts, case-insensitively, the plain name of a logical
// project ("Multi": every target framework) or the name of one member ("Multi(net8.0)":
// that framework alone). When the name matches several distinct scopes, the scopes
// whose name matches with exact case win; if that still leaves several, the result is
// Ambiguous. ValidNames lists every accepted name — each logical project's plain name
// followed by its framework-qualified member names — in model order.
internal sealed class LogicalProjectModel
{
    private readonly Dictionary<ProjectId, LogicalProject> _byProjectId;

    private LogicalProjectModel(IReadOnlyList<LogicalProject> projects)
    {
        Projects = projects;
        _byProjectId = projects
            .SelectMany(logical => logical.Projects.Select(member => (member.Id, logical)))
            .ToDictionary(pair => pair.Id, pair => pair.logical);
        ValidNames = projects
            .SelectMany(logical => new[] { logical.Name }
                .Concat(logical.Projects.Select(p => p.Name).Where(n => n != logical.Name)))
            .ToList();
    }

    public IReadOnlyList<LogicalProject> Projects { get; }

    // Every name Resolve accepts, in model order.
    public IReadOnlyList<string> ValidNames { get; }

    public static LogicalProjectModel Build(Solution solution)
    {
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var groups = new Dictionary<string, List<Project>>(pathComparer);
        var standalone = new List<Project>();
        foreach (var project in solution.Projects)
        {
            if (string.IsNullOrEmpty(project.FilePath))
            {
                standalone.Add(project);
                continue;
            }
            var key = Path.GetFullPath(project.FilePath);
            if (!groups.TryGetValue(key, out var members)) groups[key] = members = new List<Project>();
            members.Add(project);
        }

        var logical = groups.Values
            .Select(members => Create(members, members[0].FilePath))
            .Concat(standalone.Select(project => Create(new[] { project }, null)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.FilePath ?? "", StringComparer.Ordinal)
            .ToList();
        return new LogicalProjectModel(logical);
    }

    // The plain name is taken from the ordinally-first member's name; MSBuild gives every
    // member of one project file the same plain name.
    private static LogicalProject Create(IReadOnlyList<Project> members, string? filePath)
    {
        var first = members.OrderBy(p => p.Name, StringComparer.Ordinal).First();
        return new LogicalProject(LogicalProject.SplitName(first.Name).Name, filePath, members);
    }

    // The logical project a Roslyn project belongs to.
    public LogicalProject Of(Project project) => Of(project.Id);

    public LogicalProject Of(ProjectId projectId) =>
        _byProjectId.TryGetValue(projectId, out var logical)
            ? logical
            : throw new ArgumentException($"Project {projectId} is not part of this model", nameof(projectId));

    public ProjectResolution Resolve(string projectName)
    {
        var scopes = new List<(LogicalProject Project, IReadOnlyList<Project> Members, string MatchedName)>();
        foreach (var logical in Projects)
        {
            if (string.Equals(logical.Name, projectName, StringComparison.OrdinalIgnoreCase))
            {
                scopes.Add((logical, logical.Projects, logical.Name));
            }
            foreach (var member in logical.Projects)
            {
                if (member.Name != logical.Name
                    && string.Equals(member.Name, projectName, StringComparison.OrdinalIgnoreCase))
                {
                    scopes.Add((logical, new[] { member }, member.Name));
                }
            }
        }

        if (scopes.Count > 1)
        {
            var exact = scopes.Where(s => string.Equals(s.MatchedName, projectName, StringComparison.Ordinal)).ToList();
            if (exact.Count == 1) scopes = exact;
        }

        return scopes.Count switch
        {
            0 => ProjectResolution.NotFound(projectName, ValidNames),
            1 => ProjectResolution.Resolved(projectName, scopes[0].Project, scopes[0].Members, ValidNames),
            _ => ProjectResolution.Ambiguous(
                projectName, scopes.Select(s => s.Project).Distinct().ToList(), ValidNames)
        };
    }
}
