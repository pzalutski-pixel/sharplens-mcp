using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// sync_documents: brings disk changes into the loaded solution. The synced solution is
// forked from the workspace's CurrentSolution and made current through the workspace
// without writing anything (the files already hold this content), so the workspace and
// _solution stay one baseline and the next apply diffs against exactly what is on disk.
//
// Each file is decoded with its own encoding (SourceFileIO, the loader load_solution
// uses) and compared with the loaded text, so an untouched file is reported unchanged and
// the solution snapshot is left as it was. A file not yet in the solution is added only
// when it is a .cs file a project compiles - MSBuild evaluation of the project's Compile
// items (CompileItemEvaluator) decides, which honors the SDK globs, their bin/obj
// excludes and <Compile Remove> - and it is added to every project that compiles it.
// With no filePaths, every document is checked and every compile item not yet in the
// solution is added.
public partial class RoslynService
{
    private const string NotOnDiskOrInSolution = "File not found on disk and not in the solution";
    private const string NotACSharpFile = "Not a C# source file (.cs)";
    private const string NotACompileItem = "No project in the solution includes this file in its compile items";

    public async Task<object> SyncDocumentsAsync(List<string>? filePaths, CancellationToken cancellationToken = default)
    {
        if (_solution == null || _workspace == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SolutionNotLoaded,
                "No solution loaded. Call load_solution first.",
                hint: "Use load_solution before syncing documents"
            );
        }

