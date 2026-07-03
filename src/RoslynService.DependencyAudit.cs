using Microsoft.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpLensMcp;

// find_unused_dependencies: which declared PackageReference / ProjectReference
// entries the compiler never actually needs — per Roslyn's own
// GetUsedAssemblyReferences verdict. Skip rules are REPORTED, never silent:
// PrivateAssets=all entries (build-time assets) and packages contributing no
// compile-time assemblies (analyzers/source generators) cannot be judged by
// this method. Compile-time verdict only: a package loaded purely via
// reflection (plugins, MEF) appears unused — the schema says verify first.
public partial class RoslynService
{
    private static readonly Regex NuGetPackagePathRegex = new(
        @"[\\/]\.nuget[\\/]packages[\\/](?<id>[^\\/]+)[\\/]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Restored package references live under .nuget/packages/{id}/{version}/;
    // anything else (framework ref packs, project outputs) maps to null.
    internal static string? PackageIdFromReferencePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var match = NuGetPackagePathRegex.Match(path);
        return match.Success ? match.Groups["id"].Value.ToLowerInvariant() : null;
    }

    public async Task<object> FindUnusedDependenciesAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // An unknown project NAME is an invalid argument: route it through the shared
        // ProjectNotFoundError (InvalidParameter). A name that matches but whose file is
        // missing on disk is a distinct condition reported below.
        if (projectName != null && !_solution!.Projects.Any(
                p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectNotFoundError(projectName);
        }

        var projects = (projectName == null
                ? _solution!.Projects
                : _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
            .Where(p => p.FilePath != null && File.Exists(p.FilePath))
            .ToList();
        if (projects.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotFound,
                projectName == null
                    ? "The solution has no project files on disk to audit"
                    : $"Project '{projectName}' has no project file on disk to audit",
                hint: $"Available projects: {string.Join(", ", _solution!.Projects.Select(p => p.Name))}",
                context: new { projectName });
        }

        var results = new List<object>();
        var totalUnused = 0;

        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            // Which package ids contribute compile references at all, and which
            // of those the compiler actually needs.
            var allPackageIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
            {
                if (PackageIdFromReferencePath(reference.FilePath ?? reference.Display) is { } id)
                    allPackageIds.Add(id);
            }

            var usedPackageIds = new HashSet<string>(StringComparer.Ordinal);
            // Identity, not name: the assembly SYMBOLS the compiler actually used through
            // project-to-project (CompilationReference) references. Matching a project
            // reference by its assembly NAME let an unrelated package whose output name
            // collided mark the reference as used.
            var usedProjectAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            foreach (var reference in compilation.GetUsedAssemblyReferences(cancellationToken))
            {
                switch (reference)
                {
                    case PortableExecutableReference pe:
                        var path = pe.FilePath ?? pe.Display;
                        if (PackageIdFromReferencePath(path) is { } usedId) usedPackageIds.Add(usedId);
                        break;
                    case CompilationReference cr:
                        usedProjectAssemblies.Add(cr.Compilation.Assembly);
                        break;
                }
            }

            var unusedPackages = new List<object>();
            var skipped = new List<object>();
            var packagesChecked = 0;

            // A malformed Directory.Build.props / Directory.Packages.props / csproj is
            // reported via parseError, not silently swallowed — same as get_nuget_dependencies.
            var parseErrors = new List<string>();
            foreach (var packageRef in CollectEffectivePackageReferences(project.FilePath!, parseErrors))
            {
                var name = packageRef.Name;
                packagesChecked++;
                var version = packageRef.Version ?? "unknown";
                var privateAssets = packageRef.PrivateAssets;

                if (string.Equals(privateAssets, "all", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(new { name, reason = "PrivateAssets=all - a build-time asset, never a compile reference" });
                    continue;
                }
                var packageId = name.ToLowerInvariant();
                if (!allPackageIds.Contains(packageId))
                {
                    skipped.Add(new { name, reason = "Contributes no compile-time assemblies (analyzer, source generator, or build asset)" });
                    continue;
                }
                if (!usedPackageIds.Contains(packageId))
                {
                    unusedPackages.Add(new { name, version });
                }
            }

            var unusedProjectReferences = new List<object>();
            var projectReferencesChecked = 0;
            foreach (var projectReference in project.ProjectReferences)
            {
                var referenced = _solution!.GetProject(projectReference.ProjectId);
                if (referenced == null) continue;
                projectReferencesChecked++;
                // The referenced project's own assembly symbol; "used" iff it is among the
                // symbols the compiler actually pulled in — by identity, not name.
                var referencedAssembly = (await referenced.GetCompilationAsync(cancellationToken))?.Assembly;
                var used = referencedAssembly != null && usedProjectAssemblies.Contains(referencedAssembly);
                if (!used)
                {
                    unusedProjectReferences.Add(new { name = referenced.Name });
                }
            }

            totalUnused += unusedPackages.Count + unusedProjectReferences.Count;
            results.Add(new
            {
                projectName = project.Name,
                packagesChecked,
                projectReferencesChecked,
                unusedPackages,
                unusedProjectReferences,
                skipped,
                parseError = parseErrors.Count > 0 ? string.Join("; ", parseErrors) : null
            });
        }

        return CreateSuccessResponse(
            data: new { projects = results },
            suggestedNextTools: totalUnused > 0
                ? new[]
                {
                    "find_reflection_usage before removing anything - reflection-loaded packages appear unused here",
                    "Remove the confirmed-unused references from the csproj and rebuild"
                }
                : new[] { "Every declared dependency is compile-used (or accounted for in skipped)" },
            totalCount: totalUnused,
            returnedCount: totalUnused);
    }

