using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// The unified apply path. Every preview:false tool funnels its changed Solution through
// ApplyAndReportAsync. The baseline is _solution, which is always the workspace's
// CurrentSolution (sync_documents and every apply keep the two identical), so the
// workspace's TryApplyChanges diffs the edit against exactly the text it was computed
// from and writes only the documents the edit changed.
//
// Before anything is written the edit is prepared (PrepareForApplyAsync): nodes the tool
// annotated with Formatter.Annotation are formatted with the document's own options, the
// line breaks the edit inserts take the file's line-break style, each document keeps its
// encoding, and every document sharing a file path with a changed one (the per-framework
// documents of a multi-targeted project, a linked file) carries the same text. On the disk
// workspace each target file is then compared with the text the edit was computed from;
// if any changed on disk the whole apply is refused with STALE_DOCUMENT. On success the
// workspace, _solution, both caches and disk agree; on failure nothing changed.
public partial class RoslynService
{
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal sealed record AppliedChange(
        string? FilePath,
        string FileName,
        string ChangeType,
        int ChangeCount);

    // One file an edit changes, against the baseline. Documents that share a file path
    // collapse to one entry. Changes are the minimal edits that turn OldText into NewText
    // (Roslyn's syntax-tree diff), so a two-identifier edit reports two spans, not one
    // whole-file replacement. Added files have no OldText; removed files have no NewText.
    internal sealed record FileChange(
        string? FilePath,
        string FileName,
        string ChangeType,
        SourceText? OldText,
        SourceText? NewText,
        IReadOnlyList<TextChange> Changes);

    internal async Task<List<FileChange>> ComputeFileChangesAsync(
        Solution proposed, CancellationToken cancellationToken = default)
    {
        var baseline = _solution!;
        var fileChanges = new List<FileChange>();
        var seenPaths = new HashSet<string>(PathComparer);
        bool FirstForPath(string? path) => path == null || seenPaths.Add(path);

        foreach (var projectChanges in proposed.GetChanges(baseline).GetProjectChanges())
        {
            foreach (var addedId in projectChanges.GetAddedDocuments())
            {
                var document = proposed.GetDocument(addedId);
                if (document == null || !FirstForPath(document.FilePath)) continue;
                var text = await document.GetTextAsync(cancellationToken);
                fileChanges.Add(new FileChange(document.FilePath, document.Name, "Added", null, text, Array.Empty<TextChange>()));
            }

            foreach (var changedId in projectChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var oldDocument = baseline.GetDocument(changedId);
                var newDocument = proposed.GetDocument(changedId);
                if (oldDocument == null || newDocument == null) continue;
                var changes = (await newDocument.GetTextChangesAsync(oldDocument, cancellationToken)).ToList();
                if (changes.Count == 0 || !FirstForPath(newDocument.FilePath)) continue;
                fileChanges.Add(new FileChange(
                    newDocument.FilePath, newDocument.Name, "Modified",
                    await oldDocument.GetTextAsync(cancellationToken),
                    await newDocument.GetTextAsync(cancellationToken),
                    changes));
            }

            foreach (var removedId in projectChanges.GetRemovedDocuments())
            {
                var document = baseline.GetDocument(removedId);
                if (document == null || !FirstForPath(document.FilePath)) continue;
                var text = await document.GetTextAsync(cancellationToken);
                fileChanges.Add(new FileChange(document.FilePath, document.Name, "Removed", text, null, Array.Empty<TextChange>()));
            }
        }

        return fileChanges;
    }

    // The changed-file report: a modified file counts its edited spans, an added or
    // removed file counts one change.
    private List<AppliedChange> BuildChangeReport(IEnumerable<FileChange> fileChanges) =>
        fileChanges
            .Select(f => new AppliedChange(
                FormatPath(f.FilePath), f.FileName, f.ChangeType,
                f.ChangeType == "Modified" ? f.Changes.Count : 1))
            .ToList();

