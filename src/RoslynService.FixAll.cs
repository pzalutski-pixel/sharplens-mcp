using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace SharpLensMcp;

// fix_all: apply one code fix to every instance of a diagnostic id across a
// file, project, or the whole solution — "remove all unused usings", "drop
// every unused variable". COMPUTE-ONLY per the write-surface product rule:
// the changed file contents are returned and the agent writes them. The
// provider's own FixAllProvider does the batch when it supports the scope;
// otherwise an iterative fallback fixes the first instance, recomputes, and
// repeats until the diagnostic is gone or no progress is made.
public partial class RoslynService
{
    public async Task<object> FixAllAsync(
        string diagnosticId,
        string scope = "solution",
        string? filePath = null,
        string? projectName = null,
        int fixIndex = 0,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var normalizedScope = scope?.ToLowerInvariant() ?? "solution";
        if (normalizedScope is not ("file" or "project" or "solution"))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"scope must be 'file', 'project', or 'solution' (got '{scope}')",
                context: new { scope });
        }

        // Resolve the scope to stable ids up front (ids survive solution forks,
        // so the fallback loop can re-collect against its working solution).
        DocumentId? scopedFileId = null;
        ProjectId? scopedProjectId = null;
        if (normalizedScope == "file")
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return CreateErrorResponse(ErrorCodes.InvalidParameter,
                    "file scope requires filePath", context: new { scope });
            }
            var document = TryFindDocument(filePath);
            if (document == null)
            {
                return CreateErrorResponse(ErrorCodes.FileNotInSolution,
                    $"File not found in solution: {filePath}", context: new { filePath });
            }
            scopedFileId = document.Id;
        }
        else if (normalizedScope == "project")
        {
            if (string.IsNullOrEmpty(projectName))
            {
                return CreateErrorResponse(ErrorCodes.InvalidParameter,
                    "project scope requires projectName", context: new { scope });
            }
            var project = _solution!.Projects.FirstOrDefault(
                p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project == null)
            {
                return ProjectNotFoundError(projectName);
            }
            scopedProjectId = project.Id;
        }

        var matches = await CollectDiagnosticMatchesAsync(
            _solution!, diagnosticId, scopedFileId, scopedProjectId, cancellationToken);
        if (matches.Count == 0)
        {
            // fix_all reads compiler diagnostics only. A non-compiler id matching
            // nothing is not necessarily "clean" — analyzer-sourced ids are simply
            // out of scope, so say that distinctly rather than "nothing to fix".
            var analyzerIdOutOfScope = !IsCompilerDiagnosticId(diagnosticId);
            var note = analyzerIdOutOfScope
                ? $"'{diagnosticId}' is not a compiler diagnostic id (CSxxxx); fix_all matches compiler diagnostics only, so analyzer-sourced ids are out of scope. Use get_code_fixes at a position for analyzer fixes."
                : $"No '{diagnosticId}' diagnostics in scope — nothing to fix";
            return CreateSuccessResponse(
                data: new
                {
                    appliesEditsAutomatically = false,
                    diagnosticId,
                    scope = normalizedScope,
                    fixTitle = (string?)null,
                    diagnosticsMatched = 0,
                    fallbackMode = false,
                    analyzerIdOutOfScope,
                    files = Array.Empty<object>()
                },
                suggestedNextTools: new[] { note },
                totalCount: 0,
                returnedCount: 0);
        }

        var provider = GetBuiltInCodeFixProviders()
            .FirstOrDefault(p => p.FixableDiagnosticIds.Contains(diagnosticId));
        if (provider == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"No code fix provider handles '{diagnosticId}'",
                hint: "This diagnostic has no automated fix. get_code_fixes shows what is available at a position.",
                context: new { diagnosticId, diagnosticsMatched = matches.Count });
        }

        // The fix is chosen from the first instance: its title and equivalence
        // key drive both the FixAll batch and the fallback loop's selection.
        var (firstDocument, firstDiagnostic) = matches[0];
        var firstActions = await GetCodeActionsForDiagnosticAsync(firstDocument, firstDiagnostic, cancellationToken);
        if (firstActions.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"No code fixes available for '{diagnosticId}'",
                context: new { diagnosticId });
        }
        if (fixIndex < 0 || fixIndex >= firstActions.Count)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid fixIndex {fixIndex}. Available range: 0 to {firstActions.Count - 1}",
                hint: "Call get_code_fixes to list the fixes and their indices.",
                context: new { fixIndex, availableFixCount = firstActions.Count });
        }
        var selectedAction = firstActions[fixIndex];

        Solution? changedSolution = null;
        var fallbackMode = false;

        var fixAllProvider = provider.GetFixAllProvider();
        if (fixAllProvider != null && selectedAction.EquivalenceKey != null)
        {
            try
            {
                changedSolution = await TryBatchFixAllAsync(
                    provider, fixAllProvider, selectedAction.EquivalenceKey, diagnosticId,
                    normalizedScope, matches, firstDocument, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A provider's FixAll can throw on shapes it doesn't expect; the
                // loop below is the safety net.
                changedSolution = null;
            }
        }

        if (changedSolution == null)
        {
            fallbackMode = true;
            changedSolution = await FixAllByLoopAsync(
                diagnosticId, scopedFileId, scopedProjectId, fixIndex, selectedAction.EquivalenceKey,
                matches.Count, cancellationToken);
        }

        if (changedSolution == null || changedSolution == _solution)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"The fix for '{diagnosticId}' produced no changes",
                hint: "The selected fix may not apply across the requested scope.",
                context: new { diagnosticId, fixTitle = selectedAction.Title });
        }

        var files = await BuildUpdatedFileContentsAsync(changedSolution, cancellationToken);

        return CreateSuccessResponse(
            data: new
            {
                appliesEditsAutomatically = false,
                diagnosticId,
                scope = normalizedScope,
                fixTitle = selectedAction.Title,
                diagnosticsMatched = matches.Count,
                fallbackMode,
                files
            },
            suggestedNextTools: new[]
            {
                "Write each files[].updatedContent over its filePath, then sync_documents on them",
                "get_diagnostics afterwards to confirm the diagnostic is cleared"
            },
            totalCount: files.Count,
            returnedCount: files.Count);
    }

    // A compiler diagnostic id is "CS" followed by digits (CS0219, CS8019). Any
    // other shape is analyzer-sourced and outside fix_all's compiler-only scope.
    private static bool IsCompilerDiagnosticId(string id)
    {
        if (id.Length <= 2 || id[0] != 'C' || id[1] != 'S') return false;
        for (var i = 2; i < id.Length; i++)
        {
            if (!char.IsDigit(id[i])) return false;
        }
        return true;
    }

    // Compiler diagnostics matching the id within scope, paired with their
    // document. Reads base compilations (where compiler diagnostics like CS0219
    // / CS8019 live); analyzer-only ids are out of v1 scope.
    private async Task<List<(Document document, Diagnostic diagnostic)>> CollectDiagnosticMatchesAsync(
        Solution solution, string diagnosticId, DocumentId? scopedFileId, ProjectId? scopedProjectId,
        CancellationToken cancellationToken)
    {
        var matches = new List<(Document, Diagnostic)>();

        IEnumerable<Project> projects;
        if (scopedProjectId != null)
        {
            var project = solution.GetProject(scopedProjectId);
            projects = project == null ? Array.Empty<Project>() : new[] { project };
        }
        else if (scopedFileId != null)
        {
            var document = solution.GetDocument(scopedFileId);
            projects = document == null ? Array.Empty<Project>() : new[] { document.Project };
        }
        else
        {
            projects = solution.Projects;
        }

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Id != diagnosticId || diagnostic.Location.SourceTree == null) continue;
                var document = project.GetDocument(diagnostic.Location.SourceTree);
                if (document == null) continue;
                if (scopedFileId != null && document.Id != scopedFileId) continue;
                matches.Add((document, diagnostic));
            }
        }

        return matches;
    }

    private async Task<Solution?> TryBatchFixAllAsync(
        CodeFixProvider provider, FixAllProvider fixAllProvider, string equivalenceKey,
        string diagnosticId, string scope, List<(Document document, Diagnostic diagnostic)> matches,
        Document triggerDocument, CancellationToken cancellationToken)
    {
        var fixAllScope = scope switch
        {
            "file" => FixAllScope.Document,
            "project" => FixAllScope.Project,
            _ => FixAllScope.Solution
        };
        if (!fixAllProvider.GetSupportedFixAllScopes().Contains(fixAllScope)) return null;

        var byDocument = matches
            .GroupBy(m => m.document.Id)
            .ToDictionary(g => g.Key, g => (IEnumerable<Diagnostic>)g.Select(m => m.diagnostic).ToList());

        var context = new FixAllContext(
            triggerDocument, provider, fixAllScope, equivalenceKey,
            new[] { diagnosticId }, new MapDiagnosticProvider(byDocument), cancellationToken);

        var fixAction = await fixAllProvider.GetFixAsync(context);
        if (fixAction == null) return null;

        var operations = await fixAction.GetOperationsAsync(cancellationToken);
        return operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
    }

    // Fallback when the provider has no FixAll support: fix the first instance,
    // recompute against the forked solution, repeat. Bounded by the initial
    // match count, and stops the moment an iteration makes no progress.
    private async Task<Solution?> FixAllByLoopAsync(
        string diagnosticId, DocumentId? scopedFileId, ProjectId? scopedProjectId,
        int fixIndex, string? equivalenceKey, int maxIterations, CancellationToken cancellationToken)
    {
        var working = _solution!;
        var madeProgress = false;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = await CollectDiagnosticMatchesAsync(
                working, diagnosticId, scopedFileId, scopedProjectId, cancellationToken);
            if (matches.Count == 0) break;

            var (document, diagnostic) = matches[0];
            var actions = await GetCodeActionsForDiagnosticAsync(document, diagnostic, cancellationToken);
            // Select the SAME logical fix at every site by its equivalence key (the
            // key the provider path uses), not the positional index — the fixIndex-th
            // action at another site can be a different fix. Fall back to the index
            // only when the chosen fix has no equivalence key.
            var action = equivalenceKey != null
                ? actions.FirstOrDefault(a => a.EquivalenceKey == equivalenceKey)
                : (fixIndex < actions.Count ? actions[fixIndex] : null);
            if (action == null) break;

            var operations = await action.GetOperationsAsync(cancellationToken);
            var next = operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
            if (next == null || next == working) break;

            working = next;
            madeProgress = true;
        }

        return madeProgress ? working : null;
    }

    // Per-modified-document new content, diffed against the live solution.
    private async Task<List<object>> BuildUpdatedFileContentsAsync(
        Solution newSolution, CancellationToken cancellationToken)
    {
        var files = new List<object>();
        foreach (var projectChange in newSolution.GetChanges(_solution!).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                var oldDocument = _solution!.GetDocument(documentId);
                var newDocument = newSolution.GetDocument(documentId);
                if (oldDocument == null || newDocument == null) continue;

                var oldText = await oldDocument.GetTextAsync(cancellationToken);
                var newText = await newDocument.GetTextAsync(cancellationToken);
                var changeCount = newText.GetTextChanges(oldText).Count;
                if (changeCount == 0) continue;

                files.Add(new
                {
                    filePath = FormatPath(newDocument.FilePath),
                    changeCount,
                    updatedContent = newText.ToString()
                });
            }
        }
        return files;
    }

    // Feeds the FixAllContext the diagnostics we already collected, keyed by
    // document — no recomputation inside the provider.
    private sealed class MapDiagnosticProvider : FixAllContext.DiagnosticProvider
    {
        private readonly Dictionary<DocumentId, IEnumerable<Diagnostic>> _byDocument;

        public MapDiagnosticProvider(Dictionary<DocumentId, IEnumerable<Diagnostic>> byDocument) =>
            _byDocument = byDocument;

        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(
            Document document, CancellationToken cancellationToken) =>
            Task.FromResult(_byDocument.TryGetValue(document.Id, out var diagnostics)
                ? diagnostics
                : Enumerable.Empty<Diagnostic>());

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(
            Project project, CancellationToken cancellationToken) =>
            Task.FromResult(Enumerable.Empty<Diagnostic>());

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(
            Project project, CancellationToken cancellationToken)
        {
            var all = new List<Diagnostic>();
            foreach (var document in project.Documents)
            {
                if (_byDocument.TryGetValue(document.Id, out var diagnostics)) all.AddRange(diagnostics);
            }
            return Task.FromResult<IEnumerable<Diagnostic>>(all);
        }
    }
}
