using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

public partial class RoslynService
{
    public async Task<object> GetSymbolInfoAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        var symbolData = await FormatSymbolInfoAsync(symbol);

        return CreateSuccessResponse(
            data: symbolData,
            suggestedNextTools: new[]
            {
                $"find_references to see all usages of {symbol.Name}",
                symbol is Microsoft.CodeAnalysis.INamedTypeSymbol ? $"get_type_members for {symbol.Name} to see members" : null,
                $"go_to_definition to navigate to {symbol.Name}'s definition"
            }.Where(s => s != null).ToArray()!
        );
    }

    public async Task<object> FindReferencesAsync(
        string filePath, int line, int column, int? maxResults = null, string? kindFilter = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var maxResultsToReturn = maxResults ?? 100;
        var filterKind = string.IsNullOrWhiteSpace(kindFilter) ? null : kindFilter!.ToLowerInvariant();

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        var references = await SymbolFinder.FindReferencesAsync(symbol, _solution!, cancellationToken);
        var allLocations = references
            .SelectMany(r => r.Locations)
            .Where(loc => loc.Location.IsInSource)
            .ToList();

        // The generated scan is authoritative for references inside generator output:
        // SymbolFinder now also returns them (the workspace compilation carries generated
        // trees), so the SymbolFinder loop is deduped against the scan by (file, line,
        // column) and the scan supplies the flagged entries — each generated reference once.
        var generatedHits = await ScanGeneratedReferencesAsync(symbol, cancellationToken);
        var generatedSites = new HashSet<(string?, int, int)>(
            generatedHits.Select(h => ((string?)h.HintPath, h.Line, h.Column)));

        var totalReferences = 0;
        var totalAfterFilter = 0;
        var referenceList = new List<object>();

        foreach (var loc in allLocations)
        {
            var refTree = loc.Location.SourceTree;
            if (refTree == null) continue;

            var refSpan = loc.Location.SourceSpan;
            var lineSpan = refTree.GetLineSpan(refSpan, cancellationToken);
            if (generatedSites.Contains((refTree.FilePath,
                lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character)))
                continue;

            var refDocument = _solution!.GetDocument(loc.Document.Id);
            if (refDocument == null) continue;

            var text = refTree.GetText(cancellationToken);
            var lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

            var refRoot = await refTree.GetRootAsync(cancellationToken);
            var refNode = refRoot.FindNode(refSpan);
            var kind = ClassifyReferenceKind(refNode);

            totalReferences++;

            // Apply kind filter (case-insensitive); count distinct filtered-but-not-paginated total.
            if (filterKind != null && !string.Equals(kind, filterKind, StringComparison.OrdinalIgnoreCase))
                continue;

            totalAfterFilter++;
            if (referenceList.Count >= maxResultsToReturn)
                continue;

            referenceList.Add(new
            {
                filePath = FormatPath(refDocument.FilePath),
                line = lineSpan.StartLinePosition.Line,
                column = lineSpan.StartLinePosition.Character,
                lineText,
                kind
            });
        }

        // References inside generator output, surfaced once (deduped against above).
        foreach (var hit in generatedHits)
        {
            totalReferences++;
            if (filterKind != null && !string.Equals(hit.Kind, filterKind, StringComparison.OrdinalIgnoreCase))
                continue;
            totalAfterFilter++;
            if (referenceList.Count >= maxResultsToReturn) continue;
            referenceList.Add(new
            {
                filePath = FormatPath(hit.HintPath),
                line = hit.Line,
                column = hit.Column,
                lineText = hit.LineText,
                kind = hit.Kind,
                inGeneratedCode = true
            });
        }

        // When no filter is set, totalAfterFilter equals totalReferences; we omit it from the
        // response to avoid noise. When set, the caller gets both numbers.
        object data = filterKind != null
            ? new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                kindFilter = filterKind,
                totalReferences,
                totalReferencesAfterFilter = totalAfterFilter,
                references = referenceList
            }
            : new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                totalReferences,
                references = referenceList
            };

        return CreateSuccessResponse(
            data: data,
            suggestedNextTools: new[]
            {
                $"get_symbol_info to get details about {symbol.Name}",
                $"find_callers to see methods that call {symbol.Name}",
                symbol is INamedTypeSymbol ? $"get_type_members for {symbol.Name}" : null
            }.Where(s => s != null).ToArray()!,
            totalCount: filterKind != null ? totalAfterFilter : totalReferences,
            returnedCount: referenceList.Count
        );
    }

    public async Task<object> GoToDefinitionAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        var definitionLocation = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);

        if (definitionLocation == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Symbol definition not in source",
                hint: "Symbol is defined in metadata (external library). Use get_type_members to explore its API.",
                context: new { symbolName = symbol.Name, symbolKind = symbol.Kind.ToString(), fullyQualifiedName = symbol.ToDisplayString() }
            );
        }

        var defLineSpan = definitionLocation.GetLineSpan();

        return CreateSuccessResponse(
            data: new
            {
                symbol = new
                {
                    name = symbol.Name,
                    kind = symbol.Kind.ToString(),
                    fullyQualifiedName = symbol.ToDisplayString(),
                    containingType = symbol.ContainingType?.ToDisplayString(),
                    containingNamespace = symbol.ContainingNamespace?.ToDisplayString()
                },
                definition = new
                {
                    filePath = FormatPath(defLineSpan.Path),
                    line = defLineSpan.StartLinePosition.Line,
                    column = defLineSpan.StartLinePosition.Character,
                    endLine = defLineSpan.EndLinePosition.Line,
                    endColumn = defLineSpan.EndLinePosition.Character
                }
            },
            suggestedNextTools: new[]
            {
                $"find_references to see all usages of {symbol.Name}",
                $"get_symbol_info for more details about {symbol.Name}",
                symbol is INamedTypeSymbol ? $"get_type_members for {symbol.Name}" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    public async Task<object> FindImplementationsAsync(
        string filePath, int line, int column, int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var maxResultsToReturn = maxResults ?? 50;

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not INamedTypeSymbol typeSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "Not a type symbol",
                hint: "This tool requires a type symbol (interface, class, or abstract class). Use get_derived_types with a type name instead.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        var implementations = await SymbolFinder.FindImplementationsAsync(
            typeSymbol, _solution!, cancellationToken: cancellationToken);
        var allImplementations = implementations.ToList();
        var totalImplementations = allImplementations.Count;

        // The generated scan is authoritative for implementers declared in generator output.
        // SymbolFinder now also returns them, so flag the SymbolFinder entry whose name the
        // scan reports as generated; the scan then only adds implementers SymbolFinder missed.
        var generatedImpls = await ScanGeneratedImplementationsAsync(typeSymbol, cancellationToken);
        var generatedImplNames = new HashSet<string>(generatedImpls.Select(h => h.Name), StringComparer.Ordinal);

        var implementationList = new List<object>();
        foreach (var impl in allImplementations)
        {
            if (implementationList.Count >= maxResultsToReturn)
                break;

            var locations = impl.Locations
                .Where(loc => loc.IsInSource)
                .Select(loc =>
                {
                    var lineSpan = loc.GetLineSpan();
                    return new
                    {
                        filePath = FormatPath(lineSpan.Path),
                        line = lineSpan.StartLinePosition.Line,
                        column = lineSpan.StartLinePosition.Character
                    };
                })
                .ToList();

            // Flag a SymbolFinder implementer the scan reports as generated, matching
            // find_references/find_callers; the scan below then adds only the rest.
            if (generatedImplNames.Contains(impl.ToDisplayString()))
                implementationList.Add(new
                {
                    name = impl.ToDisplayString(),
                    kind = GetTypeKindString(impl),
                    containingNamespace = impl.ContainingNamespace?.ToDisplayString(),
                    locations,
                    inGeneratedCode = true
                });
            else
                implementationList.Add(new
                {
                    name = impl.ToDisplayString(),
                    kind = GetTypeKindString(impl),
                    containingNamespace = impl.ContainingNamespace?.ToDisplayString(),
                    locations
                });
        }

        // Implementers declared in generator output that SymbolFinder did NOT return,
        // deduped by name against the SymbolFinder results above.
        var seenImplementations = new HashSet<string>(
            allImplementations.Select(i => i.ToDisplayString()), StringComparer.Ordinal);
        foreach (var hit in generatedImpls)
        {
            if (!seenImplementations.Add(hit.Name)) continue;
            totalImplementations++;
            if (implementationList.Count >= maxResultsToReturn) continue;
            implementationList.Add(new
            {
                name = hit.Name,
                kind = hit.Kind,
                containingNamespace = hit.ContainingNamespace,
                locations = new[] { new { filePath = FormatPath(hit.HintPath), line = hit.Line, column = hit.Column } }.Cast<object>().ToList(),
                inGeneratedCode = true
            });
        }

        return CreateSuccessResponse(
            data: new
            {
                baseType = typeSymbol.ToDisplayString(),
                totalImplementations,
                implementations = implementationList
            },
            suggestedNextTools: new[]
            {
                $"get_type_members for each implementation to see their members",
                $"get_type_hierarchy for {typeSymbol.Name} to see full inheritance tree"
            },
            totalCount: totalImplementations,
            returnedCount: implementationList.Count
        );
    }

    public async Task<object> GetTypeHierarchyAsync(
        string filePath, int line, int column, int? maxDerivedTypes = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var maxDerivedToReturn = maxDerivedTypes ?? 50; // Default to 50

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not INamedTypeSymbol typeSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "Not a type symbol",
                hint: "This tool requires a type symbol (class, struct, interface). Use get_base_types or get_derived_types with a type name instead.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        // Get base types
        var baseTypes = new List<object>();
        var currentBase = typeSymbol.BaseType;
        while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object)
        {
            baseTypes.Add(FormatTypeInfo(currentBase));
            currentBase = currentBase.BaseType;
        }

        // Get interfaces (capped like derivedTypes, with the true total reported).
        var allInterfaces = typeSymbol.AllInterfaces;
        var interfaces = allInterfaces
            .Take(maxDerivedToReturn)
            .Select(i => FormatTypeInfo(i))
            .ToList();

        // Get derived types
        var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
            typeSymbol, _solution!, transitive: false, cancellationToken: cancellationToken);
        var allDerived = derivedTypes.ToList();
        var totalDerived = allDerived.Count;

        var derivedList = allDerived
            .Take(maxDerivedToReturn)
            .Select(d => FormatTypeInfo(d))
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                typeName = typeSymbol.ToDisplayString(),
                baseTypes,
                interfaces,
                totalInterfaces = allInterfaces.Length,
                derivedTypes = derivedList,
                totalDerivedTypes = totalDerived
            },
            suggestedNextTools: new[]
            {
                $"get_type_members for {typeSymbol.Name} to see all members",
                "get_derived_types by name for transitive derived types",
                "find_implementations for interface implementations"
            },
            totalCount: totalDerived,
            returnedCount: derivedList.Count
        );
    }

    public async Task<object> SearchSymbolsAsync(
        string query, string? kind, int maxResults, string? namespaceFilter = null, int offset = 0,
        string matchMode = "glob",
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var mode = matchMode?.ToLowerInvariant() ?? "glob";
        if (mode is not ("glob" or "fuzzy"))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"matchMode must be 'glob' or 'fuzzy' (got '{matchMode}')",
                context: new { matchMode });
        }

        var allResults = new List<object>();

        // Check if query contains glob patterns
        bool isGlobPattern = query.Contains('*') || query.Contains('?');

        foreach (var project in _solution!.Projects)
        {
            IEnumerable<ISymbol> symbols;
            if (mode == "fuzzy")
            {
                // Roslyn's IDE pattern matcher: camel-hump ("RSvc" finds
                // RoslynService), substring, and fuzzy matching — the search
                // shape agents actually attempt.
                symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                    project, query, SymbolFilter.All, cancellationToken);
            }
            else
            {
                var compilation = await GetProjectCompilationAsync(project, cancellationToken);
                if (compilation == null) continue;
                symbols = compilation.GetSymbolsWithName(
                    name => isGlobPattern ? MatchesGlobPattern(name, query) : name.Contains(query, StringComparison.OrdinalIgnoreCase),
                    SymbolFilter.All,
                    cancellationToken);
            }

            foreach (var symbol in symbols)
            {
                // Filter by kind
                if (!string.IsNullOrEmpty(kind))
                {
                    bool matches = false;

                    // For type symbols (Class, Interface, Struct, Enum), check TypeKind
                    if (symbol is INamedTypeSymbol namedType)
                    {
                        matches = GetTypeKindString(namedType).Equals(kind, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        // For other symbols (Method, Property, Field, etc.), check SymbolKind
                        matches = symbol.Kind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase);
                    }

                    if (!matches)
                        continue;
                }

                // Filter by namespace
                if (!string.IsNullOrEmpty(namespaceFilter))
                {
                    var symbolNamespace = symbol.ContainingNamespace?.ToDisplayString() ?? "";
                    bool namespaceMatches = MatchesGlobPattern(symbolNamespace, namespaceFilter);

                    if (!namespaceMatches)
                        continue;
                }

                var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
                if (location == null) continue;

                var lineSpan = location.GetLineSpan();

                allResults.Add(new
                {
                    name = symbol.Name,
                    fullyQualifiedName = symbol.ToDisplayString(),
                    kind = symbol.Kind.ToString(),
                    containingType = symbol.ContainingType?.ToDisplayString(),
                    containingNamespace = symbol.ContainingNamespace?.ToDisplayString(),
                    location = new
                    {
                        filePath = FormatPath(lineSpan.Path),
                        line = lineSpan.StartLinePosition.Line,
                        column = lineSpan.StartLinePosition.Character
                    }
                });
            }
        }

        // Apply pagination. totalCount is the true unbounded match count;
        // hasMore is derived from offset + page size.
        var totalCount = allResults.Count;
        var paginatedResults = allResults.Skip(offset).Take(maxResults).ToList();
        var hasMore = offset + paginatedResults.Count < totalCount;

        return CreateSuccessResponse(
            data: new
            {
                query,
                matchMode = mode,
                offset,
                hasMore,
                results = paginatedResults,
                pagination = new
                {
                    nextOffset = hasMore ? offset + paginatedResults.Count : (int?)null
                }
            },
            suggestedNextTools: new[]
            {
                "get_symbol_info at a result location for detailed info",
                "get_type_members for type results",
                "find_references to see all usages"
            },
            totalCount: totalCount,
            returnedCount: paginatedResults.Count
        );
    }

    // Maps a reference node to one of: write, invocation, cast, typeof, nameof, attribute, read.
    private static string ClassifyReferenceKind(SyntaxNode? node)
    {
        if (node == null) return "read";

        if (IsWriteContext(node)) return "write";

        for (var current = node; current != null; current = current.Parent)
        {
            switch (current)
            {
                case AttributeSyntax:
                    return "attribute";
                case TypeOfExpressionSyntax:
                    return "typeof";
                case CastExpressionSyntax cast:
                    // The reference is to the target type of an explicit cast: `(SomeType)x`.
                    // Only classify as "cast" when our node IS the type being cast to;
                    // if our node is the inner expression `x`, fall through to other kinds.
                    return cast.Type == node || cast.Type.Span.Contains(node.Span)
                        ? "cast"
                        : "read";
                case InvocationExpressionSyntax inv:
                    if (inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof")
                        return "nameof";
                    // "invocation" only when this node IS the invoked target — the whole
                    // call expression, or the member name of `receiver.Member(...)`. The
                    // receiver of a call (a read of it) and argument sub-expressions are not
                    // invocations, even though they sit spatially inside the call.
                    return inv.Expression == node
                        || (inv.Expression is MemberAccessExpressionSyntax ma && ma.Name == node)
                        ? "invocation"
                        : "read";
            }
        }

        return "read";
    }

    // True when `node` is the target of an assignment, ++/-- operator, or out/ref argument.
    private static bool IsWriteContext(SyntaxNode node)
    {
        // Walk up across MemberAccess / ElementAccess / parens so `obj.Field = x` still
        // classifies `Field` as a write.
        var current = node;
        while (current.Parent is MemberAccessExpressionSyntax mae && mae.Name == current
            || current.Parent is ElementAccessExpressionSyntax eae && eae.Expression == current
            || current.Parent is ParenthesizedExpressionSyntax)
        {
            current = current.Parent;
        }

        return current.Parent switch
        {
            AssignmentExpressionSyntax assign when assign.Left == current => true,
            PrefixUnaryExpressionSyntax pre when
                pre.IsKind(SyntaxKind.PreIncrementExpression) ||
                pre.IsKind(SyntaxKind.PreDecrementExpression) => true,
            PostfixUnaryExpressionSyntax post when
                post.IsKind(SyntaxKind.PostIncrementExpression) ||
                post.IsKind(SyntaxKind.PostDecrementExpression) => true,
            ArgumentSyntax arg when
                arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword) ||
                arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) => true,
            _ => false
        };
    }

}
