using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace SharpLensMcp;

// fix_all: apply one code fix to every instance of a diagnostic id across a
// file, project, or the whole solution — "remove all unused usings", "drop
// every unused variable". COMPUTE-ONLY per the write-surface product rule:
// the changed file contents are returned and the agent writes them. The fix is
// the fixIndex-th fix offered for the first instance (the order get_code_fixes
// lists), run through the provider that offered it: that provider's own
// FixAllProvider does the batch when it supports the scope; otherwise an iterative
// fallback fixes the first instance, recomputes, and repeats until the diagnostic is
// gone or no progress is made. A fix that is a menu of variants is refused with the
// variants. A project scope covers every target framework of the named project, and a
// diagnostic reported once per target framework at the same place counts once.
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
        IReadOnlyList<ProjectId>? scopedProjectIds = null;
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
            var (projects, projectError) = ResolveProjectScope(projectName);
            if (projects == null) return projectError!;
            scopedProjectIds = projects.Select(p => p.Id).ToList();
        }

        var matches = await CollectDiagnosticMatchesAsync(
            _solution!, diagnosticId, scopedFileId, scopedProjectIds, cancellationToken);
        var diagnosticsMatched = matches
            .Select(m => (m.document.FilePath, m.diagnostic.Location.SourceSpan))
            .Distinct()
            .Count();
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

        // The fix is chosen from the first instance: the fixIndex-th offered fix, with the
        // provider that offered it.
        var (firstDocument, firstDiagnostic) = matches[0];
        var firstFixes = await CodeFixesWithProvidersAsync(firstDocument, firstDiagnostic, cancellationToken);
        if (firstFixes.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"No code fixes available for '{diagnosticId}'",
                hint: "This diagnostic has no automated fix. get_code_fixes shows what is available at a position.",
                context: new { diagnosticId, diagnosticsMatched });
        }
        if (fixIndex < 0 || fixIndex >= firstFixes.Count)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid fixIndex {fixIndex}. Available range: 0 to {firstFixes.Count - 1}",
                hint: "Call get_code_fixes to list the fixes and their indices.",
                context: new { fixIndex, availableFixCount = firstFixes.Count });
        }
        var (provider, offered) = firstFixes[fixIndex];

        // A menu of variants has no operations of its own and no single meaning to apply
        // everywhere; a one-variant menu is that variant.
        var selectedAction = offered;
        for (var nested = GetNestedActionsOrEmpty(selectedAction); nested.Length > 0; nested = GetNestedActionsOrEmpty(selectedAction))
        {
            if (nested.Length > 1)
            {
                return CreateErrorResponse(
                    ErrorCodes.AmbiguousMatch,
                    $"Fix {fixIndex} ('{offered.Title}') is a menu of variants; fix_all applies a single fix",
                    hint: "Apply one variant at each site with apply_code_action_by_title, or choose another fixIndex.",
                    context: new { fixIndex, fixTitle = offered.Title, variants = nested.Select(n => n.Title).ToList() });
            }
            selectedAction = nested[0];
        }
        var equivalenceKey = selectedAction.EquivalenceKey ?? offered.EquivalenceKey;

        Solution? changedSolution = null;
        var fallbackMode = false;

        var fixAllProvider = provider.GetFixAllProvider();
        if (fixAllProvider != null && equivalenceKey != null)
        {
            try
            {
                changedSolution = await TryBatchFixAllAsync(
                    provider, fixAllProvider, equivalenceKey, diagnosticId,
                    normalizedScope, scopedProjectIds?.Count > 1, matches, firstDocument, cancellationToken);
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
                provider, diagnosticId, scopedFileId, scopedProjectIds, fixIndex, equivalenceKey,
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
                diagnosticsMatched,
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

    // The fixes the code-fix providers offer for a diagnostic, each with its provider, in
    // the order get_code_fixes and apply_code_fix list them.
    private async Task<List<(CodeFixProvider provider, CodeAction action)>> CodeFixesWithProvidersAsync(
        Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
    {
        var fixes = new List<(CodeFixProvider, CodeAction)>();
        foreach (var provider in GetBuiltInCodeFixProviders())
        {
            if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id)) continue;
            var context = new CodeFixContext(document, diagnostic, (action, _) => fixes.Add((provider, action)), cancellationToken);
            try
            {
                await provider.RegisterCodeFixesAsync(context);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A provider that cannot handle the diagnostic offers no fix.
            }
        }
        return fixes;
    }

    // Compiler diagnostics matching the id within scope, paired with their
    // document. Reads base compilations (where compiler diagnostics like CS0219
    // / CS8019 live); analyzer-only ids are out of v1 scope.
    private async Task<List<(Document document, Diagnostic diagnostic)>> CollectDiagnosticMatchesAsync(
        Solution solution, string diagnosticId, DocumentId? scopedFileId, IReadOnlyList<ProjectId>? scopedProjectIds,
        CancellationToken cancellationToken)
    {
        var matches = new List<(Document, Diagnostic)>();

        IEnumerable<Project> projects;
        if (scopedProjectIds != null)
        {
            projects = scopedProjectIds.Select(solution.GetProject).Where(p => p != null)!;
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
        string diagnosticId, string scope, bool severalProjects, List<(Document document, Diagnostic diagnostic)> matches,
        Document triggerDocument, CancellationToken cancellationToken)
    {
        // A project scope that covers several target frameworks runs as a solution-wide
        // fix restricted (by the diagnostics it is given) to that project's documents.
        var fixAllScope = scope switch
        {
            "file" => FixAllScope.Document,
            "project" when !severalProjects => FixAllScope.Project,
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
        CodeFixProvider provider, string diagnosticId, DocumentId? scopedFileId, IReadOnlyList<ProjectId>? scopedProjectIds,
        int fixIndex, string? equivalenceKey, int maxIterations, CancellationToken cancellationToken)
    {
        var working = _solution!;
        var madeProgress = false;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = await CollectDiagnosticMatchesAsync(
                working, diagnosticId, scopedFileId, scopedProjectIds, cancellationToken);
            if (matches.Count == 0) break;

            var (document, diagnostic) = matches[0];
            var fixes = await CodeFixesWithProvidersAsync(document, diagnostic, cancellationToken);
            // Select the SAME logical fix at every site: the chosen provider's fix with the
            // chosen equivalence key (a menu's one variant included), not the positional
            // index — the fixIndex-th action at another site can be a different fix. Fall
            // back to the index only when the chosen fix has no equivalence key.
            var leaves = fixes
                .Where(f => f.provider == provider)
                .SelectMany(f => GetNestedActionsOrEmpty(f.action) is { Length: 1 } single ? single.ToArray() : new[] { f.action })
                .ToList();
            var action = equivalenceKey != null
                ? leaves.FirstOrDefault(a => a.EquivalenceKey == equivalenceKey)
                : (fixIndex < fixes.Count ? fixes[fixIndex].action : null);
            if (action == null || !GetNestedActionsOrEmpty(action).IsEmpty) break;

            var operations = await action.GetOperationsAsync(cancellationToken);
            var next = operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
            if (next == null || next == working) break;

            working = next;
            madeProgress = true;
        }

        return madeProgress ? working : null;
    }

    // Per-modified-file new content, prepared exactly as an apply would write it (the
    // file's line-break style, one entry per file however many documents share it), with
    // the number of minimal edited spans against the live solution.
    private async Task<List<object>> BuildUpdatedFileContentsAsync(
        Solution newSolution, CancellationToken cancellationToken)
    {
        var (prepared, _) = await PrepareForApplyAsync(newSolution, cancellationToken);
        return (await ComputeFileChangesAsync(prepared ?? newSolution, cancellationToken))
            .Where(f => f.ChangeType == "Modified")
            .Select(f => (object)new
            {
                filePath = FormatPath(f.FilePath),
                changeCount = f.Changes.Count,
                updatedContent = f.NewText!.ToString()
            })
            .ToList();
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
