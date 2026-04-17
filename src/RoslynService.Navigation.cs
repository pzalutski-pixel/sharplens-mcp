using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

public partial class RoslynService
{
    public async Task<object> GetSymbolInfoAsync(string filePath, int line, int column)
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

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics",
                context: new { filePath, line, column }
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

        var position = GetPosition(syntaxTree, line, column);
        var token = syntaxTree.GetRoot().FindToken(position);
        var node = token.Parent;

        if (node == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column }
            );
        }

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol;

        if (symbol == null)
        {
            // Try getting the declared symbol if we're on a declaration
            symbol = semanticModel.GetDeclaredSymbol(node);
        }

        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column }
            );
        }

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

    public async Task<object> FindReferencesAsync(string filePath, int line, int column, int? maxResults = null)
    {
        EnsureSolutionLoaded();

        var maxResultsToReturn = maxResults ?? 100;

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

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics",
                context: new { filePath, line, column }
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

        var position = GetPosition(syntaxTree, line, column);
        var token = syntaxTree.GetRoot().FindToken(position);
        var node = token.Parent;

        if (node == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column }
            );
        }

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(node);

        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column }
            );
        }

        var references = await SymbolFinder.FindReferencesAsync(symbol, _solution!);
        var allLocations = references
            .SelectMany(r => r.Locations)
            .Where(loc => loc.Location.IsInSource)
            .ToList();

        var totalReferences = allLocations.Count;
        var referenceList = new List<object>();

        foreach (var loc in allLocations)
        {
            if (referenceList.Count >= maxResultsToReturn)
                break;

            var refDocument = _solution!.GetDocument(loc.Document.Id);
            if (refDocument == null) continue;

            var refTree = await refDocument.GetSyntaxTreeAsync();
            if (refTree == null) continue;

            var refSpan = loc.Location.SourceSpan;
            var lineSpan = refTree.GetLineSpan(refSpan);
            var text = refTree.GetText();
            var lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

            referenceList.Add(new
            {
                filePath = FormatPath(refDocument.FilePath),
                line = lineSpan.StartLinePosition.Line,
                column = lineSpan.StartLinePosition.Character,
                lineText,
                kind = "read"
            });
        }

        return CreateSuccessResponse(
            data: new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                totalReferences,
                references = referenceList
            },
            suggestedNextTools: new[]
            {
                $"get_symbol_info to get details about {symbol.Name}",
                $"find_callers to see methods that call {symbol.Name}",
                symbol is INamedTypeSymbol ? $"get_type_members for {symbol.Name}" : null
            }.Where(s => s != null).ToArray()!,
            totalCount: totalReferences,
            returnedCount: referenceList.Count
        );
    }

    public async Task<object> GoToDefinitionAsync(string filePath, int line, int column)
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

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics",
                context: new { filePath, line, column }
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

        var position = GetPosition(syntaxTree, line, column);
        var token = syntaxTree.GetRoot().FindToken(position);
        var node = token.Parent;

        if (node == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Ensure cursor is on a symbol name (class, method, variable, etc.)",
                context: new { filePath, line, column }
            );
        }

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(node);

        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Position may be on whitespace or non-symbol token. Try positioning on an identifier.",
                context: new { filePath, line, column, tokenText = token.Text, nodeKind = node.Kind().ToString() }
            );
        }

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

    public async Task<object> FindImplementationsAsync(string filePath, int line, int column, int? maxResults = null)
    {
        EnsureSolutionLoaded();

        var maxResultsToReturn = maxResults ?? 50;

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

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics",
                context: new { filePath, line, column }
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

        var position = GetPosition(syntaxTree, line, column);
        var token = syntaxTree.GetRoot().FindToken(position);
        var node = token.Parent;

        if (node == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column }
            );
        }

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(node);

        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column }
            );
        }

        if (symbol is not INamedTypeSymbol typeSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "Not a type symbol",
                hint: "This tool requires a type symbol (interface, class, or abstract class). Use get_derived_types with a type name instead.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        var implementations = await SymbolFinder.FindImplementationsAsync(typeSymbol, _solution!);
        var allImplementations = implementations.ToList();
        var totalImplementations = allImplementations.Count;

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

            implementationList.Add(new
            {
                name = impl.ToDisplayString(),
                kind = impl.TypeKind.ToString(),
                containingNamespace = impl.ContainingNamespace?.ToDisplayString(),
                locations
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

    public async Task<object> GetTypeHierarchyAsync(string filePath, int line, int column, int? maxDerivedTypes = null)
    {
        EnsureSolutionLoaded();

        var maxDerivedToReturn = maxDerivedTypes ?? 50; // Default to 50

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

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics",
                context: new { filePath, line, column }
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

        var position = GetPosition(syntaxTree, line, column);
        var token = syntaxTree.GetRoot().FindToken(position);
        var node = token.Parent;

        if (node == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column }
            );
        }

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(node);

        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Try adjusting the line/column or use search_symbols to find symbols by name",
                context: new { filePath, line, column }
            );
        }

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

        // Get interfaces
        var interfaces = typeSymbol.AllInterfaces
            .Select(i => FormatTypeInfo(i))
            .ToList();

        // Get derived types
        var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(typeSymbol, _solution!, transitive: false);
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
                derivedTypes = derivedList
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

    public async Task<object> SearchSymbolsAsync(string query, string? kind, int maxResults, string? namespaceFilter = null, int offset = 0)
    {
        EnsureSolutionLoaded();

        var allResults = new List<object>();

        // Check if query contains glob patterns
        bool isGlobPattern = query.Contains('*') || query.Contains('?');

        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project);
            if (compilation == null) continue;

            var symbols = compilation.GetSymbolsWithName(
                name => isGlobPattern ? MatchesGlobPattern(name, query) : name.Contains(query, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.All);

            foreach (var symbol in symbols)
            {
                // Filter by kind
                if (!string.IsNullOrEmpty(kind))
                {
                    bool matches = false;

                    // For type symbols (Class, Interface, Struct, Enum), check TypeKind
                    if (symbol is INamedTypeSymbol namedType)
                    {
                        matches = namedType.TypeKind.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase);
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

                // Continue collecting until we have offset + maxResults (to handle pagination)
                if (allResults.Count >= offset + maxResults + 100) // +100 buffer for accurate totalCount estimation
                    break;
            }

            if (allResults.Count >= offset + maxResults + 100)
                break;
        }

        // Apply pagination
        var totalCount = allResults.Count;
        var paginatedResults = allResults.Skip(offset).Take(maxResults).ToList();
        var hasMore = offset + paginatedResults.Count < totalCount;

        return CreateSuccessResponse(
            data: new
            {
                query,
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

}
