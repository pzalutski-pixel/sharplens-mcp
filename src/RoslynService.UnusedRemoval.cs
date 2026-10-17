using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// remove_unused_code: COMPUTE-ONLY bulk cleanup for find_unused_code results.
// The tool runs the detector, removes every reported declaration (all partial
// declarations; whole field declarations only when every declarator is dead),
// drops using directives that the removals made unnecessary, and returns each
// touched file's full updated content — the agent writes the files itself and
// runs sync_documents. Per product rule, new tools never write. Declarations are
// removed with DeclarationRemoval, so the directives around them (#region, #if)
// stay balanced; a file compiled once per target framework is computed once.
public partial class RoslynService
{
    public async Task<object> RemoveUnusedCodeAsync(
        string? projectName = null,
        bool includePrivate = true,
        bool includeInternal = false,
        string? symbolKindFilter = null,
        List<string>? symbolNames = null,
        int maxSymbols = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ValidateUnusedCodeKindFilter(symbolKindFilter) is { } filterError) return filterError;
        if (ParameterValidation.Limit("maxSymbols", maxSymbols, 500) is { } maxSymbolsError) return CreateErrorResponse(maxSymbolsError);
        if (ResolveProjectScope(projectName).Error is { } projectError) return projectError;

        // An allow-list narrows the detector's candidate sweep up front: only
        // the named symbols get reference-checked instead of the whole project.
        var allowList = symbolNames is { Count: > 0 }
            ? new HashSet<string>(symbolNames, StringComparer.Ordinal)
            : null;
        // For an explicit allow-list the named set IS the bound, so evaluate every
        // candidate (no maxSymbols cap). Capping here would drop allow-listed unused
        // symbols past the cap from data.Symbols and then mislabel them below as
        // "not reported unused". maxSymbols only bounds the unscoped sweep.
        var detectorMaxResults = allowList != null ? int.MaxValue : maxSymbols;
        var data = await FindUnusedCodeDataAsync(
            projectName, includePrivate, includeInternal, symbolKindFilter, detectorMaxResults,
            allowList == null ? null : allowList.Contains,
            cancellationToken);

        var skipped = new List<object>();
        var removed = new List<object>();
        // Per file path: the document the removals are computed on and the spans of the
        // declarations to remove. A declaration of a multi-targeted project is found in
        // the document of each target framework; it is one declaration of one file.
        var removalsByPath = new Dictionary<string, (Document document, HashSet<Microsoft.CodeAnalysis.Text.TextSpan> spans)>(PathComparer);

        foreach (var entry in data.Symbols)
        {
            if (entry.Symbol == null) continue;
            // No allow-list re-check here: FindUnusedCodeDataAsync was already invoked
            // with allowList.Contains as its candidate name predicate, so every entry
            // has passed it.

            var mappedAny = false;
            foreach (var declRef in entry.Symbol.DeclaringSyntaxReferences)
            {
                var document = _solution!.GetDocument(declRef.SyntaxTree);
                if (document?.FilePath == null) continue;

                var node = await declRef.GetSyntaxAsync(cancellationToken);
                var removable = ToRemovableNode(node);
                if (removable == null) continue;

                if (!removalsByPath.TryGetValue(document.FilePath, out var removals))
                {
                    removals = (document, new HashSet<Microsoft.CodeAnalysis.Text.TextSpan>());
                    removalsByPath[document.FilePath] = removals;
                }
                removals.spans.Add(removable.Span);
                mappedAny = true;
            }

            if (mappedAny)
            {
                removed.Add(new
                {
                    name = entry.Name,
                    fullyQualifiedName = entry.FullName,
                    kind = entry.Kind,
                    filePath = entry.FilePath,
                    line = entry.Line
                });
            }
            else
            {
                skipped.Add(new { name = entry.Name, reason = "No removable declaration syntax found" });
            }
        }

