using Microsoft.Build.Exceptions;
using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// find_unused_dependencies: which declared PackageReference / ProjectReference entries the
// compiler never actually needs — per Roslyn's own GetUsedAssemblyReferences verdict.
// Declared package references are what MSBuild evaluates (CollectEffectivePackageReferences);
// a compile reference is attributed to a package through the restore assets
// (RestoredPackageMap), wherever the packages were restored to. A package that contributes
// no compile-time assembly (an analyzer, source generator or build-only package, or one whose
// compile assets are excluded) cannot be judged this way and is REPORTED as skipped, never
// silently dropped. Compile-time verdict only: a package loaded purely via reflection
// (plugins, MEF) appears unused — the schema says verify first. A multi-targeted project
// is one entry: a reference is used when any target framework uses it.
public partial class RoslynService
{
    public async Task<object> FindUnusedDependenciesAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (scope, scopeError) = ResolveProjectScope(projectName);
        if (scope == null) return scopeError!;

        var model = GetLogicalProjects();
        var logicalProjects = scope
            .Where(p => p.FilePath != null && File.Exists(p.FilePath))
            .GroupBy(p => model.Of(p))
            .ToList();
        if (logicalProjects.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotFound,
                projectName == null
                    ? "The solution has no project files on disk to audit"
                    : $"Project '{projectName}' has no project file on disk to audit",
                hint: $"Available projects: {string.Join(", ", model.ValidNames)}",
                context: new { projectName });
        }

        using var evaluator = new MSBuildProjectEvaluator();
        var results = new List<object>();
        var totalUnused = 0;

        foreach (var members in logicalProjects)
        {
            var logical = members.Key;
            var parseErrors = new List<string>();

            // Per package id: its declaration (first framework that declares it), whether
            // any framework's compilation references it, and whether any uses it.
            var declared = new List<EvaluatedPackageReference>();
            var contributing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasAssets = false;
            // Per referenced logical project: whether any framework's compilation used it.
            var projectReferences = new Dictionary<string, bool>(StringComparer.Ordinal);

            foreach (var project in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetFramework = LogicalProject.TargetFrameworkOf(project);
                foreach (var reference in CollectEffectivePackageReferences(project.FilePath!, parseErrors, targetFramework, evaluator))
                {
                    if (!declared.Any(d => d.Name.Equals(reference.Name, StringComparison.OrdinalIgnoreCase))) declared.Add(reference);
                }

                var compilation = await GetProjectCompilationAsync(project, cancellationToken);
                if (compilation == null) continue;

                var packages = PackageMapFor(evaluator, project.FilePath!, targetFramework, parseErrors);
                hasAssets |= packages != null;
                foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
                {
                    if (packages?.PackageIdOf(reference.FilePath) is { } id) contributing.Add(id);
                }

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
                            if (packages?.PackageIdOf(pe.FilePath) is { } usedId) used.Add(usedId);
                            break;
                        case CompilationReference cr:
                            usedProjectAssemblies.Add(cr.Compilation.Assembly);
                            break;
                    }
                }

                foreach (var projectReference in project.ProjectReferences)
                {
                    var referenced = _solution!.GetProject(projectReference.ProjectId);
                    if (referenced == null) continue;
                    var referencedName = model.Of(referenced).Name;
                    // The referenced project's own assembly symbol; "used" iff it is among the
                    // symbols the compiler actually pulled in — by identity, not name.
                    var referencedAssembly = (await referenced.GetCompilationAsync(cancellationToken))?.Assembly;
                    var isUsed = referencedAssembly != null && usedProjectAssemblies.Contains(referencedAssembly);
                    projectReferences[referencedName] = projectReferences.GetValueOrDefault(referencedName) || isUsed;
                }
            }

            var unusedPackages = new List<object>();
            var skipped = new List<object>();
            foreach (var reference in declared)
            {
                if (!contributing.Contains(reference.Name))
                {
                    skipped.Add(new
                    {
                        name = reference.Name,
                        reason = reference.ExcludesCompileAssets
                            ? "ExcludeAssets excludes its compile assets"
                            : hasAssets
                                ? "Contributes no compile-time assemblies (analyzer, source generator, or build asset)"
                                : "The project has no restore assets (obj/project.assets.json) to attribute references to packages - restore it first"
                    });
                }
                else if (!used.Contains(reference.Name))
                {
                    unusedPackages.Add(new { name = reference.Name, version = reference.Version ?? "unknown" });
                }
            }
            var unusedProjectReferences = projectReferences
                .Where(r => !r.Value)
                .Select(r => new { name = r.Key })
                .ToList();

            totalUnused += unusedPackages.Count + unusedProjectReferences.Count;
            results.Add(new
            {
                projectName = logical.Name,
                targetFrameworks = logical.IsMultiTargeted ? logical.TargetFrameworks : null,
                packagesChecked = declared.Count,
                projectReferencesChecked = projectReferences.Count,
                unusedPackages,
                unusedProjectReferences,
                skipped,
                parseError = parseErrors.Count > 0 ? string.Join("; ", parseErrors.Distinct(StringComparer.Ordinal)) : null
            });
        }

        return CreateSuccessResponse(
            data: new { projects = results },
            ResultCounts.Complete(totalUnused),
            suggestedNextTools: totalUnused > 0
                ? new[]
                {
                    "find_reflection_usage before removing anything - reflection-loaded packages appear unused here",
                    "Remove the confirmed-unused references from the csproj and rebuild"
                }
                : new[] { "Every declared dependency is compile-used (or accounted for in skipped)" });
    }

    // The restore-assets package map of a project file (its evaluated ProjectAssetsFile);
    // null when the project has no assets file or cannot be evaluated.
    private static RestoredPackageMap? PackageMapFor(
        MSBuildProjectEvaluator evaluator, string projectFile, string? targetFramework, ICollection<string> parseErrors)
    {
        try
        {
            var (map, warning) = RestoredPackageMap.Load(evaluator.Property(projectFile, targetFramework, "ProjectAssetsFile"));
            if (warning != null) parseErrors.Add(warning);
            return map;
        }
        catch (InvalidProjectFileException)
        {
            // Already reported by CollectEffectivePackageReferences for the same file.
            return null;
        }
    }

    // The package references in effect for a project file, as MSBuild evaluates them
    // (MSBuildProjectEvaluator.PackageReferences): the nearest Directory.Build.props and
    // other imports, Conditions, $(Property) versions, Update= items, VersionOverride and
    // Central Package Management versions, for the given target framework of a
    // multi-targeted project (null: the project as evaluated without one). A project file
    // that cannot be evaluated contributes nothing and its error — naming the file at
    // fault — goes to parseErrors when supplied.
    internal static List<EvaluatedPackageReference> CollectEffectivePackageReferences(
        string projectFilePath, ICollection<string>? parseErrors = null, string? targetFramework = null,
        MSBuildProjectEvaluator? evaluator = null)
    {
        var owned = evaluator == null ? new MSBuildProjectEvaluator() : null;
        try
        {
            return (evaluator ?? owned!).PackageReferences(projectFilePath, targetFramework).ToList();
        }
        catch (InvalidProjectFileException ex)
        {
            parseErrors?.Add($"{Path.GetFileName(ex.ProjectFile ?? projectFilePath)}: {ex.Message}");
            return new List<EvaluatedPackageReference>();
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