    internal sealed record EffectivePackageReference(
        string Name, string? Version, string? PrivateAssets, string? ExcludeAssets);

    // The package references in effect for a project, merging the leaf csproj with
    // any Directory.Build.props up the tree (declared packages), and resolving a
    // Version-less reference (Central Package Management) against the nearest
    // Directory.Packages.props <PackageVersion>. Pure XML reads — no MSBuild
    // evaluation — covering the two gaps the leaf-only parse missed: CPM versions
    // showing "unknown" and props-declared packages being invisible.
    // parseErrors, when supplied, collects "file: message" for each malformed
    // props/csproj that could not be parsed — the scan still degrades gracefully,
    // but the failure is surfaced rather than silently swallowed.
    internal static List<EffectivePackageReference> CollectEffectivePackageReferences(
        string projectFilePath, ICollection<string>? parseErrors = null)
    {
        var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var propsReferences = new List<XElement>();

        for (var dir = Path.GetDirectoryName(projectFilePath); dir != null; dir = Path.GetDirectoryName(dir))
        {
            MergeCentralVersions(Path.Combine(dir, "Directory.Packages.props"), centralVersions, parseErrors);
            CollectPackageReferenceElements(Path.Combine(dir, "Directory.Build.props"), propsReferences, parseErrors);
        }

        var leafReferences = new List<XElement>();
        CollectPackageReferenceElements(projectFilePath, leafReferences, parseErrors);

        var result = new List<EffectivePackageReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Leaf first so it wins on a duplicate include.
        foreach (var element in leafReferences.Concat(propsReferences))
        {
            var name = element.Attribute("Include")?.Value;
            if (name == null || !seen.Add(name)) continue; // Update= entries declare nothing
            var version = element.Attribute("Version")?.Value
                ?? element.Element("Version")?.Value
                ?? centralVersions.GetValueOrDefault(name);
            result.Add(new EffectivePackageReference(
                name,
                version,
                element.Attribute("PrivateAssets")?.Value ?? element.Element("PrivateAssets")?.Value,
                element.Attribute("ExcludeAssets")?.Value ?? element.Element("ExcludeAssets")?.Value));
        }
        return result;
    }

    private static void MergeCentralVersions(
        string path, Dictionary<string, string> versions, ICollection<string>? parseErrors)
    {
        if (!File.Exists(path)) return;
        try
        {
            foreach (var element in XDocument.Parse(File.ReadAllText(path)).Descendants("PackageVersion"))
            {
                var name = element.Attribute("Include")?.Value;
                var version = element.Attribute("Version")?.Value ?? element.Element("Version")?.Value;
                // Nearest file wins (walked project-dir first): add only if absent.
                if (name != null && version != null) versions.TryAdd(name, version);
            }
        }
        catch (Exception ex)
        {
            parseErrors?.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void CollectPackageReferenceElements(
        string path, List<XElement> into, ICollection<string>? parseErrors)
    {
        if (!File.Exists(path)) return;
        try
        {
            into.AddRange(XDocument.Parse(File.ReadAllText(path)).Descendants("PackageReference"));
        }
        catch (Exception ex)
        {
            parseErrors?.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
