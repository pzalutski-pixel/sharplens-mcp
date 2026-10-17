using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace SharpLensMcp;

// Reads what MSBuild evaluates from a project file, in-process through the MSBuild the
// locator registered at startup (the CompileItemEvaluator pattern): properties, package
// references and Compile items exactly as a build sees them — imports (the nearest
// Directory.Build.props, Directory.Packages.props), Conditions, $(Property) expansion,
// Update= items and XML-namespaced project files included. A multi-targeted project is
// evaluated per target framework by passing it as the TargetFramework global property.
// One evaluator serves one operation and caches each (project file, target framework)
// evaluation for its lifetime; Dispose unloads them. Evaluation failures throw
// InvalidProjectFileException for the caller to report.
internal sealed class MSBuildProjectEvaluator : IDisposable
{
    private readonly ProjectCollection _collection = new();
    private readonly ProjectCollection _revisionCollection = new();
    private readonly Dictionary<(string ProjectFile, string? TargetFramework), Project> _evaluations = new();

    // The evaluated value of a property; null when it is empty or undefined.
    public string? Property(string projectFile, string? targetFramework, string name)
    {
        var value = Evaluate(projectFile, targetFramework).GetPropertyValue(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    // The package references the project declares, in evaluation order: PackageReference
    // items (with Update= applied) and Central Package Management GlobalPackageReference
    // items, without the ones the SDK defines implicitly. Version is the item's
    // VersionOverride, else its Version, else the matching PackageVersion item's.
    public IReadOnlyList<EvaluatedPackageReference> PackageReferences(string projectFile, string? targetFramework)
    {
        var project = Evaluate(projectFile, targetFramework);
        var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in project.GetItems("PackageVersion"))
        {
            var value = version.GetMetadataValue("Version");
            if (value.Length > 0) centralVersions[version.EvaluatedInclude] = value;
        }

        var references = new List<EvaluatedPackageReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in project.GetItems("PackageReference").Concat(project.GetItems("GlobalPackageReference")))
        {
            if (string.Equals(item.GetMetadataValue("IsImplicitlyDefined"), "true", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(item.EvaluatedInclude)) continue;
            references.Add(new EvaluatedPackageReference(
                item.EvaluatedInclude,
                NullIfEmpty(item.GetMetadataValue("VersionOverride"))
                    ?? NullIfEmpty(item.GetMetadataValue("Version"))
                    ?? centralVersions.GetValueOrDefault(item.EvaluatedInclude),
                NullIfEmpty(item.GetMetadataValue("PrivateAssets")),
                NullIfEmpty(item.GetMetadataValue("ExcludeAssets")),
                NullIfEmpty(item.GetMetadataValue("IncludeAssets"))));
        }
        return references;
    }

    // The project file as it read at another revision (its XML text), evaluated at the
    // current location — so its relative paths and globs resolve as they did there.
    // Imports resolve against the files on disk now. Revisions are evaluated in a
    // collection of their own, so the in-memory text never stands in for the file on disk
    // in Evaluate.
    public Project EvaluateText(string projectFile, string xml, string? targetFramework)
    {
        using var reader = XmlReader.Create(new StringReader(xml));
        var root = ProjectRootElement.Create(reader, _revisionCollection, preserveFormatting: false);
        root.FullPath = Path.GetFullPath(projectFile);
        var globalProperties = new Dictionary<string, string>();
        if (targetFramework != null) globalProperties["TargetFramework"] = targetFramework;
        return new Project(root, globalProperties, toolsVersion: null, _revisionCollection);
    }

    // Whether the project's Compile items include the path, whether or not the file exists:
    // the item operations matching it replay in evaluation order — an Include adds it (its
    // Exclude already applied), a Remove takes it out.
    public static bool CompileIncludes(Project project, string filePath)
    {
        var relative = Path.GetRelativePath(project.DirectoryPath, filePath);
        var included = false;
        foreach (var result in project.GetItemProvenance(relative, "Compile"))
        {
            if (result.Operation == Operation.Include) included = true;
            else if (result.Operation == Operation.Remove) included = false;
        }
        return included;
    }

    public Project Evaluate(string projectFile, string? targetFramework)
    {
        var key = (Path.GetFullPath(projectFile), targetFramework);
        if (_evaluations.TryGetValue(key, out var cached)) return cached;

        var globalProperties = new Dictionary<string, string>();
        if (targetFramework != null) globalProperties["TargetFramework"] = targetFramework;
        var project = new Project(key.Item1, globalProperties, toolsVersion: null, _collection);
        _evaluations[key] = project;
        return project;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    public void Dispose()
    {
        _collection.UnloadAllProjects();
        _collection.Dispose();
        _revisionCollection.UnloadAllProjects();
        _revisionCollection.Dispose();
    }
}
