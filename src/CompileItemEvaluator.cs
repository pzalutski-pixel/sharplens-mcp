using Microsoft.Build.Evaluation;

namespace SharpLensMcp;

// Which files a project compiles, from MSBuild evaluation of its project file: the Compile
// item set the build uses - the SDK default globs with their bin/obj excludes, explicit
// Include/Exclude, and Remove. sync_documents adds a file only when it is in that set, and
// an apply adds a Compile item to the project file only when no item already includes the
// new document. Evaluation runs in-process through the MSBuild the locator registered at
// startup. One evaluator serves one operation: it caches each (project file, target
// framework) evaluation for its lifetime, so it sees the project files and globs as they
// were when it first evaluated them. Dispose unloads the evaluations.
internal sealed class CompileItemEvaluator : IDisposable
{
    private const string CompileItemType = "Compile";

    private readonly ProjectCollection _collection = new();
    private readonly Dictionary<(string projectFile, string? targetFramework), Project> _evaluations = new();

    // The full paths of the existing files the project compiles for the given target
    // framework (null for a single-targeted project).
    public IReadOnlyList<string> CompileItemPaths(string projectFile, string? targetFramework)
    {
        var project = Evaluate(projectFile, targetFramework);
        return project.GetItems(CompileItemType)
            .Select(item => Path.GetFullPath(Path.Combine(project.DirectoryPath, item.EvaluatedInclude)))
            .Where(File.Exists)
            .ToList();
    }

    // Whether the project's Compile items include the path, whether or not the file exists
    // yet: the item elements that match it are replayed in evaluation order - an Include
    // adds it, a Remove takes it out, and an Include whose Exclude matches it does not add
    // it.
    public bool Includes(string projectFile, string? targetFramework, string filePath)
    {
        var project = Evaluate(projectFile, targetFramework);
        var relative = Path.GetRelativePath(project.DirectoryPath, filePath);
        var included = false;
        foreach (var result in project.GetItemProvenance(relative, CompileItemType))
        {
            if (result.Operation == Operation.Include) included = true;
            else if (result.Operation == Operation.Remove) included = false;
        }
        return included;
    }

    private Project Evaluate(string projectFile, string? targetFramework)
    {
        var key = (Path.GetFullPath(projectFile), targetFramework);
        if (_evaluations.TryGetValue(key, out var cached)) return cached;

        var globalProperties = new Dictionary<string, string>();
        if (targetFramework != null) globalProperties["TargetFramework"] = targetFramework;
        var project = new Project(key.Item1, globalProperties, toolsVersion: null, _collection);
        _evaluations[key] = project;
        return project;
    }

    public void Dispose()
    {
        _collection.UnloadAllProjects();
        _collection.Dispose();
    }
}
