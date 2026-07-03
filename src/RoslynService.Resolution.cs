using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// Shared position and symbol resolution for every position-addressed tool.
// Centralizes the document/semantic-model/syntax-tree pipeline, validates
// line/column ranges with structured errors (previously an out-of-range
// position threw and surfaced as a -32603 internal error), and gives every
// tool the tolerant symbol lookup (declaration-first, tree walk-up, ±1 column)
// that was previously exclusive to rename_symbol.
public partial class RoslynService
{
    internal sealed record ResolvedPosition(
        Document Document,
        SemanticModel Model,
        SyntaxTree Tree,
        SourceText Text,
        int Position,
        SyntaxToken Token);

    // Resolves (filePath, line, column) to a document position. Returns either
    // a ResolvedPosition or a structured error response — never throws for
    // caller-input problems.
    internal async Task<(ResolvedPosition? position, object? error)> ResolvePositionAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = TryFindDocument(filePath);
        if (document == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }));
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics",
                context: new { filePath, line, column }));
        }

        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (syntaxTree == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree",
                context: new { filePath, line, column }));
        }

        var text = await syntaxTree.GetTextAsync(cancellationToken);
        if (line < 0 || line >= text.Lines.Count)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"line {line} is out of range: the file has {text.Lines.Count} lines (zero-based, so valid lines are 0..{text.Lines.Count - 1})",
                hint: "Coordinates are ZERO-based: editor line N is line N-1 here.",
                context: new { filePath, line, column, lineCount = text.Lines.Count }));
        }

        var textLine = text.Lines[line];
        var lineLength = textLine.End - textLine.Start;
        if (column < 0 || column > lineLength)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"column {column} is out of range on line {line}: the line is {lineLength} characters long",
                hint: "Coordinates are ZERO-based: editor column N is column N-1 here.",
                context: new { filePath, line, column, lineLength }));
        }

        var position = textLine.Start + column;
        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var token = root.FindToken(position);

        return (new ResolvedPosition(document, semanticModel, syntaxTree, text, position, token), null);
    }

    // ---- Unified apply path -------------------------------------------------
    //
    // Every preview:false tool funnels its changed Solution through here. One
    // contract: on success the workspace, _solution, both caches, and disk all
    // agree; on failure nothing changed. This replaces three divergent apply
    // strategies (TryApplyChanges, File.WriteAllText + _solution swap, and
    // File.WriteAllText with a stale _solution that forced callers to run
    // sync_documents afterward).

    internal sealed record AppliedChange(
        string? FilePath,
        string FileName,
        string ChangeType,
        int ChangeCount);

    // Builds the changed-file report for a prospective solution without applying it.
    internal async Task<List<AppliedChange>> BuildChangeReportAsync(
        Solution newSolution, CancellationToken cancellationToken = default)
    {
        var report = new List<AppliedChange>();
        var solutionChanges = newSolution.GetChanges(_solution!);

        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        {
            foreach (var addedId in projectChanges.GetAddedDocuments())
            {
                var doc = newSolution.GetDocument(addedId);
                if (doc == null) continue;
                report.Add(new AppliedChange(FormatPath(doc.FilePath), doc.Name, "Added", 1));
            }

            foreach (var changedId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = _solution!.GetDocument(changedId);
                var newDoc = newSolution.GetDocument(changedId);
                if (oldDoc == null || newDoc == null) continue;
                var oldText = await oldDoc.GetTextAsync(cancellationToken);
                var newText = await newDoc.GetTextAsync(cancellationToken);
                var changeCount = newText.GetTextChanges(oldText).Count;
                if (changeCount == 0) continue;
                report.Add(new AppliedChange(FormatPath(newDoc.FilePath), newDoc.Name, "Modified", changeCount));
            }

            foreach (var removedId in projectChanges.GetRemovedDocuments())
            {
                var doc = _solution!.GetDocument(removedId);
                if (doc == null) continue;
                report.Add(new AppliedChange(FormatPath(doc.FilePath), doc.Name, "Removed", 1));
            }
        }

        return report;
    }

    // Rich per-document preview details (full new text + per-span changes) for a
    // prospective solution. Preview-only — nothing is applied or written. Used by
    // apply_code_fix and apply_code_action_by_title preview responses.
    internal async Task<List<object>> BuildPreviewChangeDetailsAsync(
        Solution newSolution, CancellationToken cancellationToken = default)
    {
        var changedDocuments = new List<object>();
        var solutionChanges = newSolution.GetChanges(_solution!);

        // Every entry carries the SAME key set regardless of changeType so a consumer
        // can read one shape: filePath, fileName, isNewFile, changeType, changeCount,
        // changes[], newText. Added/Removed have no per-span diff (changeCount 0,
        // changes empty); a removed file has no newText (null).
        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        {
            foreach (var addedDocId in projectChanges.GetAddedDocuments())
            {
                var addedDoc = newSolution.GetDocument(addedDocId);
                if (addedDoc == null) continue;
                var text = await addedDoc.GetTextAsync(cancellationToken);
                changedDocuments.Add(new
                {
                    filePath = FormatPath(addedDoc.FilePath) ?? $"NewFile_{addedDoc.Name}",
                    fileName = addedDoc.Name,
                    isNewFile = true,
                    changeType = "Added",
                    changeCount = 0,
                    changes = Array.Empty<object>(),
                    newText = (string?)text.ToString()
                });
            }

            foreach (var changedDocId in projectChanges.GetChangedDocuments())
            {
                var oldDoc = _solution!.GetDocument(changedDocId);
                var newDoc = newSolution.GetDocument(changedDocId);
                if (oldDoc == null || newDoc == null) continue;

                var oldText = await oldDoc.GetTextAsync(cancellationToken);
                var newText = await newDoc.GetTextAsync(cancellationToken);
                var changes = newText.GetTextChanges(oldText).ToList();

                changedDocuments.Add(new
                {
                    filePath = FormatPath(newDoc.FilePath),
                    fileName = newDoc.Name,
                    isNewFile = false,
                    changeType = "Modified",
                    changeCount = changes.Count,
                    changes = changes.Select(c => (object)new
                    {
                        span = new { start = c.Span.Start, end = c.Span.End, length = c.Span.Length },
                        oldText = oldText.ToString(c.Span),
                        newText = c.NewText
                    }).ToArray(),
                    newText = (string?)newText.ToString()
                });
            }

            foreach (var removedDocId in projectChanges.GetRemovedDocuments())
            {
                var removedDoc = _solution!.GetDocument(removedDocId);
                if (removedDoc == null) continue;
                changedDocuments.Add(new
                {
                    filePath = FormatPath(removedDoc.FilePath),
                    fileName = removedDoc.Name,
                    isNewFile = false,
                    changeType = "Removed",
                    changeCount = 0,
                    changes = Array.Empty<object>(),
                    newText = (string?)null
                });
            }
        }

        return changedDocuments;
    }

    // The apply report rendered as the canonical changed-file shape — the SAME key set
    // BuildPreviewChangeDetailsAsync emits (filePath, fileName, isNewFile, changeType,
    // changeCount, changes, newText) — so every apply tool surfaces `changedFiles`
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

    // Applies a changed Solution atomically. On success: workspace owns the new
    // solution, _solution tracks workspace.CurrentSolution, caches are cleared
    // (and disk is written, for MSBuildWorkspace documents). On failure: no state
    // is touched and a structured error is returned.
    internal async Task<(bool ok, object? error, List<AppliedChange> changes, IReadOnlyList<string> introducedErrors)> ApplyAndReportAsync(
        Solution newSolution, CancellationToken cancellationToken = default)
    {
        var report = await BuildChangeReportAsync(newSolution, cancellationToken);

        // The compile errors this edit introduces (the proposed solution equals what is
        // applied). Surfaced in the result by every caller as data.postApplyErrors. The
        // contract: the edit STILL applies (success:true / applied:true — disclosed so the
        // caller can revert), but a non-empty postApplyErrors flips the MCP envelope's
        // isError to true (HandleToolCallAsync), so a tool that emits
        // syntactically-valid-but-broken code is never read as a clean success. A property
        // of the shared apply path (R4).
        var introducedErrors = await IntroducedCompileErrorsAsync(newSolution, cancellationToken);

        if (!_workspace!.TryApplyChanges(newSolution))
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

    // Compile errors the proposed solution introduces that were not already present —
    // compared at the PROJECT level (the whole compilation, not just the changed
    // documents) so an edit that compiles locally but breaks a caller in an untouched
    // file is caught too. Keyed by file path + id + message: the file path distinguishes
    // a genuinely-new error from a pre-existing one elsewhere that happens to share an
    // id+message, while leaving the line number out keeps a pure line shift from reading
    // as new. Empty means the edit compiles.
    private async Task<IReadOnlyList<string>> IntroducedCompileErrorsAsync(
        Solution newSolution, CancellationToken cancellationToken)
    {
        static string Key(Diagnostic d) =>
            $"{d.Location.SourceTree?.FilePath} {d.Id} {d.GetMessage()}";

        var introduced = new List<string>();
        foreach (var projChange in newSolution.GetChanges(_solution!).GetProjectChanges())
        {
            var oldProject = _solution!.GetProject(projChange.ProjectId);
            var newProject = newSolution.GetProject(projChange.ProjectId);
            if (oldProject == null || newProject == null) continue;

            // Both sides use the generator-aware compilation: for a source-generator
            // project, an edit that newly references a generated member would otherwise
            // raise a phantom CS error against the base (pre-generator) compilation and
            // report a false postApplyError on a clean edit. The proposed project's
            // ProjectId collides with the live one, so its compilation must be computed
            // off-cache (ComputeAugmentedCompilationAsync) rather than via the cached
            // GetProjectCompilationAsync.
            var newCompilation = await ComputeAugmentedCompilationAsync(newProject, cancellationToken);
            if (newCompilation == null) continue;

            var oldCompilation = await GetProjectCompilationAsync(oldProject, cancellationToken);
            var preExisting = oldCompilation == null
                ? new HashSet<string>()
                : oldCompilation.GetDiagnostics(cancellationToken)
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(Key)
                    .ToHashSet();

            foreach (var d in newCompilation.GetDiagnostics(cancellationToken))
            {
                if (d.Severity != DiagnosticSeverity.Error) continue;
                if (preExisting.Contains(Key(d))) continue;
                var lineSpan = d.Location.GetLineSpan();
                var fileName = System.IO.Path.GetFileName(lineSpan.Path);
                if (string.IsNullOrEmpty(fileName)) fileName = "(no file)";
                introduced.Add($"{fileName} (line {lineSpan.StartLinePosition.Line}) {d.Id}: {d.GetMessage()}");
            }
        }
        return introduced;
    }

    internal sealed record ResolvedAddress(string FilePath, int Line, int Column);

    // Resolves typeName(+memberName, +overloadIndex) to the symbol it denotes.
    // Type ambiguity propagates the AMBIGUOUS_TYPE error from FindTypeByNameAsync;
    // an overloaded member without an explicit overloadIndex is refused with the
    // candidate list rather than silently picking the first overload — the result
    // feeds refactorings that rewrite the whole solution.
    internal async Task<(ISymbol? symbol, object? error)> ResolveSymbolByNameAsync(
        string typeName, string? memberName = null, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        var (type, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return (null, ambiguousType);
        if (type == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName }));
        }

        if (memberName == null)
        {
            if (overloadIndex != null)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    "overloadIndex requires memberName",
                    hint: "Supply the memberName the overloadIndex selects among, or drop overloadIndex to target the type itself.",
                    context: new { typeName, overloadIndex }));
            }
            return (type, null);
        }

        // Constructors are named ".ctor" in the symbol model; also accept the
        // type's simple name as the conventional way to address them.
        var members = (memberName == ".ctor" || memberName == type.Name
                ? type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared).Cast<ISymbol>()
                : type.GetMembers(memberName)
                    .Where(m => m is not IMethodSymbol method
                             || method.MethodKind is MethodKind.Ordinary))
            // GetMembers order is not guaranteed stable across calls (it can shift
            // after a cache invalidation between a list call and an apply call), so
            // an overloadIndex would drift. Order by source location for a stable key.
            .OrderBy(m => m.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath ?? "", StringComparer.Ordinal)
            .ThenBy(m => m.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
            .ThenBy(m => m.ToDisplayString(), StringComparer.Ordinal)
            .ToList();

        if (members.Count == 0)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Member '{memberName}' not found in type '{typeName}'",
                hint: "Use get_type_members to see available members",
                context: new { typeName, memberName }));
        }

        if (members.Count > 1 && overloadIndex == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AmbiguousMatch,
                $"Member '{memberName}' has {members.Count} overloads in '{typeName}'",
                hint: "Repeat the call with the overloadIndex of the intended candidate.",
                context: new
                {
                    typeName,
                    memberName,
                    candidates = members
                        .Select((m, i) => new { overloadIndex = i, signature = m.ToDisplayString() })
                        .ToList()
                }));
        }

        var index = overloadIndex ?? 0;
        if (index < 0 || index >= members.Count)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid overloadIndex {index}. Available: 0 to {members.Count - 1}",
                context: new { overloadIndex, availableOverloads = members.Count }));
        }

        return (members[index], null);
    }

    // Addressing-mode gate for tools that accept BOTH positional (filePath/line/
    // column) and name-based (typeName/memberName/overloadIndex) addressing.
    // Exactly one complete mode must be supplied; name-based addressing is
    // normalized to the declaration's identifier position so either mode flows
    // through the same position-based pipeline. memberNameRequired is set by
    // tools that target a member (change_signature, add_null_checks) so a bare
    // typeName fails up front instead of resolving to the type declaration and
    // failing later with a confusing NOT_A_METHOD.
    //
    // This is the AUTHORITATIVE "exactly one mode" enforcement point. The tool
    // schemas express the either/or in prose rather than JSON-Schema oneOf,
    // because MCP clients honor oneOf inconsistently and a parameter the client
    // lets through must still be validated server-side. Both-or-neither returns
    // InvalidParameter here, exercised by the name-based-addressing tests.
    internal async Task<(ResolvedAddress? address, object? error)> ResolveAddressingAsync(
        string? filePath, int? line, int? column,
        string? typeName, string? memberName, int? overloadIndex,
        bool memberNameRequired = false,
        CancellationToken cancellationToken = default)
    {
        var positionalSupplied = filePath != null || line != null || column != null;
        var nameSupplied = typeName != null || memberName != null || overloadIndex != null;

        if (positionalSupplied && nameSupplied)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "Both addressing modes were supplied. Use either filePath/line/column (positional) or typeName with optional memberName/overloadIndex (name-based), not both.",
                context: new { filePath, line, column, typeName, memberName, overloadIndex }));
        }

        if (!positionalSupplied && !nameSupplied)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "No addressing was supplied. Use either filePath/line/column (positional) or typeName with optional memberName/overloadIndex (name-based)."));
        }

        if (positionalSupplied)
        {
            if (filePath == null || line == null || column == null)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    "Positional addressing requires all of filePath, line, and column.",
                    context: new
                    {
                        filePathSupplied = filePath != null,
                        lineSupplied = line != null,
                        columnSupplied = column != null
                    }));
            }
            return (new ResolvedAddress(filePath, line.Value, column.Value), null);
        }

        if (typeName == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "memberName and overloadIndex require typeName.",
                context: new { memberName, overloadIndex }));
        }

        if (memberNameRequired && memberName == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "This tool targets a member: name-based addressing requires memberName alongside typeName.",
                context: new { typeName }));
        }

        var (symbol, symbolError) = await ResolveSymbolByNameAsync(
            typeName, memberName, overloadIndex, cancellationToken);
        if (symbol == null) return (null, symbolError);

        // Prefer a partial method's implementing part (it carries the body), and
        // pick the earliest source location by (file, span) so the address is
        // deterministic for a partial type declared across several files.
        var locationSymbol = symbol is IMethodSymbol { PartialImplementationPart: { } impl } ? impl : symbol;
        var location = locationSymbol.Locations
            .Where(l => l.IsInSource)
            .OrderBy(l => l.SourceTree?.FilePath ?? "", StringComparer.Ordinal)
            .ThenBy(l => l.SourceSpan.Start)
            .FirstOrDefault();
        if (location == null || location.SourceTree?.FilePath is not { Length: > 0 } sourcePath)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"'{symbol.ToDisplayString()}' has no source location (defined in metadata or generated code)",
                hint: "Name-based addressing only works for symbols declared in solution source files.",
                context: new { typeName, memberName }));
        }

        var start = location.GetLineSpan().StartLinePosition;
        return (new ResolvedAddress(sourcePath, start.Line, start.Character), null);
    }

    // Resolves (filePath, line, column) to the symbol at that position using
    // the tolerant strategy chain (declaration-first, GetSymbolInfo, ancestor
    // walk-up, ±1-column retry). Returns (symbol, position, null) on success
    // or (null, maybe-position, error) on failure.
    internal async Task<(ISymbol? symbol, ResolvedPosition? position, object? error)> ResolveSymbolAtAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        var (position, error) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (position == null)
        {
            return (null, null, error);
        }

        var (symbol, debugInfo) = TryFindSymbolForRename(
            position.Tree, position.Model, position.Position, line, column);

        if (symbol == null)
        {
            return (null, position, CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column, debug = debugInfo }));
        }

        return (symbol, position, null);
    }
}
