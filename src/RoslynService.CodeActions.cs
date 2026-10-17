using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// The code-action tools: listing the fixes and refactorings Roslyn offers at a position,
// applying one by title, and the wrappers that apply one named refactoring. Each call
// computes the offered actions once and chooses among them. A wrapper applies only its
// own refactoring — extract_variable an "Introduce local", inline_variable an "Inline
// temporary variable" — and fails rather than fall back to a different one;
// encapsulate_field picks the variant whose result compiles; implement_missing_members
// also works from the type declaration, implementing every missing member the compiler
// reports on the type's base list.
public partial class RoslynService
{
    // Guards a two-position code-action selection so a reversed range (end before start)
    // returns a structured error instead of throwing downstream in TextSpan.FromBounds.
    // Both positions are individually validated by ResolvePositionAsync; only their order
    // is unchecked, and FromBounds requires start <= end.
    private object? ValidateSelectionOrder(
        int startPosition, int? endPosition, int line, int column, int? endLine, int? endColumn)
    {
        if (endPosition.HasValue && endPosition.Value < startPosition)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "Invalid selection: the end position precedes the start position",
                hint: "Ensure (endLine, endColumn) is at or after (line, column).",
                context: new { line, column, endLine, endColumn });
        }
        return null;
    }

    // The document and the (validated) selection a code-action call addresses.
    private async Task<(ResolvedPosition? position, int? endPosition, object? error)> ResolveActionSelectionAsync(
        string filePath, int line, int column, int? endLine, int? endColumn, CancellationToken cancellationToken)
    {
        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return (null, null, posError);

        int? endPosition = null;
        if (endLine.HasValue && endColumn.HasValue)
        {
            var (endPos, endError) = await ResolvePositionAsync(filePath, endLine.Value, endColumn.Value, cancellationToken);
            if (endPos == null) return (null, null, endError);
            endPosition = endPos.Position;
        }

        if (ValidateSelectionOrder(pos.Position, endPosition, line, column, endLine, endColumn) is { } orderError)
            return (null, null, orderError);
        return (pos, endPosition, null);
    }

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

        var (pos, endPosition, selectionError) = await ResolveActionSelectionAsync(
            filePath, line, column, endLine, endColumn, cancellationToken);
        if (pos == null) return selectionError!;

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

        var (pos, endPosition, selectionError) = await ResolveActionSelectionAsync(
            filePath, line, column, endLine, endColumn, cancellationToken);
        if (pos == null) return selectionError!;

        var allActions = await GetAllCodeActionsAtPositionAsync(
            pos.Document,
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

        // Roslyn surfaces some refactorings as CodeActionWithNestedActions — a menu
        // wrapper with no operations of its own. Calling GetOperationsAsync on one
        // throws NotSupportedException, so descend into NestedCodeActions to a leaf.
        var (leaf, ambiguousCandidates) = ResolveLeafAction(matchingAction.action, title);
        if (leaf == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AmbiguousMatch,
                $"'{title}' resolves to a menu of actions; no single leaf matches that title",
                hint: "Repeat the call with one of the listed sub-action titles verbatim.",
                context: new { title, candidates = ambiguousCandidates!.Take(10).ToList() }
            );
        }

        var changedSolution = await ChangedSolutionOfAsync(leaf, cancellationToken);
        return await ApplyActionResultAsync(leaf.Title, matchingAction.kind, changedSolution, preview, cancellationToken);
    }

    // The solution a (leaf) code action produces, or null when it produces no change.
    private async Task<Solution?> ChangedSolutionOfAsync(CodeAction action, CancellationToken cancellationToken)
    {
        var operations = await action.GetOperationsAsync(cancellationToken);
        var changed = operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
        return changed == null || changed == _solution ? null : changed;
    }

    // The preview, or the apply, of a code action's result: the shape every code-action
    // tool returns.
    private async Task<object> ApplyActionResultAsync(
        string actionTitle, string actionKind, Solution? changedSolution, bool preview, CancellationToken cancellationToken)
    {
        if (changedSolution == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Code action did not produce any changes",
                hint: "The selected action may not be applicable in this context.",
                context: new { actionTitle }
            );
        }

        if (preview)
        {
            var changedDocuments = await BuildPreviewChangeDetailsAsync(changedSolution, cancellationToken);
            return CreateSuccessResponse(
                data: new
                {
                    applied = false,
                    actionTitle,
                    actionKind,
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
                actionTitle,
                actionKind,
                changedFiles = FormatAppliedChanges(applyReport),
                preview = false,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[] { "get_diagnostics to verify the action resolved any issues" },
            totalCount: applyReport.Count,
            returnedCount: applyReport.Count
        );
    }

    // Every leaf of the offered actions (a menu's children, recursively), with the kind
    // of the action it came from.
    private static List<(CodeAction leaf, string kind)> LeafActions(IEnumerable<(CodeAction action, string kind)> actions)
    {
        var leaves = new List<(CodeAction, string)>();
        void Walk(CodeAction action, string kind, int depth)
        {
            var nested = GetNestedActionsOrEmpty(action);
            if (nested.IsEmpty || depth > 32)
            {
                leaves.Add((action, kind));
                return;
            }
            foreach (var child in nested) Walk(child, kind, depth + 1);
        }
        foreach (var (action, kind) in actions) Walk(action, kind, 0);
        return leaves;
    }

    // The first leaf whose title starts with one of the prefixes, tried in order.
    private static (CodeAction leaf, string kind)? FirstLeafTitled(
        IReadOnlyList<(CodeAction leaf, string kind)> leaves, params string[] titlePrefixes)
    {
        foreach (var prefix in titlePrefixes)
        {
            var match = leaves.FirstOrDefault(l => l.leaf.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (match.leaf != null) return match;
        }
        return null;
    }

    // The compile errors a candidate solution introduces against the loaded one.
    private async Task<IReadOnlyList<string>> IntroducedErrorsOfAsync(Solution candidate, CancellationToken cancellationToken)
    {
        var (prepared, _) = await PrepareForApplyAsync(candidate, cancellationToken);
        if (prepared == null) return new[] { "the edit changes linked documents in conflicting ways" };
        return await IntroducedCompileErrorsAsync(prepared, await ComputeFileChangesAsync(prepared, cancellationToken), cancellationToken);
    }

    // The code fixes, by title, that implement members: interface, abstract class, or
    // either form of explicit implementation.
    private static readonly string[] ImplementTitles =
    {
        "Implement interface", "Implement abstract class", "Implement remaining members", "Implement missing members",
        "Implement all members explicitly"
    };

    // Diagnostics the implement-members fixes resolve: an interface member not
    // implemented (CS0535), an abstract member not overridden (CS0534), and an
    // implementation that is not public or has the wrong return type (CS0737, CS0738).
    private static readonly HashSet<string> MissingMemberDiagnostics = new(StringComparer.Ordinal)
    {
        "CS0535", "CS0534", "CS0737", "CS0738"
    };

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
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        var notFound = CreateErrorResponse(
            ErrorCodes.SymbolNotFound,
            "No 'implement members' action found at this position",
            hint: "Position cursor on a class that implements an interface or extends an abstract class",
            context: new { filePath, line, column });

        // A fix offered at the position itself (on the interface name in the base list,
        // where the compiler reports the missing members).
        var atPosition = LeafActions(await GetAllCodeActionsAtPositionAsync(
            pos.Document, pos.Position, null, includeCodeFixes: true, includeRefactorings: false, cancellationToken));
        if (FirstLeafTitled(atPosition, ImplementTitles) is { } offered)
        {
            return await ApplyActionResultAsync(offered.leaf.Title, offered.kind,
                await ChangedSolutionOfAsync(offered.leaf, cancellationToken), preview, cancellationToken);
        }

        // On the type declaration's header (its identifier, keyword, modifiers or base
        // list): implement every missing member the compiler reports on the type, one fix
        // at a time until none is left.
        var typeDeclaration = pos.Token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration == null
            || pos.Position < typeDeclaration.SpanStart || pos.Position > typeDeclaration.OpenBraceToken.SpanStart
            || pos.Model.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not INamedTypeSymbol typeSymbol)
        {
            return notFound;
        }

        var working = _solution!;
        string? appliedTitle = null;
        for (var round = 0; round < 64; round++)
        {
            var document = working.GetDocument(pos.Document.Id)!;
            var model = await document.GetSemanticModelAsync(cancellationToken);
            var compilation = model!.Compilation;
            var current = Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindSimilarSymbols(typeSymbol, compilation, cancellationToken).FirstOrDefault();
            var node = current?.DeclaringSyntaxReferences
                .FirstOrDefault(r => r.SyntaxTree == model.SyntaxTree)?.GetSyntax(cancellationToken) as TypeDeclarationSyntax;
            if (node?.BaseList == null) break;

            var diagnostic = model.GetDiagnostics(node.BaseList.Span, cancellationToken)
                .FirstOrDefault(d => MissingMemberDiagnostics.Contains(d.Id));
            if (diagnostic == null) break;

            var fixes = (await GetCodeActionsForDiagnosticAsync(document, diagnostic, cancellationToken))
                .Select(a => (a, "fix"));
            if (FirstLeafTitled(LeafActions(fixes), ImplementTitles) is not { } fix) break;
            var next = await ChangedSolutionOfAsync(fix.leaf, cancellationToken);
            if (next == null || next == working) break;
            working = next;
            appliedTitle ??= fix.leaf.Title;
        }

        if (appliedTitle == null) return notFound;
        return await ApplyActionResultAsync(appliedTitle, "fix", working, preview, cancellationToken);
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
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        // Roslyn offers two variants: "Encapsulate field: 'X' (and use property)" replaces
        // the field's uses with the property, "(but still use field)" leaves them. The
        // property variant is preferred; a use the property cannot take (passed by ref or
        // out, CS0206) keeps the field; when that still does not compile, the field
        // variant is used; when neither compiles, nothing is applied.
        var leaves = LeafActions(await GetAllCodeActionsAtPositionAsync(
            pos.Document, pos.Position, null, includeCodeFixes: false, includeRefactorings: true, cancellationToken));
        var useProperty = leaves.FirstOrDefault(l => l.leaf.Title.StartsWith("Encapsulate field", StringComparison.OrdinalIgnoreCase)
            && !l.leaf.Title.Contains("(but still use field)", StringComparison.OrdinalIgnoreCase));
        var keepField = leaves.FirstOrDefault(l => l.leaf.Title.StartsWith("Encapsulate field", StringComparison.OrdinalIgnoreCase)
            && l.leaf.Title.Contains("(but still use field)", StringComparison.OrdinalIgnoreCase));
        if (useProperty.leaf == null && keepField.leaf == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No 'encapsulate field' action found at this position",
                hint: "Position cursor on a field declaration",
                context: new { filePath, line, column, offeredTitles = leaves.Select(l => l.leaf.Title).Distinct().Take(10).ToList() });
        }

        IReadOnlyList<string> firstErrors = Array.Empty<string>();
        if (useProperty.leaf != null && await ChangedSolutionOfAsync(useProperty.leaf, cancellationToken) is { } withProperty)
        {
            firstErrors = await IntroducedErrorsOfAsync(withProperty, cancellationToken);
            if (firstErrors.Count == 0)
                return await ApplyActionResultAsync(useProperty.leaf.Title, useProperty.kind, withProperty, preview, cancellationToken);

            var repaired = await WithFieldAtRefUsesAsync(withProperty, cancellationToken);
            if (repaired != null && (await IntroducedErrorsOfAsync(repaired, cancellationToken)).Count == 0)
                return await ApplyActionResultAsync(useProperty.leaf.Title, useProperty.kind, repaired, preview, cancellationToken);
        }
        if (keepField.leaf != null && await ChangedSolutionOfAsync(keepField.leaf, cancellationToken) is { } withField)
        {
            var errors = await IntroducedErrorsOfAsync(withField, cancellationToken);
            if (errors.Count == 0)
                return await ApplyActionResultAsync(keepField.leaf.Title, keepField.kind, withField, preview, cancellationToken);
            if (firstErrors.Count == 0) firstErrors = errors;
        }

        return CreateErrorResponse(
            ErrorCodes.AnalysisFailed,
            $"Encapsulating the field would not compile: {firstErrors.FirstOrDefault()}",
            hint: "Nothing was applied. Encapsulate the field by hand where its uses need the field itself.",
            context: new { filePath, line, column, errors = firstErrors });
    }

    // The encapsulated solution with every use the property cannot take — passed by ref
    // or out (CS0206) — pointed back at the field the property wraps, or null when there
    // is none to repair.
    private static async Task<Solution?> WithFieldAtRefUsesAsync(Solution encapsulated, CancellationToken cancellationToken)
    {
        var repaired = encapsulated;
        var changed = false;
        foreach (var project in encapsulated.Projects)
        {
            foreach (var document in project.Documents)
            {
                var model = await document.GetSemanticModelAsync(cancellationToken);
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (model == null || root == null) continue;
                var replacements = new Dictionary<SimpleNameSyntax, SimpleNameSyntax>();
                foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken).Where(d => d.Id == "CS0206"))
                {
                    var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                    var name = node switch
                    {
                        SimpleNameSyntax simple => simple,
                        MemberAccessExpressionSyntax access => access.Name,
                        ArgumentSyntax { Expression: SimpleNameSyntax simple } => simple,
                        ArgumentSyntax { Expression: MemberAccessExpressionSyntax access } => access.Name,
                        _ => null
                    };
                    // A property in a ref or out position does not bind: it is the single
                    // candidate (CandidateReason.NotAVariable).
                    var info = name == null ? default : model.GetSymbolInfo(name, cancellationToken);
                    if (name == null || (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IPropertySymbol property) continue;
                    if (await WrappedFieldAsync(encapsulated, property, cancellationToken) is not { } field) continue;
                    replacements[name] = SyntaxFactory.IdentifierName(field.Name).WithTriviaFrom(name);
                }
                if (replacements.Count == 0) continue;
                repaired = repaired.WithDocumentSyntaxRoot(document.Id, root.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]));
                changed = true;
            }
        }
        return changed ? repaired : null;
    }

    // The field a property's getter returns, or null.
    private static async Task<IFieldSymbol?> WrappedFieldAsync(Solution solution, IPropertySymbol property, CancellationToken cancellationToken)
    {
        foreach (var reference in property.GetMethod?.DeclaringSyntaxReferences ?? Enumerable.Empty<SyntaxReference>())
        {
            var syntax = await reference.GetSyntaxAsync(cancellationToken);
            var document = solution.GetDocument(syntax.SyntaxTree);
            var model = document == null ? null : await document.GetSemanticModelAsync(cancellationToken);
            if (model == null) continue;
            foreach (var identifier in syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is IFieldSymbol field
                    && SymbolEqualityComparer.Default.Equals(field.ContainingType, property.ContainingType))
                {
                    return field;
                }
            }
        }
        return null;
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
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        // Only Roslyn's inline-temporary refactoring; never another "Inline ..." action
        // such as inline method.
        var leaves = LeafActions(await GetAllCodeActionsAtPositionAsync(
            pos.Document, pos.Position, null, includeCodeFixes: false, includeRefactorings: true, cancellationToken));
        if (FirstLeafTitled(leaves, "Inline temporary variable", "Inline variable") is not { } inline)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No 'inline variable' action found at this position",
                hint: "Position cursor on a local variable that is assigned once",
                context: new { filePath, line, column, offeredTitles = leaves.Select(l => l.leaf.Title).Distinct().Take(10).ToList() });
        }
        return await ApplyActionResultAsync(inline.leaf.Title, inline.kind,
            await ChangedSolutionOfAsync(inline.leaf, cancellationToken), preview, cancellationToken);
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
        EnsureSolutionLoaded();

        var (pos, endPosition, selectionError) = await ResolveActionSelectionAsync(
            filePath, line, column, endLine, endColumn, cancellationToken);
        if (pos == null) return selectionError!;

        // Only a local: "Introduce local" (this occurrence first, then every occurrence).
        // Introducing a field, constant or parameter changes where and how often the
        // expression is evaluated, so it is never a fallback.
        var leaves = LeafActions(await GetAllCodeActionsAtPositionAsync(
            pos.Document, pos.Position, endPosition, includeCodeFixes: false, includeRefactorings: true, cancellationToken));
        var local = FirstLeafTitled(
            leaves.Where(l => !l.leaf.Title.Contains("all occurrences", StringComparison.OrdinalIgnoreCase)).ToList(),
            "Introduce local for", "Introduce local constant for")
            ?? FirstLeafTitled(leaves, "Introduce local for", "Introduce local constant for");
        if (local is not { } chosen)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No 'extract variable' action found at this position",
                hint: "Select an expression inside a method body; only a local variable is introduced",
                context: new { filePath, line, column, endLine, endColumn, offeredTitles = leaves.Select(l => l.leaf.Title).Distinct().Take(10).ToList() });
        }
        return await ApplyActionResultAsync(chosen.leaf.Title, chosen.kind,
            await ChangedSolutionOfAsync(chosen.leaf, cancellationToken), preview, cancellationToken);
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
