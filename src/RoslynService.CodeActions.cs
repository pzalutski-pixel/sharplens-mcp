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
        bool includeRefactorings = true)
    {
        EnsureSolutionLoaded();

        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree",
                context: new { filePath, line, column }
            );
        }

        var startPosition = GetPosition(syntaxTree, line, column);
        int? endPosition = null;
        if (endLine.HasValue && endColumn.HasValue)
        {
            endPosition = GetPosition(syntaxTree, endLine.Value, endColumn.Value);
        }

        var allActions = await GetAllCodeActionsAtPositionAsync(
            document,
            startPosition,
            endPosition,
            includeCodeFixes,
            includeRefactorings);

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
        bool preview = true)
    {
        EnsureSolutionLoaded();

        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree",
                context: new { filePath, line, column }
            );
        }

        var startPosition = GetPosition(syntaxTree, line, column);
        int? endPosition = null;
        if (endLine.HasValue && endColumn.HasValue)
        {
            endPosition = GetPosition(syntaxTree, endLine.Value, endColumn.Value);
        }

        var allActions = await GetAllCodeActionsAtPositionAsync(
            document,
            startPosition,
            endPosition,
            includeCodeFixes: true,
            includeRefactorings: true);

        // Find action by title (case-insensitive)
        var matchingAction = allActions.FirstOrDefault(a =>
            a.action.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

        if (matchingAction.action == null)
        {
            // Try partial match
            matchingAction = allActions.FirstOrDefault(a =>
                a.action.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
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

        // Apply the code action
        var operations = await selectedAction.GetOperationsAsync(CancellationToken.None);
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

        // Collect all changed documents (reuse pattern from ApplyCodeFixAsync)
        var changedDocuments = new List<object>();
        var solutionChanges = changedSolution.GetChanges(_solution!);

        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        {
            // Added documents
            foreach (var addedDocId in projectChanges.GetAddedDocuments())
            {
                var addedDoc = changedSolution.GetDocument(addedDocId);
                if (addedDoc == null) continue;

                var text = await addedDoc.GetTextAsync();
                changedDocuments.Add(new
                {
                    filePath = FormatPath(addedDoc.FilePath) ?? $"NewFile_{addedDoc.Name}",
                    fileName = addedDoc.Name,
                    isNewFile = true,
                    newText = preview ? text.ToString() : null,
                    changeType = "Added"
                });

                if (!preview && addedDoc.FilePath != null)
                {
                    await File.WriteAllTextAsync(addedDoc.FilePath, text.ToString());
                }
            }

            // Changed documents
            foreach (var changedDocId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = _solution!.GetDocument(changedDocId);
                var newDoc = changedSolution.GetDocument(changedDocId);
                if (oldDoc == null || newDoc == null) continue;

                var oldText = await oldDoc.GetTextAsync();
                var newText = await newDoc.GetTextAsync();
                var changes = newText.GetTextChanges(oldText).ToList();

                changedDocuments.Add(new
                {
                    filePath = FormatPath(newDoc.FilePath),
                    fileName = newDoc.Name,
                    isNewFile = false,
                    changeCount = changes.Count,
                    newText = preview ? newText.ToString() : null,
                    changes = preview ? changes.Select(c => new
                    {
                        span = new { start = c.Span.Start, end = c.Span.End, length = c.Span.Length },
                        oldText = oldText.ToString(c.Span),
                        newText = c.NewText
                    }).ToList() : null,
                    changeType = "Modified"
                });

                if (!preview && newDoc.FilePath != null)
                {
                    await File.WriteAllTextAsync(newDoc.FilePath, newText.ToString());
                    _solution = changedSolution;
                }
            }

            // Removed documents
            foreach (var removedDocId in projectChanges.GetRemovedDocuments())
            {
                var removedDoc = _solution!.GetDocument(removedDocId);
                if (removedDoc == null) continue;

                changedDocuments.Add(new
                {
                    filePath = FormatPath(removedDoc.FilePath),
                    fileName = removedDoc.Name,
                    isNewFile = false,
                    changeType = "Removed"
                });

                if (!preview && removedDoc.FilePath != null && File.Exists(removedDoc.FilePath))
                {
                    File.Delete(removedDoc.FilePath);
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                applied = !preview,
                actionTitle = selectedAction.Title,
                actionKind = matchingAction.kind,
                changedFiles = changedDocuments,
                preview
            },
            suggestedNextTools: preview
                ? new[] { $"apply_code_action_by_title with preview=false to apply changes to disk" }
                : new[] { "get_diagnostics to verify the action resolved any issues" },
            totalCount: changedDocuments.Count,
            returnedCount: changedDocuments.Count
        );
    }



    /// <summary>
    /// Implement missing interface/abstract members.
    /// </summary>
    public async Task<object> ImplementMissingMembersAsync(
        string filePath,
        int line,
        int column,
        bool preview = true)
    {
        // This is a convenience wrapper around apply_code_action_by_title
        // Looking for actions like "Implement interface", "Implement abstract class"
        var actionsResult = await GetCodeActionsAtPositionAsync(
            filePath, line, column,
            includeCodeFixes: true,
            includeRefactorings: true);

        // Check if we got actions
        var resultDict = actionsResult as dynamic;
        if (resultDict?.success != true)
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

        foreach (var title in implementTitles)
        {
            var result = await ApplyCodeActionByTitleAsync(
                filePath, line, column, title,
                preview: preview);

            var dict = result as dynamic;
            if (dict?.success == true)
            {
                return result;
            }
        }

        return CreateErrorResponse(
            ErrorCodes.SymbolNotFound,
            "No 'implement members' action found at this position",
            hint: "Position cursor on a class that implements an interface or extends an abstract class",
            context: new { filePath, line, column }
        );
    }

    /// <summary>
    /// Encapsulate a field into a property.
    /// </summary>
    public async Task<object> EncapsulateFieldAsync(
        string filePath,
        int line,
        int column,
        bool preview = true)
    {
        // Look for encapsulate field actions
        var encapsulateTitles = new[]
        {
            "Encapsulate field",
            "Encapsulate field (and use property)",
            "Encapsulate field (but still use field)"
        };

        foreach (var title in encapsulateTitles)
        {
            var result = await ApplyCodeActionByTitleAsync(
                filePath, line, column, title,
                preview: preview);

            var dict = result as dynamic;
            if (dict?.success == true)
            {
                return result;
            }
        }

        return CreateErrorResponse(
            ErrorCodes.SymbolNotFound,
            "No 'encapsulate field' action found at this position",
            hint: "Position cursor on a field declaration",
            context: new { filePath, line, column }
        );
    }

    /// <summary>
    /// Inline a variable.
    /// </summary>
    public async Task<object> InlineVariableAsync(
        string filePath,
        int line,
        int column,
        bool preview = true)
    {
        var inlineTitles = new[]
        {
            "Inline variable",
            "Inline temporary variable",
            "Inline 'temp'"
        };

        foreach (var title in inlineTitles)
        {
            var result = await ApplyCodeActionByTitleAsync(
                filePath, line, column, title,
                preview: preview);

            var dict = result as dynamic;
            if (dict?.success == true)
            {
                return result;
            }
        }

        // Try partial match with "Inline"
        var inlineResult = await ApplyCodeActionByTitleAsync(
            filePath, line, column, "Inline",
            preview: preview);

        var inlineDict = inlineResult as dynamic;
        if (inlineDict?.success == true)
        {
            return inlineResult;
        }

        return CreateErrorResponse(
            ErrorCodes.SymbolNotFound,
            "No 'inline variable' action found at this position",
            hint: "Position cursor on a variable that can be inlined",
            context: new { filePath, line, column }
        );
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
        bool preview = true)
    {
        var extractTitles = new[]
        {
            "Introduce local",
            "Extract local variable",
            "Introduce variable for",
            "Extract variable"
        };

        foreach (var title in extractTitles)
        {
            var result = await ApplyCodeActionByTitleAsync(
                filePath, line, column, title,
                endLine, endColumn,
                preview: preview);

            var dict = result as dynamic;
            if (dict?.success == true)
            {
                return result;
            }
        }

        // Try partial match
        var extractResult = await ApplyCodeActionByTitleAsync(
            filePath, line, column, "Introduce",
            endLine, endColumn,
            preview: preview);

        var extractDict = extractResult as dynamic;
        if (extractDict?.success == true)
        {
            return extractResult;
        }

        return CreateErrorResponse(
            ErrorCodes.SymbolNotFound,
            "No 'extract variable' action found at this position",
            hint: "Select an expression to extract",
            context: new { filePath, line, column, endLine, endColumn }
        );
    }


}