    // Rich per-file preview details (full new text + per-span changes) for a prospective
    // solution, prepared exactly as an apply would write it. Preview-only - nothing is
    // applied or written. Used by apply_code_fix and apply_code_action_by_title previews.
    internal async Task<List<object>> BuildPreviewChangeDetailsAsync(
        Solution newSolution, CancellationToken cancellationToken = default)
    {
        var (prepared, _) = await PrepareForApplyAsync(newSolution, cancellationToken);
        var fileChanges = await ComputeFileChangesAsync(prepared ?? newSolution, cancellationToken);

        // Every entry carries the SAME key set regardless of changeType so a consumer
        // can read one shape: filePath, fileName, isNewFile, changeType, changeCount,
        // changes[], newText. Added/Removed have no per-span diff (changeCount 0,
        // changes empty); a removed file has no newText (null).
        return fileChanges.Select(f =>
        {
            var changes = f.ChangeType == "Modified"
                ? f.Changes.Select(c => (object)new
                {
                    span = new { start = c.Span.Start, end = c.Span.End, length = c.Span.Length },
                    oldText = f.OldText!.ToString(c.Span),
                    newText = c.NewText
                }).ToArray()
                : Array.Empty<object>();
            var filePath = FormatPath(f.FilePath);
            return (object)new
            {
                filePath = f.ChangeType == "Added" && filePath.Length == 0 ? $"NewFile_{f.FileName}" : filePath,
                fileName = f.FileName,
                isNewFile = f.ChangeType == "Added",
                changeType = f.ChangeType,
                changeCount = changes.Length,
                changes,
                newText = f.NewText?.ToString()
            };
        }).ToList();
    }

    // The apply report rendered as the canonical changed-file shape - the SAME key set
    // BuildPreviewChangeDetailsAsync emits (filePath, fileName, isNewFile, changeType,
    // changeCount, changes, newText) - so every apply tool surfaces `changedFiles`
    // identically. newText/changes are null on apply: the text went to disk, not the
    // payload.
    internal static List<object> FormatAppliedChanges(IReadOnlyList<AppliedChange> report) =>
        report.Select(c => (object)new
        {
            filePath = c.FilePath,
            fileName = c.FileName,
            isNewFile = c.ChangeType == "Added",
            changeType = c.ChangeType,
            changeCount = c.ChangeCount,
            changes = (object?)null,
            newText = (string?)null
        }).ToList();

    // Applies a changed Solution atomically. On success: the workspace owns the prepared
    // solution, _solution is the workspace's CurrentSolution, caches are cleared, and (on
    // the disk workspace) exactly the changed files are written. On failure no state is
    // touched and a structured error is returned.
    internal async Task<(bool ok, object? error, List<AppliedChange> changes, IReadOnlyList<string> introducedErrors)> ApplyAndReportAsync(
        Solution newSolution, CancellationToken cancellationToken = default)
    {
        var (prepared, prepareError) = await PrepareForApplyAsync(newSolution, cancellationToken);
        if (prepared == null) return (false, prepareError, new List<AppliedChange>(), Array.Empty<string>());

        if (_workspace is SourceFileWorkspace)
        {
            var (stamped, staleFiles, lossyFiles) = await CheckDiskBaselineAsync(prepared, cancellationToken);
            if (staleFiles.Count > 0)
            {
                return (false, CreateErrorResponse(
                    ErrorCodes.StaleDocument,
                    $"The edit's target files changed on disk since they were read: {string.Join(", ", staleFiles)}",
                    hint: "run sync_documents, then retry",
                    context: new { staleFiles }), new List<AppliedChange>(), Array.Empty<string>());
            }
            if (lossyFiles.Count > 0)
            {
                return (false, CreateErrorResponse(
                    ErrorCodes.UnsupportedEncoding,
                    $"The edit's target files do not decode losslessly in the encoding they were read with, so writing them would change text outside the edit: {string.Join(", ", lossyFiles)}",
                    hint: "Save the files as UTF-8 (with or without a byte-order mark), run sync_documents, then retry.",
                    context: new { files = lossyFiles }), new List<AppliedChange>(), Array.Empty<string>());
            }
            prepared = stamped;
        }

        var fileChanges = await ComputeFileChangesAsync(prepared, cancellationToken);
        var report = BuildChangeReport(fileChanges);

        // The compile errors this edit introduces (the prepared solution equals what is
        // applied). Surfaced in the result by every caller as data.postApplyErrors. The
        // contract: the edit STILL applies (success:true / applied:true - disclosed so the
        // caller can revert), but a non-empty postApplyErrors flips the MCP envelope's
        // isError to true (HandleToolCallAsync), so a tool that emits
        // syntactically-valid-but-broken code is never read as a clean success. A property
        // of the shared apply path (R4).
        var introducedErrors = await IntroducedCompileErrorsAsync(prepared, fileChanges, cancellationToken);

        bool applied;
        try
        {
            applied = _workspace!.TryApplyChanges(prepared);
        }
        catch (NotSupportedException ex)
        {
            return (false, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"The edit includes a change this server does not write: {ex.Message}",
                hint: "Only document text edits, added documents and removed documents are applied. Make the other change (project or package references, file renames) by hand.",
                context: new { changedFiles = report.Count }), report, introducedErrors);
        }