        if (allowList != null)
        {
            var reportedNames = data.Symbols.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var requested in allowList.Where(n => !reportedNames.Contains(n)))
            {
                skipped.Add(new { name = requested, reason = "Not reported unused by the detector (may be referenced, filtered out, or misspelled)" });
            }
        }

        // Build the updated contents on a FORKED solution — nothing is applied.
        var files = new List<object>();
        var forked = _solution!;
        foreach (var (_, (document, spans)) in removalsByPath)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null) continue;
            var nodes = spans
                .Select(span => root.FindNode(span, getInnermostNodeForTie: false))
                .Select(node => ToRemovableNode(node) ?? node)
                .Distinct()
                .ToList();

            // A member nested inside a removed type vanishes with the type;
            // passing both to the removal is redundant and fragile.
            var topLevel = nodes
                .Where(n => !nodes.Any(other => other != n && other.Span.Contains(n.Span)))
                .ToList();

            // When EVERY declarator of a multi-declarator field/local (`int a, b;`) is
            // being removed, remove the whole declaration instead — removing the
            // declarators individually would leave a typed declaration with no
            // variables (`int ;`). Partial removals keep the surviving declarators.
            var removalSet = new HashSet<SyntaxNode>(topLevel);
            var toRemove = new List<SyntaxNode>();
            foreach (var node in topLevel)
            {
                if (node is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax decl }
                    && decl.Variables.Count > 1
                    && decl.Variables.All(removalSet.Contains)
                    && decl.Parent is { } wholeDeclaration)
                {
                    if (!toRemove.Contains(wholeDeclaration)) toRemove.Add(wholeDeclaration);
                }
                else
                {
                    toRemove.Add(node);
                }
            }

            var newRoot = DeclarationRemoval.Remove(root, toRemove);
            forked = forked.WithDocumentSyntaxRoot(document.Id, newRoot);

            var (finalRoot, removedUsings) = await RemoveNewlyUnusedUsingsAsync(
                document, forked.GetDocument(document.Id)!, cancellationToken);
            if (removedUsings > 0)
            {
                forked = forked.WithDocumentSyntaxRoot(document.Id, finalRoot);
            }

            files.Add(new
            {
                filePath = FormatPath(document.FilePath),
                removedDeclarations = topLevel.Count,
                removedUsings,
                updatedContent = await MatchDocumentLineEndingsAsync(
                    document, (removedUsings > 0 ? finalRoot : newRoot).ToFullString(), cancellationToken)
            });
        }

        return CreateSuccessResponse(
            data: new
            {
                appliesEditsAutomatically = false,
                detectorTotalCount = data.TotalCount,
                removed,
                skipped,
                files
            },
            suggestedNextTools: files.Count > 0
                ? new[]
                {
                    "Write each files[].updatedContent over its filePath, then sync_documents on those files",
                    "get_diagnostics afterwards to confirm the removals compile",
                    "Run remove_unused_code again to confirm nothing remains"
                }
                : new[] { "Nothing to remove under these filters" },
            totalCount: removed.Count,
            returnedCount: removed.Count);
    }

    // A symbol's declaration reference maps to the node that should disappear:
    // field/event-field symbols reference their declarator — the whole
    // declaration goes only when it declares that single variable.
    private static SyntaxNode? ToRemovableNode(SyntaxNode declarationNode) => declarationNode switch
    {
        VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } declarator =>
            declaration.Variables.Count == 1 ? declaration.Parent : declarator,
        MemberDeclarationSyntax member => member,
        _ => null
    };

    // Using directives the removals made unnecessary: CS8019 on the forked
    // document MINUS CS8019 on the original. Pre-existing unused usings are
    // organize_usings' job, not a side effect of deleting dead code. No
    // reordering for the same reason.
    private static async Task<(SyntaxNode root, int removedUsings)> RemoveNewlyUnusedUsingsAsync(
        Document originalDocument, Document forkedDocument, CancellationToken cancellationToken)
    {
        var forkedRoot = (await forkedDocument.GetSyntaxRootAsync(cancellationToken))!;
        if (forkedRoot is not CompilationUnitSyntax compilationUnit || compilationUnit.Usings.Count == 0)
        {
            return (forkedRoot, 0);
        }

        var originalModel = await originalDocument.GetSemanticModelAsync(cancellationToken);
        var forkedModel = await forkedDocument.GetSemanticModelAsync(cancellationToken);
        if (originalModel == null || forkedModel == null) return (forkedRoot, 0);

        var originalRoot = await originalDocument.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;

        static string DirectiveKey(CompilationUnitSyntax? root, Microsoft.CodeAnalysis.Text.TextSpan span) =>
            (root?.FindNode(span) as UsingDirectiveSyntax)?.ToString().Trim() ?? "";

        // Key CS8019 by the using directive's TEXT, not its source offset: removing a dead
        // member before a using shifts that using's offset, which an offset-keyed diff
        // would misread as "newly unused" and delete a pre-existing unused using
        // (organize_usings' job). A using is newly unnecessary only when it appears as
        // CS8019 in the forked document MORE times than in the original — duplicates of the
        // same simple name are counted, not conflated.
        var originalUnusedCounts = new Dictionary<string, int>();
        foreach (var d in originalModel.GetDiagnostics(cancellationToken: cancellationToken).Where(d => d.Id == "CS8019"))
        {
            var key = DirectiveKey(originalRoot, d.Location.SourceSpan);
            originalUnusedCounts[key] = originalUnusedCounts.GetValueOrDefault(key) + 1;
        }

        var seenForked = new Dictionary<string, int>();
        var newlyUnused = new List<Microsoft.CodeAnalysis.Text.TextSpan>();
        foreach (var d in forkedModel.GetDiagnostics(cancellationToken: cancellationToken).Where(d => d.Id == "CS8019"))
        {
            var key = DirectiveKey(compilationUnit, d.Location.SourceSpan);
            seenForked[key] = seenForked.GetValueOrDefault(key) + 1;
            if (seenForked[key] > originalUnusedCounts.GetValueOrDefault(key))
                newlyUnused.Add(d.Location.SourceSpan);
        }
        if (newlyUnused.Count == 0) return (forkedRoot, 0);

        var doomed = compilationUnit.Usings
            .Where(u => newlyUnused.Any(span => span.Contains(u.Span) || u.Span.Contains(span)))
            .ToList();
        if (doomed.Count == 0) return (forkedRoot, 0);

        // Removing the first using with KeepNoTrivia would delete a file header it carries.
        // Route the surviving directives (the ORIGINAL instances, so the helper's reference
        // identity holds) through PinHeaderTrivia, which re-pins that header to whichever
        // directive ends up first — the same helper organize_usings/add_missing_imports use.
        var survivors = compilationUnit.Usings.Where(u => !doomed.Contains(u)).ToList();
        return (WithUsingsPreservingHeader(compilationUnit, compilationUnit.Usings[0], survivors), doomed.Count);
    }
}
