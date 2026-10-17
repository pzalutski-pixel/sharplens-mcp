using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace SharpLensMcp;

public partial class RoslynService
{
    // The reference kinds find_references classifies (ClassifyReferenceKind) and its kind
    // filter accepts.
    private static readonly IReadOnlyList<string> ReferenceKinds =
        new[] { "write", "invocation", "cast", "typeof", "nameof", "attribute", "read" };

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

    // Every reference to the symbol at a position, once per site (FindReferenceSitesAsync),
    // in the canonical location order, optionally filtered by reference kind and capped at
    // maxResults.
    public async Task<object> FindReferencesAsync(
        string filePath, int line, int column, int? maxResults = null, string? kindFilter = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);
        if (ParameterValidation.Choice("kind", kindFilter, ReferenceKinds, out var filterKind) is { } invalidKind)
            return CreateErrorResponse(invalidKind);

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        var sites = await FindReferenceSitesAsync(symbol, cancellationToken);
        var matching = filterKind == null ? sites : sites.Where(s => s.Item.Kind == filterKind).ToList();
        var references = matching.Take(maxResults ?? 100).Select(FormatReferenceSite).ToList();

        // totalReferencesAfterFilter is reported only when a filter is set; without one it
        // equals totalReferences.
        object data = filterKind != null
            ? new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                kindFilter = filterKind,
                totalReferences = sites.Count,
                totalReferencesAfterFilter = matching.Count,
                references
            }
            : new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                totalReferences = sites.Count,
                references
            };

        return CreateSuccessResponse(
            data,
            ResultCounts.Capped(matching.Count, references.Count),
            suggestedNextTools: new[]
            {
                $"get_symbol_info to get details about {symbol.Name}",
                $"find_callers to see methods that call {symbol.Name}",
                symbol is INamedTypeSymbol ? $"get_type_members for {symbol.Name}" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    // The definition of the symbol at a position: definition is the part that carries the
    // body of a partial member (else its earliest part), and locations every declaring part
    // in path order, each with its start and end.
    public async Task<object> GoToDefinitionAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        var definitionLocation = SymbolDisplayLocation(symbol);
        if (definitionLocation == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Symbol definition not in source",
                hint: "Symbol is defined in metadata (external library). Use get_type_members to explore its API.",
                context: new { symbolName = symbol.Name, symbolKind = symbol.Kind.ToString(), fullyQualifiedName = symbol.ToDisplayString() }
            );
        }

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
                definition = FormatDefinitionLocation(definitionLocation),
                locations = SymbolParts.SourceLocations(symbol).Select(FormatDefinitionLocation).ToList()
            },
            suggestedNextTools: new[]
            {
                $"find_references to see all usages of {symbol.Name}",
                $"get_symbol_info for more details about {symbol.Name}",
                symbol is INamedTypeSymbol ? $"get_type_members for {symbol.Name}" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    private object FormatDefinitionLocation(Location location)
    {
        var lineSpan = location.GetLineSpan();
        return new
        {
            filePath = FormatPath(lineSpan.Path),
            line = lineSpan.StartLinePosition.Line,
            column = lineSpan.StartLinePosition.Character,
            endLine = lineSpan.EndLinePosition.Line,
            endColumn = lineSpan.EndLinePosition.Character
        };
    }

    // The implementations of the type at a position (FindImplementingTypesAsync): for an
    // interface every class and struct implementing it, for a class every class deriving
    // from it, each once, in canonical order, plus implementers declared only in generator
    // output.
    public async Task<object> FindImplementationsAsync(
        string filePath, int line, int column, int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);
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

        var implementations = await FindImplementingTypesAsync(typeSymbol, cancellationToken);

        // The generated scan is authoritative for implementers declared in generator output:
        // a SymbolFinder implementer the scan reports is flagged, and the scan adds the ones
        // SymbolFinder did not return.
        var generatedImpls = await ScanGeneratedImplementationsAsync(typeSymbol, cancellationToken);
        var generatedNames = new HashSet<string>(generatedImpls.Select(h => h.Name), StringComparer.Ordinal);
        var entries = implementations.Select(m =>
        {
            var impl = m.Item;
            var entry = new Dictionary<string, object?>
            {
                ["name"] = impl.ToDisplayString(),
                ["kind"] = GetTypeKindString(impl),
                ["isAbstract"] = impl.IsAbstract,
                ["containingNamespace"] = impl.ContainingNamespace?.ToDisplayString(),
                ["locations"] = SymbolParts.SourceLocations(impl).Select(FormatLocation).ToList()
            };
            if (generatedNames.Contains(impl.ToDisplayString())) entry["inGeneratedCode"] = true;
            return (Name: impl.ToDisplayString(), Entry: WithTargetFrameworks(entry, m.TargetFrameworks));
        }).ToList();

        var seen = new HashSet<string>(entries.Select(e => e.Name), StringComparer.Ordinal);
        foreach (var hit in generatedImpls)
        {
            if (!seen.Add(hit.Name)) continue;
            entries.Add((hit.Name, new Dictionary<string, object?>
            {
                ["name"] = hit.Name,
                ["kind"] = hit.Kind,
                ["containingNamespace"] = hit.ContainingNamespace,
                ["locations"] = new[] { new { filePath = FormatPath(hit.HintPath), line = hit.Line, column = hit.Column } },
                ["inGeneratedCode"] = true
            }));
        }
        var ordered = entries.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => e.Entry).ToList();
        var page = ordered.Take(maxResultsToReturn).ToList();

        return CreateSuccessResponse(
            data: new
            {
                baseType = typeSymbol.ToDisplayString(),
                totalImplementations = ordered.Count,
                implementations = page
            },
            ResultCounts.Capped(ordered.Count, page.Count),
            suggestedNextTools: new[]
            {
                $"get_type_members for each implementation to see their members",
                $"get_type_hierarchy for {typeSymbol.Name} to see full inheritance tree"
            }
        );
    }

    // The hierarchy of the type at a position: its base classes (System.Object omitted), all
    // its interfaces, and the types directly below it (FindDerivedTypesAsync, not
    // transitive). maxDerivedTypes caps derivedTypes alone, and the counts describe
    // derivedTypes; baseTypes and interfaces are always complete.
    public async Task<object> GetTypeHierarchyAsync(
        string filePath, int line, int column, int? maxDerivedTypes = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxDerivedTypes", maxDerivedTypes, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);
        var maxDerivedToReturn = maxDerivedTypes ?? 50;

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

        var baseTypes = new List<object>();
        for (var currentBase = typeSymbol.BaseType;
             currentBase != null && currentBase.SpecialType != SpecialType.System_Object;
             currentBase = currentBase.BaseType)
        {
            baseTypes.Add(FormatTypeInfo(currentBase));
        }

        var interfaces = typeSymbol.AllInterfaces.Select(i => FormatTypeInfo(i)).ToList();
        var derived = await FindDerivedTypesAsync(typeSymbol, transitive: false, cancellationToken);
        var derivedList = derived.Take(maxDerivedToReturn).Select(d => FormatTypeInfo(d.Item, d.TargetFrameworks)).ToList();

        return CreateSuccessResponse(
            data: new
            {
                typeName = typeSymbol.ToDisplayString(),
                baseTypes,
                interfaces,
                totalInterfaces = interfaces.Count,
                derivedTypes = derivedList,
                totalDerivedTypes = derived.Count
            },
            ResultCounts.Capped(derived.Count, derivedList.Count),
            suggestedNextTools: new[]
            {
                $"get_type_members for {typeSymbol.Name} to see all members",
                "get_derived_types by name for transitive derived types",
                "find_implementations for interface implementations"
            }
        );
    }

    // Symbols whose name matches the query, labeled and filtered by the symbol-kind
    // vocabulary (SymbolKindVocabulary), each once — a multi-targeted declaration with the
    // target frameworks it occurred in, a namespace once however many projects declare it —
    // in the canonical symbol order, paged by offset and maxResults.
    public async Task<object> SearchSymbolsAsync(
        string query, string? kind, int maxResults, string? namespaceFilter = null, int offset = 0,
        string matchMode = "glob",
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Choice("matchMode", matchMode, new[] { "glob", "fuzzy" }, out var mode) is { } invalidMode)
            return CreateErrorResponse(invalidMode);
        if (ParameterValidation.Choice("kind", kind, SymbolKindVocabulary.AllKinds, out var kindFilter) is { } invalidKind)
            return CreateErrorResponse(invalidKind);
        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);
        if (ParameterValidation.Offset("offset", offset) is { } invalidOffset)
            return CreateErrorResponse(invalidOffset);

        var isGlobPattern = query.Contains('*') || query.Contains('?');
        var found = new List<(ISymbol Symbol, string Kind, string? TargetFramework)>();
        foreach (var project in GetLogicalProjects().Projects.SelectMany(p => p.Projects))
        {
            IEnumerable<ISymbol> symbols;
            if (mode == "fuzzy")
            {
                // Roslyn's IDE pattern matcher: camel-hump ("RSvc" finds RoslynService),
                // substring, and fuzzy matching — the search shape agents actually attempt.
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

            var targetFramework = LogicalProject.TargetFrameworkOf(project);
            foreach (var symbol in symbols)
            {
                if (symbol is INamespaceSymbol { IsGlobalNamespace: true }) continue;
                if (SymbolKindVocabulary.Label(symbol) is not { } label) continue;
                if (kindFilter != null && !SymbolKindVocabulary.Selects(kindFilter, label)) continue;
                if (!string.IsNullOrEmpty(namespaceFilter)
                    && !MatchesGlobPattern(symbol.ContainingNamespace?.ToDisplayString() ?? "", namespaceFilter)) continue;
                if (!symbol.Locations.Any(loc => loc.IsInSource)) continue;
                found.Add((symbol, label, targetFramework));
            }
        }

        // A namespace is one result however many projects declare it; every other symbol
        // merges by the solution's identity rule.
        var merged = TargetFrameworkDeduplication.By(
            found,
            f => f.Symbol is INamespaceSymbol ns
                ? $"N:{ns.ToDisplayString()}"
                : CrossCompilationSymbol.SolutionKey(f.Symbol) ?? $"{f.Symbol.ToDisplayString()}@{ReportedLocation(f.Symbol.Locations.FirstOrDefault(l => l.IsInSource))}",
            StringComparer.Ordinal,
            f => f.TargetFramework);
        var ordered = CanonicalOrder.BySymbol(merged, m => m.Item.Symbol);
        var page = Paging.Slice(ordered, offset, maxResults);
        var results = page.Items.Select(m =>
        {
            var symbol = m.Item.Symbol;
            return WithTargetFrameworks(new Dictionary<string, object?>
            {
                ["name"] = symbol.Name,
                ["fullyQualifiedName"] = symbol.ToDisplayString(),
                ["kind"] = m.Item.Kind,
                ["containingType"] = symbol.ContainingType?.ToDisplayString(),
                ["containingNamespace"] = symbol.ContainingNamespace?.ToDisplayString(),
                ["location"] = GetSymbolLocation(symbol)
            }, m.TargetFrameworks);
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                query,
                matchMode = mode,
                offset,
                hasMore = page.HasMore,
                results,
                pagination = new
                {
                    nextOffset = page.NextOffset
                }
            },
            ResultCounts.Page(page),
            suggestedNextTools: new[]
            {
                "get_symbol_info at a result location for detailed info",
                "get_type_members for type results",
                "find_references to see all usages"
            }
        );
    }

    // Maps a reference node to one of: write, invocation, cast, typeof, nameof, attribute, read.
    // A reference's span can be shared by the argument holding it (FindNode returns the
    // outermost node of a span, so `M(x)` and `(x, y)` yield the argument); the reference
    // is the argument's expression.
    private static string ClassifyReferenceKind(SyntaxNode? node)
    {
        if (node == null) return "read";
        if (node is ArgumentSyntax argument) node = argument.Expression;

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
                    // call expression, or the member name of `receiver.Member(...)` or of a
                    // null-conditional `receiver?.Member(...)`. The receiver of a call (a read
                    // of it) and argument sub-expressions are not invocations, even though
                    // they sit spatially inside the call.
                    return inv.Expression == node
                        || (inv.Expression is MemberAccessExpressionSyntax ma && ma.Name == node)
                        || (inv.Expression is MemberBindingExpressionSyntax mb && mb.Name == node)
                        ? "invocation"
                        : "read";
            }
        }

        return "read";
    }

    // True when `node` is assigned to: the target of an assignment (simple, compound,
    // null-conditional), an element of a deconstruction's left-hand tuple or of a foreach
    // deconstruction, the operand of ++/--, or a ref/out argument. An indexer reference (its
    // bracketed argument list) is written when its element access is. The receiver of an
    // element access (`arr` in `arr[0] = x`) is only read.
    private static bool IsWriteContext(SyntaxNode node)
    {
        var current = node;
        if (current is BracketedArgumentListSyntax { Parent: ElementAccessExpressionSyntax indexed })
        {
            current = indexed;
        }

        // The member a qualified (`obj.Field`), null-conditional (`obj?.Field`) or
        // parenthesized expression ends in is what that expression writes.
        while (current.Parent is MemberAccessExpressionSyntax mae && mae.Name == current
            || current.Parent is MemberBindingExpressionSyntax mbe && mbe.Name == current
            || current.Parent is ParenthesizedExpressionSyntax)
        {
            current = current.Parent;
        }

        // A tuple element, at any nesting depth, is written when its tuple is.
        while (current.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax tuple })
        {
            current = tuple;
        }

        return current.Parent switch
        {
            AssignmentExpressionSyntax assign when assign.Left == current => true,
            ForEachVariableStatementSyntax forEach when forEach.Variable == current => true,
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
