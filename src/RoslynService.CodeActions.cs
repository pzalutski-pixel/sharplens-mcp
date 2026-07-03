using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

public partial class RoslynService
{

    /// <summary>
    /// Get all available code actions (fixes + refactorings) at a position.
    /// </summary>
    public async Task<object> GetCodeActionsAtPositionAsync(
        string filePath,
        int line,
        int column,
        int? endLine = null,
        int? endColumn = null,
        bool includeCodeFixes = true,
        bool includeRefactorings = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        int? endPosition = null;
        if (endLine.HasValue && endColumn.HasValue)
        {
            var (endPos, endError) = await ResolvePositionAsync(filePath, endLine.Value, endColumn.Value, cancellationToken);
            if (endPos == null) return endError!;
            endPosition = endPos.Position;
        }

        var allActions = await GetAllCodeActionsAtPositionAsync(
            pos.Document,
            pos.Position,
            endPosition,
            includeCodeFixes,
            includeRefactorings,
            cancellationToken);

        if (allActions.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    position = new { line, column, endLine, endColumn },
                    actions = Array.Empty<object>(),
                    message = "No code actions available at this position"
                },
                suggestedNextTools: new[]
                {
                    "Try a different position or selection",
                    "get_diagnostics to check for issues"
                },
                totalCount: 0,
                returnedCount: 0
            );
        }

        // Group by kind and deduplicate by title
        var actions = allActions
            .GroupBy(a => a.action.Title)
            .Select((g, index) => new
            {
                index,
                title = g.Key,
                kind = g.First().kind,
                equivalenceKey = g.First().action.EquivalenceKey,
                count = g.Count()
            })
            .OrderBy(a => a.kind)
            .ThenBy(a => a.title)
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                position = new { line, column, endLine, endColumn },
                actions,
                fixCount = actions.Count(a => a.kind == "fix"),
                refactoringCount = actions.Count(a => a.kind == "refactoring")
            },
            suggestedNextTools: new[]
            {
                actions.Count > 0 ? $"apply_code_action_by_title with title=\"{actions[0].title}\" to apply" : null
            }.Where(s => s != null).ToArray()!,
            totalCount: actions.Count,
            returnedCount: actions.Count
        );
    }

    /// <summary>
    /// Apply a code action by its title.
    /// </summary>
    public async Task<object> ApplyCodeActionByTitleAsync(
        string filePath,
        int line,
        int column,
        string title,
        int? endLine = null,
        int? endColumn = null,
        bool preview = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;
        var document = pos.Document;

        int? endPosition = null;
        if (endLine.HasValue && endColumn.HasValue)
        {
            var (endPos, endError) = await ResolvePositionAsync(filePath, endLine.Value, endColumn.Value, cancellationToken);
            if (endPos == null) return endError!;
            endPosition = endPos.Position;
        }

        var allActions = await GetAllCodeActionsAtPositionAsync(
            document,
            pos.Position,
            endPosition,
            includeCodeFixes: true,
            includeRefactorings: true,
            cancellationToken);

        // Find action by title (case-insensitive): exact first; a partial match is
        // accepted only when it identifies ONE distinct title. Applying the first of
        // several partial matches would pick an arbitrary action — and with
        // preview:false write it to disk.
        var matchingAction = allActions.FirstOrDefault(a =>
            a.action.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

        if (matchingAction.action == null)
        {
            var partialMatches = allActions
                .Where(a => a.action.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var candidateTitles = partialMatches
                .Select(a => a.action.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidateTitles.Count > 1)
            {
                return CreateErrorResponse(
                    ErrorCodes.AmbiguousMatch,
                    $"Title '{title}' partially matches {candidateTitles.Count} different code actions",
                    hint: "Repeat the call with one of the candidate titles verbatim.",
                    context: new { title, candidates = candidateTitles.Take(10).ToList() }
                );
            }

            matchingAction = partialMatches.FirstOrDefault();
        }

        if (matchingAction.action == null)
        {
            var availableTitles = allActions
                .Select(a => a.action.Title)
                .Distinct()
                .Take(10)
                .ToList();

            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"No code action found with title matching '{title}'",
                hint: availableTitles.Count > 0
                    ? $"Available actions: {string.Join(", ", availableTitles)}"
                    : "No actions available at this position. Try get_code_actions_at_position first.",
                context: new { title, availableCount = allActions.Count }
            );
        }

        var selectedAction = matchingAction.action;

        // Roslyn surfaces some refactorings as CodeActionWithNestedActions — a menu
        // wrapper with no operations of its own. Calling GetOperationsAsync on one
        // throws NotSupportedException, so descend into NestedCodeActions to a leaf.
        var (leaf, ambiguousCandidates) = ResolveLeafAction(selectedAction, title);
        if (leaf == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AmbiguousMatch,
                $"'{title}' resolves to a menu of actions; no single leaf matches that title",
                hint: "Repeat the call with one of the listed sub-action titles verbatim.",
                context: new { title, candidates = ambiguousCandidates!.Take(10).ToList() }
            );
        }
        selectedAction = leaf;

        // Apply the code action
        var operations = await selectedAction.GetOperationsAsync(cancellationToken);
        var changedSolution = _solution;

        foreach (var operation in operations)
        {
            if (operation is ApplyChangesOperation applyChangesOp)
            {
                changedSolution = applyChangesOp.ChangedSolution;
                break;
            }
        }

        if (changedSolution == null || changedSolution == _solution)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Code action did not produce any changes",
                hint: "The selected action may not be applicable in this context.",
                context: new { actionTitle = selectedAction.Title }
            );
        }

        if (preview)
        {
            var changedDocuments = await BuildPreviewChangeDetailsAsync(changedSolution, cancellationToken);
            return CreateSuccessResponse(
                data: new
                {
                    applied = false,
                    actionTitle = selectedAction.Title,
                    actionKind = matchingAction.kind,
                    changedFiles = changedDocuments,
                    preview = true
                },
                suggestedNextTools: new[] { "apply_code_action_by_title with preview=false to apply changes to disk" },
                totalCount: changedDocuments.Count,
                returnedCount: changedDocuments.Count
            );
        }

        var (ok, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(changedSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                actionTitle = selectedAction.Title,
                actionKind = matchingAction.kind,
                changedFiles = FormatAppliedChanges(applyReport),
                preview = false,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[] { "get_diagnostics to verify the action resolved any issues" },
            totalCount: applyReport.Count,
            returnedCount: applyReport.Count
        );
    }



    // Tries each candidate title until one applies. A title that simply isn't
    // offered (SYMBOL_NOT_FOUND) or matches several actions (AMBIGUOUS_MATCH) is
    // skipped to the next candidate; any OTHER error means a title WAS selected
    // but applying it failed — that is surfaced distinctly instead of being masked
    // as the generic "no action found at this position".
    private async Task<object> ApplyFirstMatchingActionAsync(
        IEnumerable<string> titles, Func<string, Task<object>> apply, object notFound)
    {
        object? applyFailure = null;
        foreach (var title in titles)
        {
            var result = await apply(title);
            if (IsSuccessResponse(result)) return result;
            var code = GetResponseErrorCode(result);
            if (code is not (ErrorCodes.SymbolNotFound or ErrorCodes.AmbiguousMatch))
            {
                applyFailure ??= result;
            }
        }
        return applyFailure ?? notFound;
    }

    /// <summary>
    /// Implement missing interface/abstract members.
    /// </summary>
    public async Task<object> ImplementMissingMembersAsync(
        string filePath,
        int line,
        int column,
        bool preview = true,
        CancellationToken cancellationToken = default)
    {
        // This is a convenience wrapper around apply_code_action_by_title
        // Looking for actions like "Implement interface", "Implement abstract class"
        var actionsResult = await GetCodeActionsAtPositionAsync(
            filePath, line, column,
            includeCodeFixes: true,
            includeRefactorings: true,
            cancellationToken: cancellationToken);

        // Check if we got actions
        if (!IsSuccessResponse(actionsResult))
        {
            return actionsResult;
        }

        // Look for implement actions
        var implementTitles = new[]
        {
            "Implement interface",
            "Implement abstract class",
            "Implement all members explicitly",
            "Implement remaining members",
            "Implement missing members"
        };

        return await ApplyFirstMatchingActionAsync(
            implementTitles,
            t => ApplyCodeActionByTitleAsync(filePath, line, column, t, preview: preview, cancellationToken: cancellationToken),
            CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No 'implement members' action found at this position",
                hint: "Position cursor on a class that implements an interface or extends an abstract class",
                context: new { filePath, line, column }));
    }

    /// <summary>
    /// Encapsulate a field into a property.
    /// </summary>
    public async Task<object> EncapsulateFieldAsync(
        string filePath,
        int line,
        int column,
        bool preview = true,
        CancellationToken cancellationToken = default)
    {
        // Roslyn titles the two variants "Encapsulate field: 'X' (and use property)"
        // and "Encapsulate field: 'X' (but still use field)", so a bare
        // "Encapsulate field" probe matches both and is ambiguous. Probe the
        // preferred property variant by its unique suffix first; the generic probe
        // still resolves offerings with a single variant.
        var encapsulateTitles = new[]
        {
            "(and use property)",
            "Encapsulate field"
        };

        return await ApplyFirstMatchingActionAsync(
            encapsulateTitles,
            t => ApplyCodeActionByTitleAsync(filePath, line, column, t, preview: preview, cancellationToken: cancellationToken),
            CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No 'encapsulate field' action found at this position",
                hint: "Position cursor on a field declaration",
                context: new { filePath, line, column }));
    }

    /// <summary>
    /// Inline a variable.
    /// </summary>
    public async Task<object> InlineVariableAsync(
        string filePath,
        int line,
        int column,
        bool preview = true,
        CancellationToken cancellationToken = default)
    {
        var inlineTitles = new[]
        {
            "Inline variable",
            "Inline temporary variable",
            "Inline 'temp'",
            "Inline" // broadest partial probe, tried last
        };

        return await ApplyFirstMatchingActionAsync(
            inlineTitles,
            t => ApplyCodeActionByTitleAsync(filePath, line, column, t, preview: preview, cancellationToken: cancellationToken),
            CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No 'inline variable' action found at this position",
                hint: "Position cursor on a variable that can be inlined",
                context: new { filePath, line, column }));
    }

    /// <summary>
    /// Extract an expression to a variable.
    /// </summary>
    public async Task<object> ExtractVariableAsync(
        string filePath,
        int line,
        int column,
        int? endLine = null,
        int? endColumn = null,
        bool preview = true,
        CancellationToken cancellationToken = default)
    {
        var extractTitles = new[]
        {
            "Introduce local",
            "Extract local variable",
            "Introduce variable for",
            "Extract variable",
            "Introduce" // broadest partial probe, tried last
        };

        return await ApplyFirstMatchingActionAsync(
            extractTitles,
            t => ApplyCodeActionByTitleAsync(filePath, line, column, t, endLine, endColumn, preview: preview, cancellationToken: cancellationToken),
            CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No 'extract variable' action found at this position",
                hint: "Select an expression to extract",
                context: new { filePath, line, column, endLine, endColumn }));
    }

    // Reach Roslyn's internal CodeAction.NestedCodeActions via reflection. The property
    // is internal in Microsoft.CodeAnalysis 5.0 but is the only way to discover whether
    // a CodeAction is a menu wrapper (CodeActionWithNestedActions) vs a leaf action that
    // can produce operations. Cached PropertyInfo per type to avoid repeated lookups.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> _nestedActionsPropCache = new();
    internal static System.Collections.Immutable.ImmutableArray<CodeAction> GetNestedActionsOrEmpty(CodeAction action)
    {
        var prop = _nestedActionsPropCache.GetOrAdd(
            action.GetType(),
            t => t.GetProperty("NestedCodeActions",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic));
        if (prop == null) return System.Collections.Immutable.ImmutableArray<CodeAction>.Empty;
        var value = prop.GetValue(action);
        return value is System.Collections.Immutable.ImmutableArray<CodeAction> arr
            ? arr
            : System.Collections.Immutable.ImmutableArray<CodeAction>.Empty;
    }

    // Descends a (possibly nested) code action to the leaf the caller's title
    // selects. A one-child menu unwraps; in a multi-child menu the first child whose
    // title matches the request is taken (Roslyn lists the most-relevant variant
    // first). When no child matches, returns no leaf and the child titles so the
    // caller can choose.
    internal static (CodeAction? leaf, IReadOnlyList<string>? candidates) ResolveLeafAction(
        CodeAction action, string title)
    {
        var selected = action;
        for (var depth = 0; depth < 32; depth++)
        {
            var nested = GetNestedActionsOrEmpty(selected);
            if (nested.Length == 0) return (selected, null);
            if (nested.Length == 1) { selected = nested[0]; continue; }

            var match = nested.FirstOrDefault(child =>
                child.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (match == null) return (null, nested.Select(n => n.Title).ToList());
            selected = match;
        }
        return (selected, null);
    }
}