        if (!applied)
        {
            return (false, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "The workspace rejected the change",
                hint: "The solution may have changed since the operation started. Retry the operation; if it persists, reload the solution.",
                context: new { changedFiles = report.Count }), report, introducedErrors);
        }

        _solution = _workspace.CurrentSolution;
        _documentCache.Clear();
        _compilationCache.Clear();

        return (true, null, report, introducedErrors);
    }

    // The edit as it will be written. Returns the prepared solution, or null and an error
    // when documents that share one file path received conflicting edits.
    private async Task<(Solution? prepared, object? error)> PrepareForApplyAsync(
        Solution proposed, CancellationToken cancellationToken)
    {
        var baseline = _solution!;
        var prepared = proposed;
        var changedDocumentIds = new List<DocumentId>();
        var addedDocumentIds = new List<DocumentId>();

        foreach (var projectChanges in proposed.GetChanges(baseline).GetProjectChanges())
        {
            changedDocumentIds.AddRange(projectChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true));
            addedDocumentIds.AddRange(projectChanges.GetAddedDocuments());
        }

        // A changed document: formatting of annotated nodes, then the original's line-break
        // style and encoding.
        foreach (var documentId in changedDocumentIds)
        {
            var oldDocument = baseline.GetDocument(documentId);
            var newDocument = prepared.GetDocument(documentId);
            if (oldDocument == null || newDocument == null) continue;
            newDocument = await FormatAnnotatedNodesAsync(newDocument, cancellationToken);
            var oldText = await oldDocument.GetTextAsync(cancellationToken);
            var edited = await newDocument.GetTextAsync(cancellationToken);
            var edits = await newDocument.GetTextChangesAsync(oldDocument, cancellationToken);
            var final = WithEncoding(LineEndings.MatchOriginal(oldText, edited, edits), oldText.Encoding);
            prepared = prepared.WithDocumentText(documentId, final, PreservationMode.PreserveIdentity);
        }

        // An added document takes the line-break style and encoding of the project's file
        // the edit came from: a changed document of the same project, else the project's
        // first document.
        foreach (var documentId in addedDocumentIds)
        {
            var document = prepared.GetDocument(documentId);
            if (document == null) continue;
            document = await FormatAnnotatedNodesAsync(document, cancellationToken);
            var text = await document.GetTextAsync(cancellationToken);
            var model = await ModelTextAsync(baseline, document.Project.Id, changedDocumentIds, cancellationToken);
            var lineBreak = (model == null ? null : LineEndings.UniformStyle(model) ?? LineEndings.DominantStyle(model))
                ?? LineEndings.DominantStyle(text);
            var content = lineBreak == null ? text.ToString() : LineEndings.Convert(text.ToString(), lineBreak);
            prepared = prepared.WithDocumentText(documentId,
                SourceText.From(content, model?.Encoding ?? SourceFileIO.Utf8NoBom, text.ChecksumAlgorithm),
                PreservationMode.PreserveIdentity);
        }

        prepared = AddToSiblingFrameworkProjects(prepared, addedDocumentIds);

        return await UnifyLinkedDocumentsAsync(baseline, prepared, changedDocumentIds, cancellationToken);
    }

    // A compute-only tool's updated content for a document, with the line breaks the
    // update inserts in the document's own style - the normalization an apply performs,
    // for content the caller writes itself.
    internal static async Task<string> MatchDocumentLineEndingsAsync(
        Document document, string updatedContent, CancellationToken cancellationToken)
    {
        var original = await document.GetTextAsync(cancellationToken);
        var updated = SourceText.From(updatedContent, original.Encoding, original.ChecksumAlgorithm);
        var changes = await document.WithText(updated).GetTextChangesAsync(document, cancellationToken);
        return LineEndings.MatchOriginal(original, updated, changes).ToString();
    }

    // Formats the nodes a tool marked with Formatter.Annotation - the Roslyn convention for
    // generated syntax that needs formatting - using the document's own options. Text the
    // tool did not annotate is left exactly as it is.
    private static async Task<Document> FormatAnnotatedNodesAsync(Document document, CancellationToken cancellationToken)
    {
        if (!document.TryGetSyntaxRoot(out var root)) return document;
        if (!root.GetAnnotatedNodesAndTokens(Formatter.Annotation).Any()) return document;
        return await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancellationToken);
    }

    private static SourceText WithEncoding(SourceText text, System.Text.Encoding? encoding) =>
        encoding == null || Equals(text.Encoding, encoding)
            ? text
            : SourceText.From(text.ToString(), encoding, text.ChecksumAlgorithm);

    // The text a new document in the project should look like: a document of the same
    // project the edit changes, else the project's first document with a file path.
    private static async Task<SourceText?> ModelTextAsync(
        Solution baseline, ProjectId projectId, IReadOnlyList<DocumentId> changedDocumentIds, CancellationToken cancellationToken)
    {
        var project = baseline.GetProject(projectId);
        if (project == null) return null;
        var model = changedDocumentIds
            .Where(id => id.ProjectId == projectId)
            .Select(baseline.GetDocument)
            .FirstOrDefault(d => d != null)
            ?? project.Documents.FirstOrDefault(d => d.FilePath != null);
        return model == null ? null : await model.GetTextAsync(cancellationToken);
    }

    // A document added to one target framework of a multi-targeted project is added to
    // the project's other frameworks too when their Compile items include it - or when the
    // project needs an explicit Compile item for it, which applies to every framework.
    private static Solution AddToSiblingFrameworkProjects(Solution prepared, IReadOnlyList<DocumentId> addedDocumentIds)
    {
        CompileItemEvaluator? evaluator = null;
        try
        {
            foreach (var documentId in addedDocumentIds)
            {
                var document = prepared.GetDocument(documentId);
                var projectFile = document?.Project.FilePath;
                if (document?.FilePath == null || projectFile == null || !File.Exists(projectFile)) continue;

                var siblings = prepared.Projects
                    .Where(p => p.Id != document.Project.Id
                        && p.FilePath != null && PathComparer.Equals(p.FilePath, projectFile)
                        && !p.Documents.Any(d => PathComparer.Equals(d.FilePath, document.FilePath)))
                    .ToList();
                if (siblings.Count == 0) continue;

                evaluator ??= new CompileItemEvaluator();
                var needsExplicitItem = !evaluator.Includes(projectFile, ProjectTargetFramework.Of(document.Project), document.FilePath);
                if (!document.TryGetText(out var text)) continue;
                foreach (var sibling in siblings)
                {
                    if (!needsExplicitItem
                        && !evaluator.Includes(projectFile, ProjectTargetFramework.Of(sibling), document.FilePath)) continue;
                    prepared = prepared.AddDocument(DocumentInfo.Create(
                        DocumentId.CreateNewId(sibling.Id),
                        document.Name,
                        document.Folders,
                        document.SourceCodeKind,
                        TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), document.FilePath)),
                        document.FilePath));
                }
            }
        }
        finally
        {
            evaluator?.Dispose();
        }
        return prepared;
    }

    // Every document that shares a file path with a changed document must carry the same
    // text - the file is written once. When only one of them was edited (a tool that edits
    // the document of one target framework), its text goes to the others; when several
    // were edited differently, their changes are merged, and overlapping different edits
    // are a conflict.
    private async Task<(Solution? prepared, object? error)> UnifyLinkedDocumentsAsync(
        Solution baseline, Solution prepared, IReadOnlyList<DocumentId> changedDocumentIds, CancellationToken cancellationToken)
    {
        var handled = new HashSet<string>(PathComparer);
        foreach (var changedId in changedDocumentIds)
        {
            var path = prepared.GetDocument(changedId)?.FilePath;
            if (path == null || !handled.Add(path)) continue;

            var linked = prepared.GetDocumentIdsWithFilePath(path);
            if (linked.Length < 2) continue;

            var edited = new List<(DocumentId id, SourceText text, IReadOnlyList<TextChange> changes)>();
            foreach (var id in linked)
            {
                var oldDocument = baseline.GetDocument(id);
                var newDocument = prepared.GetDocument(id)!;
                var newText = await newDocument.GetTextAsync(cancellationToken);
                if (oldDocument == null || (await oldDocument.GetTextAsync(cancellationToken)).ContentEquals(newText)) continue;
                edited.Add((id, newText, (await newDocument.GetTextChangesAsync(oldDocument, cancellationToken)).ToList()));
            }
            if (edited.Count == 0) continue;

            SourceText unified;
            if (edited.Skip(1).All(e => e.text.ContentEquals(edited[0].text)))
            {
                unified = edited[0].text;
            }
            else
            {
                var merged = edited.SelectMany(e => e.changes).Distinct().OrderBy(c => c.Span.Start).ThenBy(c => c.Span.End).ToList();
                for (var i = 1; i < merged.Count; i++)
                {
                    if (merged[i].Span.Start < merged[i - 1].Span.End
                        || (merged[i].Span.Start == merged[i - 1].Span.Start && merged[i].Span.IsEmpty && merged[i - 1].Span.IsEmpty))
                    {
                        return (null, CreateErrorResponse(
                            ErrorCodes.AnalysisFailed,
                            $"The edit changes the documents that share '{FormatPath(path)}' (one per target framework or linked project) in conflicting ways",
                            hint: "Apply the edit to one target framework at a time, or make it by hand.",
                            context: new { filePath = FormatPath(path) }));
                    }
                }
                var baseText = await baseline.GetDocument(edited[0].id)!.GetTextAsync(cancellationToken);
                unified = WithEncoding(baseText.WithChanges(merged), edited[0].text.Encoding);
            }

            foreach (var id in linked)
            {
                var current = await prepared.GetDocument(id)!.GetTextAsync(cancellationToken);
                if (!ReferenceEquals(current, unified)) prepared = prepared.WithDocumentText(id, unified, PreservationMode.PreserveIdentity);
            }
        }
        return (prepared, null);
    }

    // On the disk workspace: compares every target file with the text the edit was
    // computed from and stamps each changed document with its file's encoding as it is on
    // disk. A modified or removed file whose disk content differs from that text is stale,
    // as is an added file whose path already exists on disk. A modified file whose bytes
    // are not what its decoded text re-encodes to (a BOM-less file that is not valid UTF-8
    // where the fallback code page is UTF-8, so its bytes decoded to U+FFFD) is lossy:
    // writing it would change text outside the edit. Returns the stamped solution, the
    // stale files and the lossy files (solution-relative paths).
    private async Task<(Solution stamped, List<string> staleFiles, List<string> lossyFiles)> CheckDiskBaselineAsync(
        Solution prepared, CancellationToken cancellationToken)
    {
        var baseline = _solution!;
        var stale = new List<string>();
        var lossy = new List<string>();
        var stamped = prepared;
        var checkedPaths = new HashSet<string>(PathComparer);

        foreach (var projectChanges in prepared.GetChanges(baseline).GetProjectChanges())
        {
            foreach (var changedId in projectChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var oldDocument = baseline.GetDocument(changedId);
                var path = oldDocument?.FilePath;
                if (path == null || !checkedPaths.Add(path)) continue;
                if (!File.Exists(path))
                {
                    stale.Add(FormatPath(path));
                    continue;
                }
                var disk = await SourceFileIO.ReadAsync(path, cancellationToken);
                if (!disk.ContentEquals(await oldDocument!.GetTextAsync(cancellationToken)))
                {
                    stale.Add(FormatPath(path));
                    continue;
                }
                if (!SourceFileIO.RoundTrips(path, disk))
                {
                    lossy.Add(FormatPath(path));
                    continue;
                }
                foreach (var id in stamped.GetDocumentIdsWithFilePath(path))
                {
                    var text = await stamped.GetDocument(id)!.GetTextAsync(cancellationToken);
                    var withDiskEncoding = WithEncoding(text, disk.Encoding);
                    if (!ReferenceEquals(withDiskEncoding, text))
                        stamped = stamped.WithDocumentText(id, withDiskEncoding, PreservationMode.PreserveIdentity);
                }
            }

            foreach (var removedId in projectChanges.GetRemovedDocuments())
            {
                var oldDocument = baseline.GetDocument(removedId);
                var path = oldDocument?.FilePath;
                if (path == null || !checkedPaths.Add(path) || !File.Exists(path)) continue;
                var disk = await SourceFileIO.ReadAsync(path, cancellationToken);
                if (!disk.ContentEquals(await oldDocument!.GetTextAsync(cancellationToken))) stale.Add(FormatPath(path));
            }

            foreach (var addedId in projectChanges.GetAddedDocuments())
            {
                var path = prepared.GetDocument(addedId)?.FilePath;
                if (path != null && checkedPaths.Add(path) && File.Exists(path)) stale.Add(FormatPath(path));
            }
        }

        return (stamped, stale, lossy);
    }

    // Compile errors the prepared solution introduces that were not already present. Every
    // project the edit changes is compiled - each target framework of a multi-targeted
    // project is its own project - and so is every project that depends on one of them,
    // since an edit can compile locally and still break a caller elsewhere. Errors are
    // matched by file + id + message as a multiset: a new error identical to a pre-existing
    // one is introduced when the count grows. Which instances are new is decided by
    // position: each pre-existing error's position is carried through the edit's text
    // changes, and new errors not at a carried position are the introduced ones. Each entry
    // names the solution-relative path and the zero-based line. Identical entries from
    // several projects (one file compiled per framework) are listed once.
    private async Task<IReadOnlyList<string>> IntroducedCompileErrorsAsync(
        Solution prepared, IReadOnlyList<FileChange> fileChanges, CancellationToken cancellationToken)
    {
        var baseline = _solution!;
        static (string path, string id, string message) Key(Diagnostic d) =>
            (d.Location.SourceTree?.FilePath ?? "", d.Id, d.GetMessage());

        var changesByPath = new Dictionary<string, IReadOnlyList<TextChange>>(PathComparer);
        foreach (var fileChange in fileChanges)
        {
            if (fileChange.ChangeType == "Modified" && fileChange.FilePath != null)
                changesByPath[fileChange.FilePath] = fileChange.Changes;
        }

        var changedProjects = prepared.GetChanges(baseline).GetProjectChanges().Select(p => p.ProjectId).ToHashSet();
        var dependencyGraph = prepared.GetProjectDependencyGraph();
        var projectsToCheck = new HashSet<ProjectId>(changedProjects);
        foreach (var projectId in changedProjects)
            projectsToCheck.UnionWith(dependencyGraph.GetProjectsThatTransitivelyDependOnThisProject(projectId));

        var introduced = new List<string>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectId in prepared.ProjectIds.Where(projectsToCheck.Contains))
        {
            // Both sides use the generator-aware compilation: for a source-generator
            // project, an edit that newly references a generated member would otherwise
            // raise a phantom CS error against the base (pre-generator) compilation and
            // report a false postApplyError on a clean edit. The proposed project's
            // ProjectId collides with the live one, so its compilation must be computed
            // off-cache (ComputeAugmentedCompilationAsync) rather than via the cached
            // GetProjectCompilationAsync.
            var newProject = prepared.GetProject(projectId)!;
            var newCompilation = await ComputeAugmentedCompilationAsync(newProject, cancellationToken);
            if (newCompilation == null) continue;
            var oldProject = baseline.GetProject(projectId);
            var oldCompilation = oldProject == null ? null : await GetProjectCompilationAsync(oldProject, cancellationToken);

            var carriedPositions = new Dictionary<(string path, string id, string message), List<int>>();
            var oldCounts = new Dictionary<(string path, string id, string message), int>();
            foreach (var d in Errors(oldCompilation))
            {
                var key = Key(d);
                oldCounts[key] = oldCounts.GetValueOrDefault(key) + 1;
                var carried = changesByPath.TryGetValue(key.path, out var changes)
                    ? CarryPosition(d.Location.SourceSpan.Start, changes)
                    : d.Location.SourceSpan.Start;
                if (carried == null) continue;
                if (!carriedPositions.TryGetValue(key, out var positions)) carriedPositions[key] = positions = new List<int>();
                positions.Add(carried.Value);
            }

            foreach (var group in Errors(newCompilation).GroupBy(Key))
            {
                var excess = group.Count() - oldCounts.GetValueOrDefault(group.Key);
                if (excess <= 0) continue;
                var positions = carriedPositions.GetValueOrDefault(group.Key) ?? new List<int>();
                var unmatched = new List<Diagnostic>();
                foreach (var d in group.OrderBy(d => d.Location.SourceSpan.Start))
                {
                    if (!positions.Remove(d.Location.SourceSpan.Start)) unmatched.Add(d);
                }
                foreach (var d in unmatched.Take(excess))
                {
                    var entry = DescribeError(d);
                    if (listed.Add(entry)) introduced.Add(entry);
                }
            }
        }
        return introduced;

        IEnumerable<Diagnostic> Errors(Compilation? compilation) => compilation == null
            ? Enumerable.Empty<Diagnostic>()
            : compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error);
    }

    private string DescribeError(Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        var path = string.IsNullOrEmpty(lineSpan.Path) ? "(no file)" : FormatPath(lineSpan.Path);
        return $"{path} (line {lineSpan.StartLinePosition.Line}) {diagnostic.Id}: {diagnostic.GetMessage()}";
    }

    // Where an offset of the original text lands in the edited text, or null when the
    // edit replaced the text at that offset. Changes are in original-text coordinates
    // and do not overlap.
    private static int? CarryPosition(int position, IReadOnlyList<TextChange> changes)
    {
        var delta = 0;
        foreach (var change in changes.OrderBy(c => c.Span.Start))
        {
            if (change.Span.End <= position)
            {
                delta += (change.NewText?.Length ?? 0) - change.Span.Length;
                continue;
            }
            if (change.Span.Start <= position) return null;
            break;
        }
        return position + delta;
    }
}
