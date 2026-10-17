using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One project as the user knows it: a project file, whatever number of target
// frameworks it builds. MSBuildWorkspace loads a multi-targeted project as one Roslyn
// Project per target framework, all with the same FilePath, named "<name>(<tfm>)" (e.g.
// "Multi(net8.0)" and "Multi(net10.0)"); a single-target project loads as one Project
// named "<name>". A LogicalProject groups those per-framework Projects back into one.
//
// Name is the plain project name ("Multi"). TargetFrameworks lists the frameworks parsed
// from the member names, sorted ordinally ("net10.0" before "net8.0"); it is empty for a
// project loaded without a framework suffix. Projects lists the members in the same
// order (a member without a framework first). LogicalProjectModel builds these from a
// Solution and resolves user-supplied project names against them.
internal sealed class LogicalProject
{
    // "<name>(<tfm>)" where <tfm> has the shape of a target framework moniker: letters,
    // then a digit, then letters, digits, dots or dashes (net8.0, netstandard2.0,
    // net8.0-windows10.0.19041, net481). A parenthesized suffix of any other shape is part
    // of the project's own name.
    private static readonly Regex FrameworkSuffix = new(
        @"^(?<name>.+)\((?<tfm>[A-Za-z]+[0-9][A-Za-z0-9.\-]*)\)$",
        RegexOptions.CultureInvariant);

    public LogicalProject(string name, string? filePath, IReadOnlyList<Project> projects)
    {
        Name = name;
        FilePath = filePath;
        Projects = projects
            .OrderBy(p => TargetFrameworkOf(p) ?? "", StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        TargetFrameworks = Projects
            .Select(TargetFrameworkOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // The plain project name, without a target-framework suffix.
    public string Name { get; }

    // The project file shared by every member; null for an in-memory project without one.
    public string? FilePath { get; }

    // The per-target-framework Roslyn projects, ordered by target framework (ordinal).
    public IReadOnlyList<Project> Projects { get; }

    // The target frameworks of the members, distinct and ordered ordinally.
    public IReadOnlyList<string> TargetFrameworks { get; }

    // True when the project loaded as more than one per-framework Project.
    public bool IsMultiTargeted => Projects.Count > 1;

    // The target framework a Roslyn project was loaded for, parsed from its
    // "<name>(<tfm>)" name; null when the name carries no framework suffix.
    public static string? TargetFrameworkOf(Project project) => SplitName(project.Name).TargetFramework;

    // Splits a project name into the plain name and the target framework of its
    // "<name>(<tfm>)" suffix. A name without such a suffix is returned whole, with a null
    // framework.
    public static (string Name, string? TargetFramework) SplitName(string projectName)
    {
        var match = FrameworkSuffix.Match(projectName);
        return match.Success
            ? (match.Groups["name"].Value, match.Groups["tfm"].Value)
            : (projectName, null);
    }
}
