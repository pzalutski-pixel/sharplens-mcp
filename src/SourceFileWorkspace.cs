using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// The server's host workspace. MSBuildWorkspace loads the solution and this workspace then
// owns it, so CurrentSolution is the single baseline both directions go through:
// sync_documents makes a solution updated from disk current without writing anything
// (AcceptDiskState), and an apply goes through TryApplyChanges, which diffs the edited
// solution against that baseline and calls the overrides below once per real change.
//
// The overrides write exactly the changed documents, in the text's encoding (the apply
// path stamps each document with the encoding its file has on disk). A new document is
// written to disk and gets a Compile item only when the project's Compile items do not
// already include it (an SDK project's default globs do); a removed document's file is
// deleted along with an explicit Compile item naming it. Text, document additions and
// removals are the only changes this workspace writes; any other kind (project or
// metadata references, document renames) is refused by TryApplyChanges.
internal sealed class SourceFileWorkspace : Workspace
{
    private readonly IDisposable? _loader;

    // The paths written during the current TryApplyChanges: documents of a multi-targeted
    // project (one per target framework) or a linked file share one path and must write
    // it once.
    private readonly HashSet<string> _writtenThisApply = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // loader: the workspace that loaded `solution` (the MSBuildWorkspace); disposed with this one.
    public SourceFileWorkspace(HostServices hostServices, string workspaceKind, Solution solution, IDisposable? loader)
        : base(hostServices, workspaceKind)
    {
        _loader = loader;
        SetCurrentSolution(solution);
    }

    // Makes a solution forked from CurrentSolution and updated from disk the current
    // solution. Nothing is written: the files already hold this content.
    public void AcceptDiskState(Solution synced) => SetCurrentSolution(synced);

    public override bool CanApplyChange(ApplyChangesKind feature) =>
        feature is ApplyChangesKind.ChangeDocument
            or ApplyChangesKind.AddDocument
            or ApplyChangesKind.RemoveDocument;

    public override bool TryApplyChanges(Solution newSolution)
    {
        _writtenThisApply.Clear();
        try
        {
            return base.TryApplyChanges(newSolution);
        }
        finally
        {
            _writtenThisApply.Clear();
        }
    }

    protected override void ApplyDocumentTextChanged(DocumentId documentId, SourceText text)
    {
        var document = CurrentSolution.GetDocument(documentId);
        if (document?.FilePath is { } path && _writtenThisApply.Add(path))
        {
            SourceFileIO.Write(path, text.ToString(), text.Encoding ?? SourceFileIO.Utf8NoBom);
        }
        OnDocumentTextChanged(documentId, text, PreservationMode.PreserveValue);
    }

    protected override void ApplyDocumentAdded(DocumentInfo info, SourceText text)
    {
        var project = CurrentSolution.GetProject(info.Id.ProjectId)
            ?? throw new InvalidOperationException($"The project of added document '{info.Name}' is not in the solution");
        var path = info.FilePath ?? DefaultDocumentPath(project, info);
        if (_writtenThisApply.Add(path))
        {
            SourceFileIO.Write(path, text.ToString(), text.Encoding ?? SourceFileIO.Utf8NoBom);
        }
        EnsureCompileItem(project, path);
        OnDocumentAdded(info
            .WithFilePath(path)
            .WithTextLoader(TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), path))));
    }

    protected override void ApplyDocumentRemoved(DocumentId documentId)
    {
        var document = CurrentSolution.GetDocument(documentId);
        if (document?.FilePath is { } path)
        {
            if (_writtenThisApply.Add(path) && File.Exists(path)) File.Delete(path);
            if (document.Project.FilePath is { } projectFile)
            {
                ProjectFileEditor.RemoveCompileItem(projectFile, RelativeInclude(projectFile, path));
            }
        }
        OnDocumentRemoved(documentId);
    }

    // Adds a Compile item for the file unless the project's Compile items already include
    // it. Evaluated fresh, so an item added for another target framework of the same
    // project file in this apply is seen.
    private static void EnsureCompileItem(Project project, string path)
    {
        if (project.FilePath is not { } projectFile) return;
        using var evaluator = new CompileItemEvaluator();
        if (evaluator.Includes(projectFile, ProjectTargetFramework.Of(project), path)) return;
        ProjectFileEditor.AddCompileItem(projectFile, RelativeInclude(projectFile, path));
    }

    private static string RelativeInclude(string projectFile, string path) =>
        Path.GetRelativePath(Path.GetDirectoryName(projectFile)!, path);

    // Where MSBuildWorkspace places a new document that has no path: the project directory
    // plus the document's folders.
    private static string DefaultDocumentPath(Project project, DocumentInfo info)
    {
        var projectDirectory = Path.GetDirectoryName(project.FilePath)
            ?? throw new InvalidOperationException($"Project '{project.Name}' has no directory to add '{info.Name}' to");
        return Path.Combine(new[] { projectDirectory }.Concat(info.Folders).Append(info.Name).ToArray());
    }

    protected override void Dispose(bool finalize)
    {
        base.Dispose(finalize);
        if (!finalize) _loader?.Dispose();
    }
}