        if (filePaths != null && filePaths.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "filePaths is empty: it names no file to sync",
                hint: "Omit filePaths to sync every document, or list the files to sync.");
        }
        if (filePaths != null && filePaths.Any(string.IsNullOrWhiteSpace))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "filePaths contains a blank entry",
                hint: "Every entry must be a file path, absolute or relative to the solution directory.",
                context: new { filePaths });
        }

        var baseline = _solution;
        var solutionDir = Path.GetDirectoryName(baseline.FilePath) ?? "";
        var updated = new List<string>();
        var added = new List<string>();
        var removed = new List<string>();
        var errors = new List<object>();
        var unchanged = 0;

        // A file path is not unique: a multi-targeted project has one Project per TFM over
        // the same .cs files, and linked/shared files appear in several projects. Group by
        // path and sync every document sharing it.
        var documentsByPath = baseline.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null)
            .GroupBy(d => Path.GetFullPath(d.FilePath!), PathComparer)
            .ToDictionary(g => g.Key, g => g.ToList(), PathComparer);

        using var compileItems = new CompileItemEvaluator();
        var working = baseline;

        List<string> paths;
        if (filePaths != null)
        {
            paths = filePaths
                .Select(WithNativeSeparators)
                .Select(p => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(solutionDir, p)))
                .Distinct(PathComparer)
                .ToList();
        }
        else
        {
            paths = documentsByPath.Keys.ToList();
            paths.AddRange(CompileItemsNotInSolution(baseline, documentsByPath, compileItems, errors));
        }

        foreach (var fullPath in paths)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileExists = File.Exists(fullPath);
                var inSolution = documentsByPath.TryGetValue(fullPath, out var existingDocuments);

                if (fileExists && inSolution)
                {
                    var diskText = await SourceFileIO.ReadAsync(fullPath, cancellationToken);
                    var changed = false;
                    foreach (var document in existingDocuments!)
                    {
                        if ((await document.GetTextAsync(cancellationToken)).ContentEquals(diskText)) continue;
                        working = working.WithDocumentText(document.Id, diskText, PreservationMode.PreserveIdentity);
                        changed = true;
                    }
                    if (changed) updated.Add(FormatPath(fullPath));
                    else unchanged++;
                }
                else if (fileExists)
                {
                    if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new { path = FormatPath(fullPath), error = NotACSharpFile });
                        continue;
                    }
                    var owners = ProjectsCompiling(baseline, fullPath, compileItems);
                    if (owners.Count == 0)
                    {
                        errors.Add(new { path = FormatPath(fullPath), error = NotACompileItem });
                        continue;
                    }
                    var diskText = await SourceFileIO.ReadAsync(fullPath, cancellationToken);
                    foreach (var project in owners)
                    {
                        working = working.AddDocument(DocumentInfo.Create(
                            DocumentId.CreateNewId(project.Id),
                            Path.GetFileName(fullPath),
                            FoldersFor(project, fullPath),
                            SourceCodeKind.Regular,
                            TextLoader.From(TextAndVersion.Create(diskText, VersionStamp.Create(), fullPath)),
                            fullPath));
                    }
                    added.Add(FormatPath(fullPath));
                }
                else if (inSolution)
                {
                    // Remove the deleted document from every project that included it.
                    foreach (var document in existingDocuments!)
                        working = working.RemoveDocument(document.Id);
                    removed.Add(FormatPath(fullPath));
                }
                else
                {
                    errors.Add(new { path = FormatPath(fullPath), error = NotOnDiskOrInSolution });
                }
            }
            catch (OperationCanceledException)
            {
                // A per-call timeout mid-sync must surface as cancellation (→ TIMEOUT),
                // not be folded into a per-file error and returned as partial success.
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(new { path = FormatPath(fullPath), error = ex.Message });
            }
        }

        if (working != baseline)
        {
            CommitDiskState(working);
            _documentCache.Clear();
            _compilationCache.Clear();
        }

        return CreateSuccessResponse(
            data: new
            {
                updated = updated.Count,
                added = added.Count,
                removed = removed.Count,
                unchanged,
                totalSynced = updated.Count + added.Count + removed.Count,
                updatedFiles = updated,
                addedFiles = added,
                removedFiles = removed,
                errors = errors.Count > 0 ? errors : null
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics to check for errors after sync",
                "search_symbols to find updated code"
            }
        );
    }

    // Makes the synced solution the workspace's current solution without writing to disk,
    // and _solution that same snapshot. A workspace that is not the disk workspace (the
    // in-memory test workspace) takes it through TryApplyChanges, which writes nothing.
    private void CommitDiskState(Solution synced)
    {
        if (_workspace is SourceFileWorkspace sourceFileWorkspace)
        {
            sourceFileWorkspace.AcceptDiskState(synced);
        }
        else if (!_workspace!.TryApplyChanges(synced))
        {
            throw new InvalidOperationException("The workspace rejected the synced solution");
        }
        _solution = _workspace.CurrentSolution;
    }

    // Every existing .cs file some project compiles that no document of the solution
    // holds yet. A project file MSBuild cannot evaluate is reported in errors and skipped.
    private List<string> CompileItemsNotInSolution(
        Solution solution, Dictionary<string, List<Document>> documentsByPath, CompileItemEvaluator compileItems,
        List<object> errors)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        var failedProjectFiles = new HashSet<string>(PathComparer);
        foreach (var project in solution.Projects)
        {
            if (!IsEvaluableCSharpProject(project)) continue;
            IReadOnlyList<string> compileItemPaths;
            try
            {
                compileItemPaths = compileItems.CompileItemPaths(project.FilePath!, ProjectTargetFramework.Of(project));
            }
            catch (Microsoft.Build.Exceptions.InvalidProjectFileException ex)
            {
                if (failedProjectFiles.Add(project.FilePath!))
                    errors.Add(new { path = FormatPath(project.FilePath), error = ex.Message });
                continue;
            }
            foreach (var path in compileItemPaths)
            {
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !documentsByPath.ContainsKey(path)
                    && seen.Add(path))
                {
                    found.Add(path);
                }
            }
        }
        return found;
    }

    // The C# projects whose Compile items include the file - one per target framework of
    // a multi-targeted project, and every project of a file linked into several. A project
    // file MSBuild cannot evaluate includes nothing.
    private static List<Project> ProjectsCompiling(Solution solution, string path, CompileItemEvaluator compileItems)
    {
        var owners = new List<Project>();
        foreach (var project in solution.Projects)
        {
            if (!IsEvaluableCSharpProject(project)) continue;
            try
            {
                if (compileItems.Includes(project.FilePath!, ProjectTargetFramework.Of(project), path)) owners.Add(project);
            }
            catch (Microsoft.Build.Exceptions.InvalidProjectFileException)
            {
                // This project file does not evaluate, so it is not an owner of the file.
            }
        }
        return owners;
    }

    private static bool IsEvaluableCSharpProject(Project project) =>
        project.FilePath != null && project.Language == LanguageNames.CSharp && File.Exists(project.FilePath);

    // The document folders of a file inside the project directory: its directory relative
    // to the project directory, one entry per segment. A file outside the project
    // directory (a linked file) has none.
    private static IReadOnlyList<string> FoldersFor(Project project, string path)
    {
        var projectDir = Path.GetDirectoryName(project.FilePath);
        if (projectDir == null || !IsUnderDirectory(path, projectDir)) return Array.Empty<string>();
        var relativeDir = Path.GetDirectoryName(Path.GetRelativePath(projectDir, path));
        return string.IsNullOrEmpty(relativeDir)
            ? Array.Empty<string>()
            : relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // Whether the path lies inside the directory - compared at a path-separator boundary,
    // so "C:\src\AppExtra\x.cs" is not inside "C:\src\App".
    private static bool IsUnderDirectory(string path, string directory)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, PathComparison);
    }
}
