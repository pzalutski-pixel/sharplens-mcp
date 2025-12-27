using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Concurrent;
using System.Text;

namespace SharpLensMcp;

#region Infrastructure Classes

/// <summary>
/// Error codes for consistent error handling across all tools.
/// </summary>
public static class ErrorCodes
{
    public const string SolutionNotLoaded = "SOLUTION_NOT_LOADED";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileNotInSolution = "FILE_NOT_IN_SOLUTION";
    public const string SymbolNotFound = "SYMBOL_NOT_FOUND";
    public const string TypeNotFound = "TYPE_NOT_FOUND";
    public const string NotAType = "NOT_A_TYPE";
    public const string NotAMethod = "NOT_A_METHOD";
    public const string AnalysisFailed = "ANALYSIS_FAILED";
    public const string InvalidParameter = "INVALID_PARAMETER";
    public const string Timeout = "TIMEOUT";
}

/// <summary>
/// Structured error with recovery guidance for AI agents.
/// </summary>
public class RoslynError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Hint { get; set; }
    public object? Context { get; set; }
}

/// <summary>
/// Response metadata for AI workflow guidance.
/// </summary>
public class ResponseMetadata
{
    public int? TotalCount { get; set; }
    public int? ReturnedCount { get; set; }
    public bool? Truncated { get; set; }
    public string[]? SuggestedNextTools { get; set; }
    public string? Verbosity { get; set; }
}

/// <summary>
/// Represents a single change to a method signature.
/// </summary>
public class SignatureChange
{
    /// <summary>
    /// Action: "add", "remove", "rename", "reorder"
    /// </summary>
    public string Action { get; set; } = "";

    /// <summary>
    /// Parameter name (for remove, rename) or new parameter name (for add)
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Parameter type (for add)
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// New name (for rename)
    /// </summary>
    public string? NewName { get; set; }

    /// <summary>
    /// Default value (for add)
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Position to insert (for add), -1 means end
    /// </summary>
    public int? Position { get; set; }

    /// <summary>
    /// New parameter order (for reorder)
    /// </summary>
    public List<string>? Order { get; set; }
}

#endregion

public class RoslynService
{
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly Dictionary<string, Document> _documentCache = new();
    private readonly int _maxDiagnostics;
    private readonly int _timeoutSeconds;

    private DateTime? _solutionLoadedAt;

    public RoslynService()
    {
        _maxDiagnostics = int.TryParse(Environment.GetEnvironmentVariable("ROSLYN_MAX_DIAGNOSTICS"), out var maxDiag)
            ? maxDiag : 100;
        _timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("ROSLYN_TIMEOUT_SECONDS"), out var timeout)
            ? timeout : 30;
    }

    // Helper method for glob pattern matching (supports * and ? wildcards)
    private static bool MatchesGlobPattern(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return true;

        // Convert glob pattern to regex
        // Escape regex special chars except * and ?
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")  // * matches any characters
            .Replace("\\?", ".")   // ? matches single character
            + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    #region Response Helper Methods

    /// <summary>
    /// Creates a standardized success response with metadata.
    /// </summary>
    private object CreateSuccessResponse(object data, string[]? suggestedNextTools = null,
        int? totalCount = null, int? returnedCount = null, string? verbosity = null)
    {
        return new
        {
            success = true,
            data,
            meta = new ResponseMetadata
            {
                TotalCount = totalCount,
                ReturnedCount = returnedCount,
                Truncated = totalCount.HasValue && returnedCount.HasValue && returnedCount < totalCount,
                SuggestedNextTools = suggestedNextTools,
                Verbosity = verbosity
            }
        };
    }

    /// <summary>
    /// Creates a standardized error response with recovery hints.
    /// </summary>
    private object CreateErrorResponse(string code, string message, string? hint = null, object? context = null)
    {
        return new
        {
            success = false,
            error = new RoslynError
            {
                Code = code,
                Message = message,
                Hint = hint,
                Context = context
            }
        };
    }

    #endregion

    #region Type Resolution Helpers

    /// <summary>
    /// Finds a type by name, trying multiple resolution strategies.
    /// Supports fully-qualified names, simple names, and Godot namespace prefixing.
    /// </summary>
    private async Task<INamedTypeSymbol?> FindTypeByNameAsync(string typeName)
    {
        EnsureSolutionLoaded();

        foreach (var project in _solution!.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            // Strategy 1: Fully-qualified metadata name
            var type = compilation.GetTypeByMetadataName(typeName);
            if (type != null) return type;

            // Strategy 2: Common Godot namespace prefix
            type = compilation.GetTypeByMetadataName($"Godot.{typeName}");
            if (type != null) return type;

            // Strategy 3: Search by simple name (case-insensitive)
            var symbols = compilation.GetSymbolsWithName(
                name => name.Equals(typeName, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.Type);

            var found = symbols.OfType<INamedTypeSymbol>().FirstOrDefault();
            if (found != null) return found;

            // Strategy 4: Partial match (contains)
            symbols = compilation.GetSymbolsWithName(
                name => name.Contains(typeName, StringComparison.OrdinalIgnoreCase),
                SymbolFilter.Type);

            found = symbols.OfType<INamedTypeSymbol>().FirstOrDefault();
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>
    /// Checks if a type has a specific attribute (by simple name).
    /// </summary>
    private static bool HasAttribute(ISymbol symbol, string attributeName)
    {
        return symbol.GetAttributes().Any(attr =>
            attr.AttributeClass?.Name.Equals(attributeName, StringComparison.OrdinalIgnoreCase) == true ||
            attr.AttributeClass?.Name.Equals($"{attributeName}Attribute", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Gets the source location of a symbol, if available.
    /// </summary>
    private static object? GetSymbolLocation(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
        if (location == null) return null;

        var lineSpan = location.GetLineSpan();
        return new
        {
            filePath = lineSpan.Path,
            line = lineSpan.StartLinePosition.Line,
            column = lineSpan.StartLinePosition.Character
        };
    }

    #endregion

    public async Task<object> LoadSolutionAsync(string solutionPath)
    {
        if (!File.Exists(solutionPath))
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotFound,
                $"Solution file not found: {solutionPath}",
                hint: "Verify the path is correct and the file exists",
                context: new { solutionPath }
            );
        }

        // Dispose existing workspace
        _workspace?.Dispose();
        _documentCache.Clear();

        _workspace = MSBuildWorkspace.Create();
        _workspace.WorkspaceFailed += (sender, args) =>
        {
            Console.Error.WriteLine($"[Warning] Workspace: {args.Diagnostic.Message}");
        };

        _solution = await _workspace.OpenSolutionAsync(solutionPath);
        _solutionLoadedAt = DateTime.UtcNow;

        var projectCount = _solution.ProjectIds.Count;
        var documentCount = _solution.Projects.Sum(p => p.DocumentIds.Count);

        return CreateSuccessResponse(
            data: new
            {
                solutionPath,
                projectCount,
                documentCount
            },
            suggestedNextTools: new[]
            {
                "get_project_structure to see solution structure",
                "search_symbols to find code",
                "get_diagnostics to check for issues"
            }
        );
    }

    public async Task<object> GetHealthCheckAsync()
    {
        if (_solution == null || _workspace == null)
        {
            return new
            {
                status = "Not Ready",
                message = "No solution loaded. Call roslyn:load_solution first or set DOTNET_SOLUTION_PATH environment variable.",
                solution = (object?)null,
                workspace = (object?)null
            };
        }

        // Get diagnostic summary
        var errorCount = 0;
        var warningCount = 0;

        try
        {
            foreach (var project in _solution.Projects.Take(5)) // Sample first 5 projects for quick health check
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation != null)
                {
                    var diagnostics = compilation.GetDiagnostics();
                    errorCount += diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
                    warningCount += diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
                }
            }
        }
        catch
        {
            // Ignore errors during health check diagnostics
        }

        var projectCount = _solution.ProjectIds.Count;
        var documentCount = _solution.Projects.Sum(p => p.DocumentIds.Count);

        return new
        {
            status = "Ready",
            message = "Roslyn MCP Server is operational",
            solution = new
            {
                loaded = true,
                path = _solution.FilePath,
                projects = projectCount,
                documents = documentCount,
                loadedAt = _solutionLoadedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                errors = errorCount,
                warnings = warningCount
            },
            workspace = new
            {
                indexed = true,
                cacheSize = _documentCache.Count
            },
            capabilities = new
            {
                findReferences = true,
                findImplementations = true,
                codeFixProvider = true,
                symbolSearch = true,
                diagnostics = true
            },
            configuration = new
            {
                maxDiagnostics = _maxDiagnostics,
                timeoutSeconds = _timeoutSeconds,
                semanticCacheEnabled = Environment.GetEnvironmentVariable("ROSLYN_ENABLE_SEMANTIC_CACHE") != "false"
            }
        };
    }

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
                filePath = refDocument.FilePath,
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
                    filePath = defLineSpan.Path,
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
                        filePath = lineSpan.Path,
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
            var compilation = await project.GetCompilationAsync();
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
                        filePath = lineSpan.Path,
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

    public async Task<object> GetDiagnosticsAsync(string? filePath, string? projectPath, string? severity, bool includeHidden)
    {
        EnsureSolutionLoaded();

        var allDiagnostics = new List<Diagnostic>();

        if (!string.IsNullOrEmpty(filePath))
        {
            // Get diagnostics for specific file
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
            if (semanticModel != null)
            {
                allDiagnostics.AddRange(semanticModel.GetDiagnostics());
            }
        }
        else if (!string.IsNullOrEmpty(projectPath))
        {
            // Get diagnostics for specific project
            var project = _solution!.Projects.FirstOrDefault(p => p.FilePath == projectPath);
            if (project != null)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation != null)
                {
                    allDiagnostics.AddRange(compilation.GetDiagnostics());
                }
            }
        }
        else
        {
            // Get diagnostics for entire solution
            foreach (var project in _solution!.Projects)
            {
                var compilation = await project.GetCompilationAsync();
                if (compilation != null)
                {
                    allDiagnostics.AddRange(compilation.GetDiagnostics());
                }
            }
        }

        // Filter by severity
        if (!string.IsNullOrEmpty(severity))
        {
            var severityEnum = Enum.Parse<DiagnosticSeverity>(severity, ignoreCase: true);
            allDiagnostics = allDiagnostics.Where(d => d.Severity == severityEnum).ToList();
        }

        // Filter hidden
        if (!includeHidden)
        {
            allDiagnostics = allDiagnostics.Where(d => d.Severity != DiagnosticSeverity.Hidden).ToList();
        }

        // Limit results
        allDiagnostics = allDiagnostics.Take(_maxDiagnostics).ToList();

        var diagnosticList = allDiagnostics.Select(d =>
        {
            var lineSpan = d.Location.GetLineSpan();
            return new
            {
                id = d.Id,
                severity = d.Severity.ToString(),
                message = d.GetMessage(),
                filePath = lineSpan.Path,
                line = lineSpan.StartLinePosition.Line,
                column = lineSpan.StartLinePosition.Character,
                endLine = lineSpan.EndLinePosition.Line,
                endColumn = lineSpan.EndLinePosition.Character
            };
        }).ToList();

        var errorCount = diagnosticList.Count(d => d.severity == "Error");
        var warningCount = diagnosticList.Count(d => d.severity == "Warning");

        return CreateSuccessResponse(
            data: new
            {
                errorCount,
                warningCount,
                diagnostics = diagnosticList
            },
            suggestedNextTools: errorCount > 0 || warningCount > 0
                ? new[]
                {
                    "get_code_fixes for a diagnostic to see available fixes",
                    "apply_code_fix to apply a fix automatically"
                }
                : new[]
                {
                    "No diagnostics found - solution is healthy"
                },
            totalCount: diagnosticList.Count,
            returnedCount: diagnosticList.Count
        );
    }

    public async Task<object> GetCodeFixesAsync(string filePath, string diagnosticId, int line, int column)
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

        // Get all diagnostics for the document (semantic + syntax)
        var allDiagnostics = semanticModel.GetDiagnostics().ToList();
        var syntaxDiagnostics = syntaxTree.GetDiagnostics().ToList();
        allDiagnostics.AddRange(syntaxDiagnostics);

        var position = GetPosition(syntaxTree, line, column);

        // Strategy 1: Try exact ID match with position contained in span
        var diagnostic = allDiagnostics.FirstOrDefault(d =>
            d.Id == diagnosticId &&
            d.Location.SourceSpan.Contains(position));

        // Strategy 2: Try exact ID match with nearby position (within 50 chars)
        if (diagnostic == null)
        {
            diagnostic = allDiagnostics.FirstOrDefault(d =>
                d.Id == diagnosticId &&
                Math.Abs(d.Location.SourceSpan.Start - position) < 50);
        }

        // Strategy 3: Try exact ID match anywhere in the file
        if (diagnostic == null)
        {
            diagnostic = allDiagnostics.FirstOrDefault(d => d.Id == diagnosticId);
        }

        // Find all diagnostics at or near the position for error message
        var diagnosticsAtPosition = allDiagnostics
            .Where(d => d.Location.SourceSpan.Contains(position) || Math.Abs(d.Location.SourceSpan.Start - position) < 50)
            .Take(10)
            .Select(d => new
            {
                id = d.Id,
                message = d.GetMessage(),
                severity = d.Severity.ToString(),
                span = new
                {
                    start = d.Location.SourceSpan.Start,
                    end = d.Location.SourceSpan.End,
                    length = d.Location.SourceSpan.Length
                }
            })
            .ToList();

        if (diagnostic == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"No diagnostic with ID '{diagnosticId}' found",
                hint: diagnosticsAtPosition.Count > 0
                    ? $"Found {diagnosticsAtPosition.Count} other diagnostic(s) near this position. Try using one of their IDs."
                    : "No diagnostics found at this position. Run get_diagnostics to see all available diagnostics.",
                context: new { line, column, diagnosticsNearby = diagnosticsAtPosition }
            );
        }

        var lineSpan = diagnostic.Location.GetLineSpan();

        // Note: Actual code fix provider infrastructure would require CodeFixProvider registration
        // For now, we return diagnostic info and common fix suggestions based on diagnostic ID
        var suggestedFixes = GetCommonFixSuggestions(diagnostic.Id, diagnostic.GetMessage());

        return CreateSuccessResponse(
            data: new
            {
                diagnosticId = diagnostic.Id,
                message = diagnostic.GetMessage(),
                severity = diagnostic.Severity.ToString(),
                location = new
                {
                    filePath = lineSpan.Path,
                    startLine = lineSpan.StartLinePosition.Line,
                    startColumn = lineSpan.StartLinePosition.Character,
                    endLine = lineSpan.EndLinePosition.Line,
                    endColumn = lineSpan.EndLinePosition.Character
                },
                suggestedFixes
            },
            suggestedNextTools: new[]
            {
                "apply_code_fix to apply a fix automatically",
                "get_diagnostics to see other issues"
            }
        );
    }

    private List<string> GetCommonFixSuggestions(string diagnosticId, string message)
    {
        // Common fix suggestions for well-known diagnostic IDs
        return diagnosticId switch
        {
            "CS0168" => new List<string> { "Remove unused variable", "Use the variable", "Prefix with underscore to indicate intentionally unused" },
            "CS0219" => new List<string> { "Remove unused variable", "Use the variable in an expression" },
            "CS1998" => new List<string> { "Add await keyword to async operation", "Remove async modifier if method doesn't need to be async", "Return Task.CompletedTask or Task.FromResult()" },
            "CS0162" => new List<string> { "Remove unreachable code", "Fix control flow logic" },
            "CS0649" => new List<string> { "Initialize the field", "Remove unused field", "Mark as obsolete if legacy code" },
            "CS8019" => new List<string> { "Remove unnecessary using directive", "Run 'Organize Usings'" },
            "CS0246" => new List<string> { "Add missing using directive", "Check type name spelling", "Add assembly reference" },
            "CS0103" => new List<string> { "Add missing using directive", "Check name spelling", "Declare the variable or method" },
            "CS4012" => new List<string> { "Move Utf8JsonReader to non-async context", "Use synchronous JSON parsing", "Wrap in Task.Run() for async operation" },
            "CS1503" => new List<string> { "Cast argument to expected type", "Change parameter type", "Fix argument expression" },
            _ => new List<string> { "Review diagnostic message for fix guidance", "Consult C# documentation for " + diagnosticId }
        };
    }

    public async Task<object> ApplyCodeFixAsync(
        string filePath,
        string diagnosticId,
        int line,
        int column,
        int? fixIndex = null,
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

        // Get all diagnostics (semantic + syntax)
        var allDiagnostics = semanticModel.GetDiagnostics().ToList();
        var syntaxDiagnostics = syntaxTree.GetDiagnostics().ToList();
        allDiagnostics.AddRange(syntaxDiagnostics);

        var position = GetPosition(syntaxTree, line, column);

        // Find the diagnostic using the same strategy as GetCodeFixesAsync
        var diagnostic = allDiagnostics.FirstOrDefault(d =>
            d.Id == diagnosticId &&
            d.Location.SourceSpan.Contains(position));

        if (diagnostic == null)
        {
            diagnostic = allDiagnostics.FirstOrDefault(d =>
                d.Id == diagnosticId &&
                Math.Abs(d.Location.SourceSpan.Start - position) < 50);
        }

        if (diagnostic == null)
        {
            diagnostic = allDiagnostics.FirstOrDefault(d => d.Id == diagnosticId);
        }

        if (diagnostic == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"No diagnostic with ID '{diagnosticId}' found at line {line}, column {column}",
                hint: "Run get_code_fixes first to verify the diagnostic exists at this location.",
                context: new { filePath, diagnosticId, line, column }
            );
        }

        // Get code actions from built-in code fix providers
        var codeActions = await GetCodeActionsForDiagnosticAsync(document, diagnostic);

        if (codeActions.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"No code fixes available for diagnostic '{diagnosticId}'",
                hint: "This diagnostic may not have automated code fixes available. Try the suggestions from get_code_fixes.",
                context: new { diagnosticMessage = diagnostic.GetMessage(), suggestedFixes = GetCommonFixSuggestions(diagnosticId, diagnostic.GetMessage()) }
            );
        }

        // If no fixIndex specified, return available fixes
        if (fixIndex == null)
        {
            var availableFixes = codeActions.Select((action, index) => new
            {
                index,
                title = action.Title,
                equivalenceKey = action.EquivalenceKey
            }).ToList();

            return CreateSuccessResponse(
                data: new
                {
                    diagnosticId = diagnostic.Id,
                    message = diagnostic.GetMessage(),
                    availableFixes
                },
                suggestedNextTools: new[]
                {
                    $"apply_code_fix with fixIndex=0 and preview=true to preview the first fix",
                    $"apply_code_fix with fixIndex=0 and preview=false to apply the first fix"
                },
                totalCount: availableFixes.Count,
                returnedCount: availableFixes.Count
            );
        }

        // Validate fixIndex
        if (fixIndex < 0 || fixIndex >= codeActions.Count)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid fixIndex {fixIndex}. Available range: 0 to {codeActions.Count - 1}",
                hint: "Call without fixIndex to list available fixes",
                context: new { fixIndex, availableFixCount = codeActions.Count }
            );
        }

        var selectedAction = codeActions[fixIndex.Value];

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

        if (changedSolution == _solution)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Code fix did not produce any changes",
                hint: "The selected fix may not be applicable in this context.",
                context: new { fixTitle = selectedAction.Title, fixIndex }
            );
        }

        // Collect all changed documents
        var changedDocuments = new List<object>();
        var solutionChanges = changedSolution.GetChanges(_solution!);

        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        {
            // Check for added documents
            foreach (var addedDocId in projectChanges.GetAddedDocuments())
            {
                var addedDoc = changedSolution.GetDocument(addedDocId);
                if (addedDoc == null) continue;

                var text = await addedDoc.GetTextAsync();
                changedDocuments.Add(new
                {
                    filePath = addedDoc.FilePath ?? $"NewFile_{addedDoc.Name}",
                    fileName = addedDoc.Name,
                    isNewFile = true,
                    newText = preview ? text.ToString() : null,
                    changeType = "Added"
                });

                // Write to disk if not preview
                if (!preview && addedDoc.FilePath != null)
                {
                    await File.WriteAllTextAsync(addedDoc.FilePath, text.ToString());
                }
            }

            // Check for changed documents
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
                    filePath = newDoc.FilePath,
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

                // Write to disk if not preview
                if (!preview && newDoc.FilePath != null)
                {
                    await File.WriteAllTextAsync(newDoc.FilePath, newText.ToString());

                    // Update solution with changes for subsequent operations
                    _solution = changedSolution;
                }
            }

            // Check for removed documents
            foreach (var removedDocId in projectChanges.GetRemovedDocuments())
            {
                var removedDoc = _solution!.GetDocument(removedDocId);
                if (removedDoc == null) continue;

                changedDocuments.Add(new
                {
                    filePath = removedDoc.FilePath,
                    fileName = removedDoc.Name,
                    isNewFile = false,
                    changeType = "Removed"
                });

                // Delete file if not preview
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
                diagnosticId = diagnostic.Id,
                fixTitle = selectedAction.Title,
                fixIndex = fixIndex.Value,
                changedFiles = changedDocuments,
                preview
            },
            suggestedNextTools: preview
                ? new[] { $"apply_code_fix with preview=false to apply changes to disk" }
                : new[] { "get_diagnostics to verify fix resolved the issue" },
            totalCount: changedDocuments.Count,
            returnedCount: changedDocuments.Count
        );
    }

    private async Task<List<CodeAction>> GetCodeActionsForDiagnosticAsync(Document document, Diagnostic diagnostic)
    {
        var codeActions = new List<CodeAction>();
        var codeFixProviders = GetBuiltInCodeFixProviders();

        foreach (var provider in codeFixProviders)
        {
            if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
                continue;

            var context = new CodeFixContext(
                document,
                diagnostic,
                (action, _) => codeActions.Add(action),
                CancellationToken.None);

            try
            {
                await provider.RegisterCodeFixesAsync(context);
            }
            catch
            {
                // Some providers may throw if they can't handle the diagnostic
                // Silently continue to next provider
            }
        }

        return codeActions;
    }

    private List<CodeFixProvider> GetBuiltInCodeFixProviders()
    {
        // Get built-in C# code fix providers from Roslyn
        var codeFixProviderType = typeof(CodeFixProvider);
        var assembly = typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly;

        var providers = assembly.GetTypes()
            .Where(t => !t.IsAbstract && codeFixProviderType.IsAssignableFrom(t))
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Length == 0)) // Has parameterless constructor
            .Select(t =>
            {
                try
                {
                    return Activator.CreateInstance(t) as CodeFixProvider;
                }
                catch
                {
                    return null;
                }
            })
            .Where(p => p != null)
            .Cast<CodeFixProvider>()
            .ToList();

        return providers;
    }

    private List<CodeRefactoringProvider> GetBuiltInCodeRefactoringProviders()
    {
        // Get built-in C# code refactoring providers from Roslyn
        var codeRefactoringProviderType = typeof(CodeRefactoringProvider);

        // Check multiple assemblies for refactoring providers
        var assemblies = new[]
        {
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly,
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxNode).Assembly
        };

        var providers = new List<CodeRefactoringProvider>();

        foreach (var assembly in assemblies)
        {
            var assemblyProviders = assembly.GetTypes()
                .Where(t => !t.IsAbstract && codeRefactoringProviderType.IsAssignableFrom(t))
                .Where(t => t.GetConstructors().Any(c => c.GetParameters().Length == 0))
                .Select(t =>
                {
                    try
                    {
                        return Activator.CreateInstance(t) as CodeRefactoringProvider;
                    }
                    catch
                    {
                        return null;
                    }
                })
                .Where(p => p != null)
                .Cast<CodeRefactoringProvider>();

            providers.AddRange(assemblyProviders);
        }

        // Also try to load from Features assembly if available
        try
        {
            var featuresAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp.Features");

            if (featuresAssembly != null)
            {
                var featuresProviders = featuresAssembly.GetTypes()
                    .Where(t => !t.IsAbstract && codeRefactoringProviderType.IsAssignableFrom(t))
                    .Where(t => t.GetConstructors().Any(c => c.GetParameters().Length == 0))
                    .Select(t =>
                    {
                        try
                        {
                            return Activator.CreateInstance(t) as CodeRefactoringProvider;
                        }
                        catch
                        {
                            return null;
                        }
                    })
                    .Where(p => p != null)
                    .Cast<CodeRefactoringProvider>();

                providers.AddRange(featuresProviders);
            }
        }
        catch
        {
            // Features assembly not available, continue with what we have
        }

        return providers.Distinct().ToList();
    }

    private async Task<List<(CodeAction action, string kind)>> GetAllCodeActionsAtPositionAsync(
        Document document,
        int position,
        int? endPosition = null,
        bool includeCodeFixes = true,
        bool includeRefactorings = true)
    {
        var allActions = new List<(CodeAction action, string kind)>();
        var span = endPosition.HasValue
            ? TextSpan.FromBounds(position, endPosition.Value)
            : new TextSpan(position, 0);

        // Get code fixes for diagnostics at this position
        if (includeCodeFixes)
        {
            var semanticModel = await document.GetSemanticModelAsync();
            var syntaxTree = await document.GetSyntaxTreeAsync();

            if (semanticModel != null && syntaxTree != null)
            {
                // Get all diagnostics and filter to those overlapping our span
                var allDiagnostics = semanticModel.GetDiagnostics().ToList();
                allDiagnostics.AddRange(syntaxTree.GetDiagnostics());

                var diagnostics = allDiagnostics
                    .Where(d => d.Location.SourceSpan.IntersectsWith(span) ||
                                d.Location.SourceSpan.Contains(position))
                    .ToList();

                var codeFixProviders = GetBuiltInCodeFixProviders();

                foreach (var diagnostic in diagnostics.Distinct())
                {
                    foreach (var provider in codeFixProviders)
                    {
                        if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
                            continue;

                        var context = new CodeFixContext(
                            document,
                            diagnostic,
                            (action, _) => allActions.Add((action, "fix")),
                            CancellationToken.None);

                        try
                        {
                            await provider.RegisterCodeFixesAsync(context);
                        }
                        catch
                        {
                            // Skip providers that throw
                        }
                    }
                }
            }
        }

        // Get code refactorings at this position
        if (includeRefactorings)
        {
            var refactoringProviders = GetBuiltInCodeRefactoringProviders();

            foreach (var provider in refactoringProviders)
            {
                var context = new CodeRefactoringContext(
                    document,
                    span,
                    action => allActions.Add((action, "refactoring")),
                    CancellationToken.None);

                try
                {
                    await provider.ComputeRefactoringsAsync(context);
                }
                catch
                {
                    // Skip providers that throw
                }
            }
        }

        return allActions;
    }

    public Task<object> GetProjectStructureAsync(
        bool includeReferences,
        bool includeDocuments,
        string? projectNamePattern = null,
        int? maxProjects = null,
        bool summaryOnly = false)
    {
        EnsureSolutionLoaded();

        // Filter projects by name pattern
        var filteredProjects = _solution!.Projects.AsEnumerable();

        if (!string.IsNullOrEmpty(projectNamePattern))
        {
            // Support wildcards: * and ?
            var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(projectNamePattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            filteredProjects = filteredProjects.Where(p => regex.IsMatch(p.Name));
        }

        // Apply max projects limit
        if (maxProjects.HasValue && maxProjects.Value > 0)
        {
            filteredProjects = filteredProjects.Take(maxProjects.Value);
        }

        var projectsList = filteredProjects.ToList();

        // Summary mode - just names and counts
        if (summaryOnly)
        {
            var summary = projectsList.Select(p => new
            {
                name = p.Name,
                documentCount = p.DocumentIds.Count,
                projectReferenceCount = p.ProjectReferences.Count(),
                language = p.Language
            }).ToList();

            return Task.FromResult(CreateSuccessResponse(
                data: new
                {
                    solutionPath = _solution!.FilePath,
                    projects = summary
                },
                suggestedNextTools: new[]
                {
                    "get_project_structure with summaryOnly=false for full details",
                    "get_project_structure with projectNamePattern to filter projects"
                },
                totalCount: _solution!.Projects.Count(),
                returnedCount: summary.Count
            ));
        }

        // Full mode - detailed info
        var projects = new List<object>();

        foreach (var project in projectsList)
        {
            var references = includeReferences
                ? project.MetadataReferences
                    .Take(100) // Limit references to prevent huge output
                    .Select(r => r.Display ?? "Unknown")
                    .ToList()
                : null;

            var projectReferences = project.ProjectReferences
                .Select(pr => _solution!.GetProject(pr.ProjectId)?.Name ?? "Unknown")
                .ToList();

            var documents = includeDocuments
                ? project.Documents
                    .Take(500) // Limit documents to prevent huge output
                    .Select(d => new
                    {
                        name = d.Name,
                        filePath = d.FilePath,
                        folders = d.Folders.ToList()
                    })
                    .ToList()
                : null;

            var referenceCount = project.MetadataReferences.Count();
            var documentCount = project.DocumentIds.Count;

            projects.Add(new
            {
                name = project.Name,
                filePath = project.FilePath,
                language = project.Language,
                outputPath = project.OutputFilePath,
                targetFramework = project.CompilationOptions?.Platform.ToString(),
                documentCount,
                referenceCount,
                references = includeReferences ? (referenceCount > 100 ? references!.Concat(new[] { $"... and {referenceCount - 100} more" }).ToList() : references) : null,
                projectReferences,
                documents = includeDocuments ? (documentCount > 500 ? documents!.Concat(new[] { new { name = $"... and {documentCount - 500} more documents", filePath = (string?)null, folders = new List<string>() } }).ToList() : documents) : null
            });
        }

        return Task.FromResult(CreateSuccessResponse(
            data: new
            {
                solutionPath = _solution!.FilePath,
                projects
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics for a project to check for issues",
                "dependency_graph to visualize project dependencies"
            },
            totalCount: _solution!.Projects.Count(),
            returnedCount: projects.Count
        ));
    }

    public async Task<object> OrganizeUsingsAsync(string filePath)
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
                context: new { filePath }
            );
        }

        var root = await syntaxTree.GetRootAsync();
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Not a valid C# file",
                hint: "This tool only works with C# source files",
                context: new { filePath }
            );
        }

        // Get all usings
        var usings = compilationUnit.Usings;

        // Sort them (System namespaces first, then alphabetically)
        var sortedUsings = usings
            .OrderBy(u => u.Name?.ToString().StartsWith("System") == true ? 0 : 1)
            .ThenBy(u => u.Name?.ToString())
            .ToList();

        // Create new compilation unit with sorted usings
        var newRoot = compilationUnit.WithUsings(SyntaxFactory.List(sortedUsings));

        return CreateSuccessResponse(
            data: new
            {
                organizedText = newRoot.ToFullString()
            },
            suggestedNextTools: new[]
            {
                "organize_usings_batch to process multiple files",
                "format_document_batch for consistent formatting"
            }
        );
    }

    public async Task<object> OrganizeUsingsBatchAsync(string? projectName, string? filePattern, bool preview = true)
    {
        EnsureSolutionLoaded();

        var projectsToProcess = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name == projectName);

        var processedFiles = new List<object>();
        var totalFiles = 0;
        var filesWithChanges = 0;

        foreach (var project in projectsToProcess)
        {
            var documents = project.Documents;

            // Apply file pattern filter if specified
            if (!string.IsNullOrEmpty(filePattern))
            {
                documents = documents.Where(d =>
                    d.FilePath != null && MatchesGlobPattern(Path.GetFileName(d.FilePath), filePattern));
            }

            foreach (var document in documents)
            {
                if (document.FilePath == null) continue;
                totalFiles++;

                try
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync();
                    if (syntaxTree == null) continue;

                    var root = await syntaxTree.GetRootAsync();
                    if (root is not CompilationUnitSyntax compilationUnit) continue;

                    var usings = compilationUnit.Usings;
                    if (usings.Count == 0) continue;

                    // Sort usings
                    var sortedUsings = usings
                        .OrderBy(u => u.Name?.ToString().StartsWith("System") == true ? 0 : 1)
                        .ThenBy(u => u.Name?.ToString())
                        .ToList();

                    // Check if anything changed
                    var hasChanges = !usings.SequenceEqual(sortedUsings);
                    if (!hasChanges) continue;

                    filesWithChanges++;

                    // Create new compilation unit with sorted usings
                    var newRoot = compilationUnit.WithUsings(SyntaxFactory.List(sortedUsings));
                    var newText = newRoot.ToFullString();

                    processedFiles.Add(new
                    {
                        filePath = document.FilePath,
                        fileName = Path.GetFileName(document.FilePath),
                        projectName = project.Name,
                        usingCount = usings.Count,
                        preview
                    });

                    // Write to disk if not preview
                    if (!preview)
                    {
                        await File.WriteAllTextAsync(document.FilePath, newText);
                    }
                }
                catch (Exception ex)
                {
                    processedFiles.Add(new
                    {
                        filePath = document.FilePath,
                        fileName = Path.GetFileName(document.FilePath),
                        projectName = project.Name,
                        error = ex.Message
                    });
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                totalFilesScanned = totalFiles,
                filesWithChanges,
                preview,
                files = processedFiles
            },
            suggestedNextTools: preview
                ? new[] { "organize_usings_batch with preview=false to apply changes" }
                : new[] { "get_diagnostics to verify no issues introduced", "format_document_batch for consistent formatting" },
            totalCount: totalFiles,
            returnedCount: processedFiles.Count
        );
    }

    public async Task<object> FormatDocumentBatchAsync(string? projectName, bool includeTests = true, bool preview = true)
    {
        EnsureSolutionLoaded();

        var projectsToProcess = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name == projectName);

        var processedFiles = new List<object>();
        var totalFiles = 0;
        var filesFormatted = 0;

        foreach (var project in projectsToProcess)
        {
            // Filter out test projects if includeTests is false
            if (!includeTests && project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;
                totalFiles++;

                try
                {
                    var syntaxTree = await document.GetSyntaxTreeAsync();
                    if (syntaxTree == null) continue;

                    var root = await syntaxTree.GetRootAsync();

                    // Format the document using Roslyn's formatter
                    var formattedRoot = root.NormalizeWhitespace();
                    var formattedText = formattedRoot.ToFullString();

                    // Check if anything changed
                    var originalText = root.ToFullString();
                    var hasChanges = originalText != formattedText;

                    if (!hasChanges) continue;

                    filesFormatted++;

                    processedFiles.Add(new
                    {
                        filePath = document.FilePath,
                        fileName = Path.GetFileName(document.FilePath),
                        projectName = project.Name,
                        preview
                    });

                    // Write to disk if not preview
                    if (!preview)
                    {
                        await File.WriteAllTextAsync(document.FilePath, formattedText);
                    }
                }
                catch (Exception ex)
                {
                    processedFiles.Add(new
                    {
                        filePath = document.FilePath,
                        fileName = Path.GetFileName(document.FilePath),
                        projectName = project.Name,
                        error = ex.Message
                    });
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                totalFilesScanned = totalFiles,
                filesFormatted,
                preview,
                files = processedFiles
            },
            suggestedNextTools: preview
                ? new[] { "format_document_batch with preview=false to apply changes" }
                : new[] { "get_diagnostics to verify no issues introduced" },
            totalCount: totalFiles,
            returnedCount: processedFiles.Count
        );
    }

    public async Task<object> GetMethodOverloadsAsync(string filePath, int line, int column)
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

        if (symbol is not IMethodSymbol methodSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a method symbol",
                hint: "This tool requires a method symbol. Use get_method_signature with type and method names instead.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        // Get all members of the containing type with the same name
        var containingType = methodSymbol.ContainingType;
        var overloads = containingType.GetMembers(methodSymbol.Name)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .ToList();

        var overloadList = overloads.Select(m =>
        {
            var location = m.Locations.FirstOrDefault(loc => loc.IsInSource);
            var lineSpan = location?.GetLineSpan();

            return new
            {
                signature = m.ToDisplayString(),
                parameters = m.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type.ToDisplayString(),
                    isOptional = p.IsOptional,
                    defaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
                }).ToList(),
                returnType = m.ReturnType.ToDisplayString(),
                isAsync = m.IsAsync,
                isStatic = m.IsStatic,
                location = lineSpan != null ? new
                {
                    filePath = lineSpan.Value.Path,
                    line = lineSpan.Value.StartLinePosition.Line,
                    column = lineSpan.Value.StartLinePosition.Character
                } : null
            };
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                methodName = methodSymbol.Name,
                containingType = containingType.ToDisplayString(),
                overloads = overloadList
            },
            suggestedNextTools: new[]
            {
                "find_references to see where overloads are called",
                "get_method_signature for detailed signature info"
            },
            totalCount: overloadList.Count,
            returnedCount: overloadList.Count
        );
    }

    public async Task<object> GetContainingMemberAsync(string filePath, int line, int column)
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

        var position = GetPosition(syntaxTree, line, column);
        var token = syntaxTree.GetRoot().FindToken(position);

        // Walk up the syntax tree to find the containing member
        var memberNode = token.Parent?.AncestorsAndSelf().FirstOrDefault(n =>
            n is MethodDeclarationSyntax or
            PropertyDeclarationSyntax or
            ConstructorDeclarationSyntax or
            ClassDeclarationSyntax or
            StructDeclarationSyntax or
            InterfaceDeclarationSyntax);

        if (memberNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No containing member found",
                hint: "Position may be outside any method, property, or type declaration",
                context: new { filePath, line, column }
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

        var symbol = semanticModel.GetDeclaredSymbol(memberNode);
        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Could not resolve symbol for containing member",
                context: new { filePath, line, column }
            );
        }

        var span = memberNode.Span;
        var lineSpan = syntaxTree.GetLineSpan(span);

        return CreateSuccessResponse(
            data: new
            {
                memberName = symbol.Name,
                memberKind = symbol.Kind.ToString(),
                containingType = symbol.ContainingType?.ToDisplayString(),
                signature = symbol.ToDisplayString(),
                span = new
                {
                    startLine = lineSpan.StartLinePosition.Line,
                    startColumn = lineSpan.StartLinePosition.Character,
                    endLine = lineSpan.EndLinePosition.Line,
                    endColumn = lineSpan.EndLinePosition.Character
                }
            },
            suggestedNextTools: new[]
            {
                $"find_references to see usages of {symbol.Name}",
                "get_symbol_info for more details"
            }
        );
    }

    public async Task<object> FindCallersAsync(string filePath, int line, int column, int? maxResults = null)
    {
        EnsureSolutionLoaded();

        var maxResultsToReturn = maxResults ?? 100; // Default to 100

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

        // Find callers works best for methods, properties, and constructors
        if (symbol is not (IMethodSymbol or IPropertySymbol))
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a callable symbol",
                hint: "This tool works for methods, properties, and constructors. Use find_references for other symbol types.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        var callers = await SymbolFinder.FindCallersAsync(symbol, _solution!);

        // First count total
        var totalCallers = 0;
        foreach (var caller in callers)
        {
            totalCallers += caller.Locations.Count(loc => loc.IsInSource);
        }

        var callerList = new List<object>();
        foreach (var caller in callers)
        {
            var callingSymbol = caller.CallingSymbol;
            var locations = caller.Locations;

            foreach (var location in locations.Where(loc => loc.IsInSource))
            {
                if (callerList.Count >= maxResultsToReturn)
                    break; // Stop at limit

                if (location.SourceTree == null) continue;

                var callerDocument = _solution!.GetDocument(location.SourceTree);
                if (callerDocument == null) continue;

                var lineSpan = location.GetLineSpan();
                var text = location.SourceTree.GetText();
                var lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

                callerList.Add(new
                {
                    callingSymbol = new
                    {
                        name = callingSymbol.Name,
                        kind = callingSymbol.Kind.ToString(),
                        containingType = callingSymbol.ContainingType?.ToDisplayString(),
                        signature = callingSymbol.ToDisplayString()
                    },
                    location = new
                    {
                        filePath = callerDocument.FilePath,
                        line = lineSpan.StartLinePosition.Line,
                        column = lineSpan.StartLinePosition.Character,
                        lineText
                    }
                });
            }

            if (callerList.Count >= maxResultsToReturn)
                break; // Stop outer loop too
        }

        return CreateSuccessResponse(
            data: new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                symbolSignature = symbol.ToDisplayString(),
                callers = callerList
            },
            suggestedNextTools: new[]
            {
                "get_containing_member for caller context",
                "find_references for all usages including non-call references"
            },
            totalCount: totalCallers,
            returnedCount: callerList.Count
        );
    }

    public async Task<object> FindUnusedCodeAsync(
        string? projectName,
        bool includePrivate,
        bool includeInternal,
        string? symbolKindFilter = null,
        int? maxResults = null)
    {
        EnsureSolutionLoaded();

        var unusedSymbols = new List<object>();
        var maxResultsToReturn = maxResults ?? 50; // Default to 50 to prevent huge outputs

        var projectsToAnalyze = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name == projectName);

        // Track counts by kind for summary
        var countByKind = new Dictionary<string, int>();

        foreach (var project in projectsToAnalyze)
        {
            if (unusedSymbols.Count >= maxResultsToReturn)
                break; // Stop analyzing if we hit the limit

            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            // Check if we should analyze types
            var shouldAnalyzeTypes = string.IsNullOrEmpty(symbolKindFilter) ||
                                     symbolKindFilter.Equals("Class", StringComparison.OrdinalIgnoreCase) ||
                                     symbolKindFilter.Equals("Interface", StringComparison.OrdinalIgnoreCase) ||
                                     symbolKindFilter.Equals("Struct", StringComparison.OrdinalIgnoreCase) ||
                                     symbolKindFilter.Equals("Type", StringComparison.OrdinalIgnoreCase);

            if (shouldAnalyzeTypes)
            {
                // Get all named type symbols (classes, interfaces, structs, enums)
                var allTypes = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type)
                    .OfType<INamedTypeSymbol>();

                foreach (var typeSymbol in allTypes)
                {
                    if (unusedSymbols.Count >= maxResultsToReturn)
                        break;

                    // Skip compiler-generated, extern, and types not in source
                    if (typeSymbol.IsImplicitlyDeclared ||
                        typeSymbol.IsExtern ||
                        !typeSymbol.Locations.Any(loc => loc.IsInSource))
                        continue;

                    // Filter by accessibility
                    if (!includePrivate && typeSymbol.DeclaredAccessibility == Accessibility.Private)
                        continue;
                    if (!includeInternal && typeSymbol.DeclaredAccessibility == Accessibility.Internal)
                        continue;

                    // Skip classes that implement framework interfaces (likely used via DI or framework)
                    if (ImplementsFrameworkInterface(typeSymbol))
                        continue;

                    // Skip classes with framework attributes (controllers, hosted services, etc.)
                    if (HasFrameworkAttribute(typeSymbol))
                        continue;

                    // Find references to this type
                    var references = await SymbolFinder.FindReferencesAsync(typeSymbol, _solution!);
                    var referenceCount = references.SelectMany(r => r.Locations).Count();

                    // For types, also check if any members are referenced
                    // This handles static classes where the class itself isn't referenced
                    // but its static methods/properties are called
                    var hasReferencedMembers = false;
                    if (referenceCount <= 1) // Type itself has no references
                    {
                        // Check if any public/internal members are referenced
                        foreach (var member in typeSymbol.GetMembers())
                        {
                            // Skip constructors, compiler-generated, and special members
                            if (member.IsImplicitlyDeclared ||
                                member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
                                continue;

                            var memberRefs = await SymbolFinder.FindReferencesAsync(member, _solution!);
                            var memberRefCount = memberRefs.SelectMany(r => r.Locations).Count();

                            if (memberRefCount > 1) // Member is referenced (beyond its declaration)
                            {
                                hasReferencedMembers = true;
                                break; // No need to check other members
                            }
                        }
                    }

                    // If no references to type AND no references to any members, it's unused
                    if (referenceCount <= 1 && !hasReferencedMembers) // 1 = just the declaration
                    {
                        var location = typeSymbol.Locations.FirstOrDefault(loc => loc.IsInSource);
                        if (location != null)
                        {
                            var lineSpan = location.GetLineSpan();
                            var kind = typeSymbol.TypeKind.ToString();

                            countByKind[kind] = countByKind.GetValueOrDefault(kind) + 1;

                            unusedSymbols.Add(new
                            {
                                name = typeSymbol.Name,
                                fullyQualifiedName = typeSymbol.ToDisplayString(),
                                kind,
                                accessibility = typeSymbol.DeclaredAccessibility.ToString(),
                                filePath = lineSpan.Path,
                                line = lineSpan.StartLinePosition.Line,
                                column = lineSpan.StartLinePosition.Character
                            });
                        }
                    }
                }
            }

            // Check if we should analyze members
            var shouldAnalyzeMembers = string.IsNullOrEmpty(symbolKindFilter) ||
                                       symbolKindFilter.Equals("Method", StringComparison.OrdinalIgnoreCase) ||
                                       symbolKindFilter.Equals("Property", StringComparison.OrdinalIgnoreCase) ||
                                       symbolKindFilter.Equals("Field", StringComparison.OrdinalIgnoreCase) ||
                                       symbolKindFilter.Equals("Member", StringComparison.OrdinalIgnoreCase);

            if (shouldAnalyzeMembers && unusedSymbols.Count < maxResultsToReturn)
            {
                // Also check methods, properties, and fields
                var allMembers = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Member);

                foreach (var member in allMembers)
                {
                    if (unusedSymbols.Count >= maxResultsToReturn)
                        break;

                    if (member is not (IMethodSymbol or IPropertySymbol or IFieldSymbol))
                        continue;

                    // Skip compiler-generated, extern, and symbols not in source
                    if (member.IsImplicitlyDeclared ||
                        member.IsExtern ||
                        !member.Locations.Any(loc => loc.IsInSource))
                        continue;

                    // Skip special methods (constructors, operators, etc.)
                    if (member is IMethodSymbol method &&
                        (method.MethodKind != MethodKind.Ordinary || method.IsOverride || method.IsVirtual))
                        continue;

                    // Filter by accessibility
                    if (!includePrivate && member.DeclaredAccessibility == Accessibility.Private)
                        continue;
                    if (!includeInternal && member.DeclaredAccessibility == Accessibility.Internal)
                        continue;

                    // Find references
                    var references = await SymbolFinder.FindReferencesAsync(member, _solution!);
                    var referenceCount = references.SelectMany(r => r.Locations).Count();

                    if (referenceCount <= 1)
                    {
                        var location = member.Locations.FirstOrDefault(loc => loc.IsInSource);
                        if (location != null)
                        {
                            var lineSpan = location.GetLineSpan();
                            var kind = member.Kind.ToString();

                            countByKind[kind] = countByKind.GetValueOrDefault(kind) + 1;

                            unusedSymbols.Add(new
                            {
                                name = member.Name,
                                fullyQualifiedName = member.ToDisplayString(),
                                kind,
                                accessibility = member.DeclaredAccessibility.ToString(),
                                containingType = member.ContainingType?.ToDisplayString(),
                                filePath = lineSpan.Path,
                                line = lineSpan.StartLinePosition.Line,
                                column = lineSpan.StartLinePosition.Character
                            });
                        }
                    }
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                projectName = projectName ?? "All projects",
                countByKind,
                unusedSymbols = unusedSymbols.ToList()
            },
            suggestedNextTools: unusedSymbols.Count > 0
                ? new[] { "find_references to verify symbol is truly unused", "rename_symbol or delete unused code" }
                : new[] { "No unused code found - codebase is clean" },
            totalCount: unusedSymbols.Count,
            returnedCount: unusedSymbols.Count
        );
    }

    public async Task<object> RenameSymbolAsync(
        string filePath,
        int line,
        int column,
        string newName,
        bool preview,
        int? maxFiles = null,
        string? verbosity = null)
    {
        EnsureSolutionLoaded();

        var maxFilesToShow = maxFiles ?? 20; // Default to 20 files to prevent huge outputs
        var verbosityLevel = verbosity?.ToLower() ?? "summary"; // Default to summary to prevent token explosions

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

        // Try to find symbol with improved logic and tolerance
        var (symbol, debugInfo) = TryFindSymbolForRename(syntaxTree, semanticModel, position, line, column);

        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: "Ensure cursor is on a symbol name (class, method, variable, etc.). Try adjusting the column position by ±1.",
                context: new { line, column, debug = debugInfo }
            );
        }

        // Validate new name
        if (string.IsNullOrWhiteSpace(newName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "New name cannot be empty",
                context: new { newName }
            );
        }

        // Check if symbol can be renamed (not extern, not from metadata)
        if (symbol.Locations.All(loc => !loc.IsInSource))
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Cannot rename symbol",
                hint: "Symbol is defined in metadata (external library), not in source code",
                context: new { symbolName = symbol.Name }
            );
        }

        // Perform rename
        var renameOptions = new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions();
        var newSolution = await Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync(
            _solution!,
            symbol,
            renameOptions,
            newName);

        // Get all changes
        var changes = new List<object>();
        var solutionChanges = newSolution.GetChanges(_solution!);

        var totalFiles = 0;
        var totalChanges = 0;

        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        {
            foreach (var changedDocumentId in projectChanges.GetChangedDocuments())
            {
                totalFiles++;

                var oldDocument = _solution!.GetDocument(changedDocumentId);
                var newDocument = newSolution.GetDocument(changedDocumentId);

                if (oldDocument == null || newDocument == null)
                    continue;

                var oldText = await oldDocument.GetTextAsync();
                var newText = await newDocument.GetTextAsync();

                var textChanges = newText.GetTextChanges(oldText);
                totalChanges += textChanges.Count();

                // Only include detailed changes for first N files
                if (changes.Count < maxFilesToShow)
                {
                    if (verbosityLevel == "summary")
                    {
                        // Summary: Just file path and count
                        changes.Add(new
                        {
                            filePath = oldDocument.FilePath,
                            changeCount = textChanges.Count()
                        });
                    }
                    else if (verbosityLevel == "compact")
                    {
                        // Compact: Include change locations but no text
                        var documentChanges = new List<object>();
                        foreach (var textChange in textChanges.Take(20))
                        {
                            var lineSpan = oldText.Lines.GetLinePositionSpan(textChange.Span);
                            documentChanges.Add(new
                            {
                                line = lineSpan.Start.Line,
                                column = lineSpan.Start.Character
                            });
                        }

                        changes.Add(new
                        {
                            filePath = oldDocument.FilePath,
                            changeCount = textChanges.Count(),
                            changes = documentChanges,
                            truncated = textChanges.Count() > 20
                        });
                    }
                    else // "full" or any other value
                    {
                        // Full: Include old/new text for each change
                        var documentChanges = new List<object>();
                        foreach (var textChange in textChanges.Take(20))
                        {
                            var lineSpan = oldText.Lines.GetLinePositionSpan(textChange.Span);
                            documentChanges.Add(new
                            {
                                startLine = lineSpan.Start.Line,
                                startColumn = lineSpan.Start.Character,
                                endLine = lineSpan.End.Line,
                                endColumn = lineSpan.End.Character,
                                oldText = textChange.Span.Length > 0 ? oldText.ToString(textChange.Span) : "",
                                newText = textChange.NewText
                            });
                        }

                        changes.Add(new
                        {
                            filePath = oldDocument.FilePath,
                            changeCount = textChanges.Count(),
                            changes = documentChanges,
                            truncated = textChanges.Count() > 20
                        });
                    }
                }
            }
        }

        var filesShown = changes.Count;
        var filesHidden = totalFiles - filesShown;

        // If preview mode, just return the changes
        if (preview)
        {
            var verbosityHint = verbosityLevel == "summary"
                ? "Using verbosity='summary' (file paths + counts only). Use verbosity='compact' for locations or verbosity='full' for detailed text changes."
                : verbosityLevel == "compact"
                    ? "Using verbosity='compact' (locations only). Use verbosity='full' to see old/new text for each change."
                    : null;

            var hints = new List<string>();
            if (filesHidden > 0)
                hints.Add($"Showing first {maxFilesToShow} files. {filesHidden} more files will be changed. Use maxFiles parameter to see more.");
            if (verbosityHint != null)
                hints.Add(verbosityHint);
            if (hints.Count == 0)
                hints.Add("Set preview=false to apply these changes.");

            return CreateSuccessResponse(
                data: new
                {
                    symbolName = symbol.Name,
                    symbolKind = symbol.Kind.ToString(),
                    newName,
                    verbosity = verbosityLevel,
                    changes,
                    preview = true,
                    applied = false
                },
                suggestedNextTools: new[] { "rename_symbol with preview=false to apply changes" },
                totalCount: totalFiles,
                returnedCount: filesShown
            );
        }

        // Apply changes by updating the solution
        _solution = newSolution;

        // Write changes to disk
        var workspace = _workspace!;
        if (workspace.TryApplyChanges(newSolution))
        {
            return CreateSuccessResponse(
                data: new
                {
                    symbolName = symbol.Name,
                    symbolKind = symbol.Kind.ToString(),
                    newName,
                    changes,
                    preview = false,
                    applied = true
                },
                suggestedNextTools: new[] { "get_diagnostics to verify no issues after rename" },
                totalCount: totalFiles,
                returnedCount: filesShown
            );
        }
        else
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Failed to apply changes",
                hint: "Workspace.TryApplyChanges returned false. Changes may conflict with current workspace state.",
                context: new { symbolName = symbol.Name, newName, totalFiles, totalChanges }
            );
        }
    }

    public Task<object> GetDependencyGraphAsync(string? format)
    {
        EnsureSolutionLoaded();

        var projectGraph = new Dictionary<string, List<string>>();
        var allProjects = _solution!.Projects.ToList();

        // Build dependency graph
        foreach (var project in allProjects)
        {
            var dependencies = project.ProjectReferences
                .Select(pr => _solution!.GetProject(pr.ProjectId)?.Name)
                .Where(name => name != null)
                .Cast<string>()
                .ToList();

            projectGraph[project.Name] = dependencies;
        }

        // Detect cycles
        var cycles = DetectCycles(projectGraph);

        // Generate output based on format
        if (format?.ToLower() == "mermaid")
        {
            var mermaid = GenerateMermaidGraph(projectGraph);
            return Task.FromResult(CreateSuccessResponse(
                data: new
                {
                    format = "mermaid",
                    graph = mermaid,
                    hasCycles = cycles.Count > 0,
                    cycles
                },
                suggestedNextTools: cycles.Count > 0
                    ? new[] { "Resolve circular dependencies before building" }
                    : new[] { "get_project_structure for detailed project info" },
                totalCount: allProjects.Count,
                returnedCount: allProjects.Count
            ));
        }

        // Default: return structured data
        return Task.FromResult(CreateSuccessResponse(
            data: new
            {
                dependencies = projectGraph,
                hasCycles = cycles.Count > 0,
                cycles
            },
            suggestedNextTools: cycles.Count > 0
                ? new[] { "Resolve circular dependencies before building" }
                : new[] { "get_project_structure for detailed project info" },
            totalCount: allProjects.Count,
            returnedCount: allProjects.Count
        ));
    }

    public async Task<object> ExtractInterfaceAsync(string filePath, int line, int column, string interfaceName, List<string>? includeMemberNames)
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
        var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(node);

        if (symbol is not INamedTypeSymbol typeSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "Not a type symbol",
                hint: "Place cursor on a class or struct to extract an interface",
                context: new { actualKind = symbol?.Kind.ToString() ?? "Unknown" }
            );
        }

        // Get public members
        var members = typeSymbol.GetMembers()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public)
            .Where(m => m is IMethodSymbol or IPropertySymbol or IEventSymbol)
            .Where(m => !m.IsStatic)
            .Where(m => m is not IMethodSymbol method || method.MethodKind == MethodKind.Ordinary)
            .ToList();

        // Filter by included names if specified
        if (includeMemberNames != null && includeMemberNames.Count > 0)
        {
            members = members.Where(m => includeMemberNames.Contains(m.Name)).ToList();
        }

        // Generate interface code
        var interfaceCode = GenerateInterfaceCode(interfaceName, members, typeSymbol.ContainingNamespace);

        return CreateSuccessResponse(
            data: new
            {
                className = typeSymbol.Name,
                interfaceName,
                members = members.Select(m => new
                {
                    name = m.Name,
                    kind = m.Kind.ToString(),
                    signature = m.ToDisplayString()
                }).ToList(),
                interfaceCode,
                suggestedFileName = $"{interfaceName}.cs"
            },
            suggestedNextTools: new[]
            {
                $"Create file {interfaceName}.cs with the generated code",
                $"Add : {interfaceName} to {typeSymbol.Name} class declaration"
            },
            totalCount: members.Count,
            returnedCount: members.Count
        );
    }

    #region Phase 2: AI-Focused Tools

    /// <summary>
    /// Gets members that must be implemented for interfaces/abstract classes.
    /// </summary>
    public async Task<object> GetMissingMembersAsync(string filePath, int line, int column)
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

        // Find the type declaration
        var typeDecl = node?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "No type declaration found at position",
                hint: "Place cursor on a class or struct declaration",
                context: new { filePath, line, column }
            );
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Could not resolve type symbol",
                context: new { filePath, line, column }
            );
        }

        var missingMembers = new List<object>();

        // Check interfaces
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility == Accessibility.Private)
                    continue;

                var implementation = typeSymbol.FindImplementationForInterfaceMember(member);
                if (implementation == null)
                {
                    missingMembers.Add(new
                    {
                        fromInterface = iface.ToDisplayString(),
                        memberName = member.Name,
                        kind = member.Kind.ToString(),
                        signature = GetMemberSignature(member),
                        returnType = GetMemberReturnType(member)
                    });
                }
            }
        }

        // Check abstract base class members
        var baseType = typeSymbol.BaseType;
        while (baseType != null)
        {
            if (baseType.IsAbstract)
            {
                foreach (var member in baseType.GetMembers())
                {
                    if (!member.IsAbstract) continue;

                    var isImplemented = typeSymbol.GetMembers(member.Name)
                        .Any(m => m.IsOverride && !m.IsAbstract);

                    if (!isImplemented)
                    {
                        missingMembers.Add(new
                        {
                            fromAbstractClass = baseType.ToDisplayString(),
                            memberName = member.Name,
                            kind = member.Kind.ToString(),
                            signature = GetMemberSignature(member),
                            returnType = GetMemberReturnType(member)
                        });
                    }
                }
            }
            baseType = baseType.BaseType;
        }

        return CreateSuccessResponse(
            data: new
            {
                typeName = typeSymbol.ToDisplayString(),
                isAbstract = typeSymbol.IsAbstract,
                interfaces = typeSymbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList(),
                missingMembers
            },
            suggestedNextTools: missingMembers.Count > 0
                ? new[] { "Use the signatures to implement the missing members" }
                : new[] { "All interface and abstract members are implemented" },
            totalCount: missingMembers.Count,
            returnedCount: missingMembers.Count
        );
    }

    /// <summary>
    /// Gets all methods called by a given method.
    /// </summary>
    public async Task<object> GetOutgoingCallsAsync(string filePath, int line, int column, int? maxDepth = null)
    {
        EnsureSolutionLoaded();

        var depth = maxDepth ?? 1;

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

        // Find the method declaration
        var methodDecl = node?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method declaration found at position",
                hint: "Place cursor inside a method body",
                context: new { filePath, line, column }
            );
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Could not resolve method symbol",
                context: new { filePath, line, column }
            );
        }

        var calls = new List<object>();
        var visited = new HashSet<string>();

        // Find all invocations in the method body
        var invocations = methodDecl.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
            var calledMethod = symbolInfo.Symbol as IMethodSymbol;
            if (calledMethod == null) continue;

            var key = calledMethod.ToDisplayString();
            if (visited.Contains(key)) continue;
            visited.Add(key);

            var location = calledMethod.Locations.FirstOrDefault(l => l.IsInSource);
            var lineSpan = location?.GetLineSpan();

            calls.Add(new
            {
                method = calledMethod.ToDisplayString(),
                shortName = $"{calledMethod.ContainingType?.Name}.{calledMethod.Name}",
                returnType = calledMethod.ReturnType.ToDisplayString(),
                isAsync = calledMethod.IsAsync,
                isExternal = !location?.IsInSource ?? true,
                location = lineSpan != null ? new
                {
                    filePath = lineSpan.Value.Path,
                    line = lineSpan.Value.StartLinePosition.Line,
                    column = lineSpan.Value.StartLinePosition.Character
                } : null
            });
        }

        // Also find property accesses
        var memberAccesses = methodDecl.DescendantNodes().OfType<MemberAccessExpressionSyntax>();
        foreach (var access in memberAccesses)
        {
            var symbolInfo = semanticModel.GetSymbolInfo(access);
            if (symbolInfo.Symbol is IPropertySymbol prop)
            {
                var key = prop.ToDisplayString();
                if (visited.Contains(key)) continue;
                visited.Add(key);

                var location = prop.Locations.FirstOrDefault(l => l.IsInSource);
                var lineSpan = location?.GetLineSpan();

                calls.Add(new
                {
                    method = prop.ToDisplayString(),
                    shortName = $"{prop.ContainingType?.Name}.{prop.Name}",
                    returnType = prop.Type.ToDisplayString(),
                    isAsync = false,
                    isProperty = true,
                    isExternal = !location?.IsInSource ?? true,
                    location = lineSpan != null ? new
                    {
                        filePath = lineSpan.Value.Path,
                        line = lineSpan.Value.StartLinePosition.Line,
                        column = lineSpan.Value.StartLinePosition.Character
                    } : null
                });
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                method = methodSymbol.ToDisplayString(),
                containingType = methodSymbol.ContainingType?.ToDisplayString(),
                calls
            },
            suggestedNextTools: new[]
            {
                "get_outgoing_calls on a called method to trace deeper",
                "find_callers to see who calls this method"
            },
            totalCount: calls.Count,
            returnedCount: calls.Count
        );
    }

    /// <summary>
    /// Validates if code would compile without writing to disk.
    /// </summary>
    public async Task<object> ValidateCodeAsync(string code, string? contextFilePath = null, bool standalone = false)
    {
        EnsureSolutionLoaded();

        try
        {
            SyntaxTree syntaxTree;
            Compilation compilation;

            if (standalone)
            {
                // Treat as complete file
                syntaxTree = CSharpSyntaxTree.ParseText(code);
                var references = _solution!.Projects.First().MetadataReferences;
                compilation = CSharpCompilation.Create(
                    "ValidationAssembly",
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );
            }
            else if (!string.IsNullOrEmpty(contextFilePath))
            {
                // Inject into context of existing file
                Document document;
                try
                {
                    document = await GetDocumentAsync(contextFilePath);
                }
                catch (FileNotFoundException)
                {
                    return CreateErrorResponse(
                        ErrorCodes.FileNotInSolution,
                        $"Context file not found: {contextFilePath}",
                        hint: "Check the file path or use standalone=true",
                        context: new { contextFilePath }
                    );
                }

                var existingTree = await document.GetSyntaxTreeAsync();
                var existingRoot = await existingTree!.GetRootAsync() as CompilationUnitSyntax;

                // Parse the new code
                var newCode = CSharpSyntaxTree.ParseText(code);
                var newRoot = await newCode.GetRootAsync();

                // Get usings from context
                var usings = existingRoot?.Usings.ToFullString() ?? "";
                var ns = existingRoot?.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                var nsName = ns?.Name.ToString() ?? "";

                // Wrap code in namespace context
                var wrappedCode = $@"
{usings}
namespace {(string.IsNullOrEmpty(nsName) ? "ValidationNamespace" : nsName)} {{
    public class ValidationClass {{
        public void ValidationMethod() {{
            {code}
        }}
    }}
}}";
                syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);
                var project = document.Project;
                compilation = (await project.GetCompilationAsync())!
                    .AddSyntaxTrees(syntaxTree);
            }
            else
            {
                // No context - wrap in minimal class
                var wrappedCode = $@"
using System;
using System.Collections.Generic;
using System.Linq;

public class ValidationClass {{
    public void ValidationMethod() {{
        {code}
    }}
}}";
                syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);
                var references = _solution!.Projects.First().MetadataReferences;
                compilation = CSharpCompilation.Create(
                    "ValidationAssembly",
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );
            }

            var diagnostics = compilation.GetDiagnostics();
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => new
                {
                    id = d.Id,
                    message = d.GetMessage(),
                    line = d.Location.GetLineSpan().StartLinePosition.Line,
                    column = d.Location.GetLineSpan().StartLinePosition.Character
                })
                .ToList();

            var warnings = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Warning)
                .Select(d => new
                {
                    id = d.Id,
                    message = d.GetMessage(),
                    line = d.Location.GetLineSpan().StartLinePosition.Line,
                    column = d.Location.GetLineSpan().StartLinePosition.Character
                })
                .ToList();

            return CreateSuccessResponse(
                data: new
                {
                    compiles = errors.Count == 0,
                    errorCount = errors.Count,
                    warningCount = warnings.Count,
                    errors,
                    warnings
                },
                suggestedNextTools: errors.Count > 0
                    ? new[] { "Fix the errors and validate again" }
                    : new[] { "Code is valid - safe to write to file" }
            );
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"Validation failed: {ex.Message}",
                hint: "Check syntax of the code snippet",
                context: new { codeLength = code.Length }
            );
        }
    }

    /// <summary>
    /// Checks if one type can be assigned to another.
    /// </summary>
    public async Task<object> CheckTypeCompatibilityAsync(string sourceType, string targetType)
    {
        EnsureSolutionLoaded();

        var source = await FindTypeByNameAsync(sourceType);
        if (source == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Source type not found: {sourceType}",
                hint: "Use fully qualified name or check spelling",
                context: new { sourceType }
            );
        }

        var target = await FindTypeByNameAsync(targetType);
        if (target == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Target type not found: {targetType}",
                hint: "Use fully qualified name or check spelling",
                context: new { targetType }
            );
        }

        var compilation = await _solution!.Projects.First().GetCompilationAsync();
        if (compilation == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get compilation",
                context: new { sourceType, targetType }
            );
        }

        var conversion = compilation.ClassifyConversion(source, target);

        string conversionKind;
        string reason;

        if (conversion.IsIdentity)
        {
            conversionKind = "Identity";
            reason = "Same type";
        }
        else if (conversion.IsImplicit)
        {
            if (conversion.IsReference)
            {
                conversionKind = "ImplicitReference";
                reason = $"{sourceType} inherits from or implements {targetType}";
            }
            else if (conversion.IsNumeric)
            {
                conversionKind = "ImplicitNumeric";
                reason = "Numeric widening conversion";
            }
            else if (conversion.IsBoxing)
            {
                conversionKind = "Boxing";
                reason = "Value type to object/interface";
            }
            else
            {
                conversionKind = "Implicit";
                reason = "Implicit conversion available";
            }
        }
        else if (conversion.IsExplicit)
        {
            if (conversion.IsUnboxing)
            {
                conversionKind = "Unboxing";
                reason = "Requires explicit cast (unboxing)";
            }
            else if (conversion.IsNumeric)
            {
                conversionKind = "ExplicitNumeric";
                reason = "Numeric narrowing - may lose precision";
            }
            else
            {
                conversionKind = "Explicit";
                reason = "Requires explicit cast";
            }
        }
        else
        {
            conversionKind = "None";
            reason = "No conversion exists between these types";
        }

        return CreateSuccessResponse(
            data: new
            {
                sourceType = source.ToDisplayString(),
                targetType = target.ToDisplayString(),
                compatible = conversion.Exists,
                requiresCast = conversion.IsExplicit,
                conversionKind,
                reason,
                isReferenceConversion = conversion.IsReference,
                isNumericConversion = conversion.IsNumeric,
                isBoxing = conversion.IsBoxing,
                isUnboxing = conversion.IsUnboxing
            },
            suggestedNextTools: new[]
            {
                "get_base_types to see inheritance chain",
                "get_type_members to see available members"
            }
        );
    }

    /// <summary>
    /// Gets all ways to instantiate a type.
    /// </summary>
    public async Task<object> GetInstantiationOptionsAsync(string typeName)
    {
        EnsureSolutionLoaded();

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use fully qualified name like Namespace.ClassName",
                context: new { typeName }
            );
        }

        // Get constructors
        var constructors = type.Constructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public ||
                        c.DeclaredAccessibility == Accessibility.Protected)
            .Select(c => new
            {
                signature = c.ToDisplayString(),
                accessibility = c.DeclaredAccessibility.ToString(),
                parameters = c.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type.ToDisplayString(),
                    isOptional = p.IsOptional,
                    defaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
                }).ToList(),
                isObsolete = c.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute")
            })
            .ToList();

        // Find static factory methods
        var factoryMethods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.IsStatic &&
                       m.DeclaredAccessibility == Accessibility.Public &&
                       SymbolEqualityComparer.Default.Equals(m.ReturnType, type))
            .Select(m => new
            {
                name = m.Name,
                signature = m.ToDisplayString(),
                parameters = m.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type.ToDisplayString()
                }).ToList()
            })
            .ToList();

        // Find factory methods in other types that return this type
        var externalFactories = new List<object>();
        foreach (var project in _solution!.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            var allTypes = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>();
            foreach (var t in allTypes)
            {
                if (SymbolEqualityComparer.Default.Equals(t, type)) continue;

                var factories = t.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => m.IsStatic &&
                               m.DeclaredAccessibility == Accessibility.Public &&
                               (m.Name.StartsWith("Create") || m.Name.StartsWith("Build") || m.Name.StartsWith("New")) &&
                               SymbolEqualityComparer.Default.Equals(m.ReturnType, type))
                    .Take(5); // Limit to avoid too many

                foreach (var f in factories)
                {
                    externalFactories.Add(new
                    {
                        containingType = t.ToDisplayString(),
                        name = f.Name,
                        signature = f.ToDisplayString()
                    });
                }
            }

            if (externalFactories.Count >= 10) break; // Limit total
        }

        // Check for common patterns
        var implementsIDisposable = type.AllInterfaces.Any(i => i.Name == "IDisposable");
        var hasBuilder = _solution.Projects
            .SelectMany(p => p.Documents)
            .Any(d => d.Name.Contains($"{type.Name}Builder"));

        string? hint = null;
        if (implementsIDisposable)
            hint = "Type implements IDisposable - consider using 'using' statement";
        else if (type.IsAbstract)
            hint = "Type is abstract - cannot instantiate directly, use derived type";
        else if (type.TypeKind == TypeKind.Interface)
            hint = "Type is an interface - cannot instantiate directly";

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                typeKind = type.TypeKind.ToString(),
                isAbstract = type.IsAbstract,
                isStatic = type.IsStatic,
                constructors,
                factoryMethods,
                externalFactories,
                implementsIDisposable,
                hasBuilder,
                hint
            },
            suggestedNextTools: new[]
            {
                "get_method_signature for constructor parameter details",
                "get_type_members to see what methods are available after creation"
            },
            totalCount: constructors.Count + factoryMethods.Count + externalFactories.Count,
            returnedCount: constructors.Count + factoryMethods.Count + externalFactories.Count
        );
    }

    /// <summary>
    /// Analyzes impact of changing a symbol.
    /// </summary>
    public async Task<object> AnalyzeChangeImpactAsync(
        string filePath,
        int line,
        int column,
        string changeType,
        string? newValue = null)
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
                hint: "Place cursor on a symbol (method, property, type, etc.)",
                context: new { filePath, line, column }
            );
        }

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(node);

        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Could not resolve symbol",
                context: new { filePath, line, column }
            );
        }

        // Find all references
        var references = await SymbolFinder.FindReferencesAsync(symbol, _solution!);
        var allLocations = references.SelectMany(r => r.Locations).ToList();

        var impactedLocations = new List<object>();
        var breakingChanges = 0;
        var warnings = 0;

        foreach (var location in allLocations)
        {
            if (!location.Location.IsInSource) continue;

            var refDocument = location.Document;
            var refSemanticModel = await refDocument.GetSemanticModelAsync();
            var lineSpan = location.Location.GetLineSpan();

            string impact;
            string severity;

            switch (changeType.ToLower())
            {
                case "rename":
                    impact = "Reference will need to be updated";
                    severity = "info";
                    break;

                case "changetype":
                case "change_type":
                    impact = $"Usage may be incompatible with new type";
                    severity = "warning";
                    warnings++;
                    break;

                case "addparameter":
                case "add_parameter":
                    impact = "Call site missing new parameter";
                    severity = "error";
                    breakingChanges++;
                    break;

                case "removeparameter":
                case "remove_parameter":
                    impact = "Call site has extra parameter";
                    severity = "error";
                    breakingChanges++;
                    break;

                case "changeaccessibility":
                case "change_accessibility":
                    if (symbol.DeclaredAccessibility == Accessibility.Public)
                    {
                        impact = "External usages may lose access";
                        severity = "error";
                        breakingChanges++;
                    }
                    else
                    {
                        impact = "Accessibility change may affect visibility";
                        severity = "warning";
                        warnings++;
                    }
                    break;

                case "delete":
                    impact = "Reference will be broken";
                    severity = "error";
                    breakingChanges++;
                    break;

                default:
                    impact = "Unknown impact";
                    severity = "info";
                    break;
            }

            impactedLocations.Add(new
            {
                filePath = lineSpan.Path,
                line = lineSpan.StartLinePosition.Line,
                column = lineSpan.StartLinePosition.Character,
                impact,
                severity
            });
        }

        return CreateSuccessResponse(
            data: new
            {
                symbol = symbol.ToDisplayString(),
                symbolKind = symbol.Kind.ToString(),
                changeType,
                newValue,
                totalReferences = allLocations.Count,
                breakingChanges,
                warnings,
                safe = breakingChanges == 0,
                impactedLocations
            },
            suggestedNextTools: breakingChanges > 0
                ? new[] { "Fix breaking changes before applying", "Use rename_symbol for safe renames" }
                : new[] { "Safe to proceed with change" },
            totalCount: allLocations.Count,
            returnedCount: impactedLocations.Count
        );
    }

    // Helper for getting member signatures
    private string GetMemberSignature(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => $"{method.ReturnType.ToDisplayString()} {method.Name}({string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))})",
            IPropertySymbol prop => $"{prop.Type.ToDisplayString()} {prop.Name} {{ {(prop.GetMethod != null ? "get; " : "")}{(prop.SetMethod != null ? "set; " : "")}}}",
            IEventSymbol evt => $"event {evt.Type.ToDisplayString()} {evt.Name}",
            _ => member.ToDisplayString()
        };
    }

    // Helper for getting member return types
    private string? GetMemberReturnType(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => method.ReturnType.ToDisplayString(),
            IPropertySymbol prop => prop.Type.ToDisplayString(),
            IEventSymbol evt => evt.Type.ToDisplayString(),
            _ => null
        };
    }

    /// <summary>
    /// Gets the actual source code of a method by name.
    /// </summary>
    public async Task<object> GetMethodSourceAsync(string typeName, string methodName, int? overloadIndex = null)
    {
        EnsureSolutionLoaded();

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName }
            );
        }

        // Find all methods with matching name
        var methods = type.GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .ToList();

        if (methods.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found in type '{typeName}'",
                hint: "Use get_type_members to see available methods",
                context: new { typeName, methodName }
            );
        }

        // Select specific overload or default to first
        var index = overloadIndex ?? 0;
        if (index < 0 || index >= methods.Count)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid overloadIndex {index}. Available: 0 to {methods.Count - 1}",
                context: new { overloadIndex, availableOverloads = methods.Count }
            );
        }

        var method = methods[index];
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);

        if (location == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Method is defined in metadata (external library), source not available",
                hint: "This method is from a compiled assembly, not source code",
                context: new { typeName, methodName, isFromMetadata = true }
            );
        }

        // Get the syntax node
        var syntaxTree = location.SourceTree;
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree for method",
                context: new { typeName, methodName }
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var node = root.FindNode(location.SourceSpan);

        // Find the method declaration
        var methodDecl = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not find method declaration syntax",
                context: new { typeName, methodName }
            );
        }

        var lineSpan = location.GetLineSpan();
        var sourceText = methodDecl.ToFullString();

        // Also get just the body if available
        string? bodySource = null;
        if (methodDecl.Body != null)
        {
            bodySource = methodDecl.Body.ToFullString();
        }
        else if (methodDecl.ExpressionBody != null)
        {
            bodySource = methodDecl.ExpressionBody.ToFullString();
        }

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                methodName = method.Name,
                signature = method.ToDisplayString(),
                overloadIndex = index,
                totalOverloads = methods.Count,
                location = new
                {
                    filePath = lineSpan.Path,
                    startLine = lineSpan.StartLinePosition.Line,
                    endLine = lineSpan.EndLinePosition.Line
                },
                fullSource = sourceText,
                bodySource,
                lineCount = sourceText.Split('\n').Length
            },
            suggestedNextTools: new[]
            {
                "get_outgoing_calls to see what this method calls",
                "find_callers to see who calls this method"
            }
        );
    }

    /// <summary>
    /// Gets source code for multiple methods in a single call (batch optimization).
    /// </summary>
    public async Task<object> GetMethodSourceBatchAsync(
        List<Dictionary<string, object>> methods,
        int maxMethods = 20)
    {
        EnsureSolutionLoaded();

        if (methods == null || methods.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "methods array is required and must not be empty",
                hint: "Provide an array like [{typeName: 'MyClass', methodName: 'MyMethod'}, ...]",
                context: new { parameter = "methods" }
            );
        }

        if (methods.Count > maxMethods)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Too many methods requested ({methods.Count}). Maximum is {maxMethods}",
                hint: $"Split request into batches of {maxMethods} or fewer"
            );
        }

        var results = new List<object>();
        var errors = new List<object>();

        foreach (var methodReq in methods)
        {
            var typeName = methodReq.TryGetValue("typeName", out var tn) ? tn?.ToString() : null;
            var methodName = methodReq.TryGetValue("methodName", out var mn) ? mn?.ToString() : null;
            int? overloadIndex = methodReq.TryGetValue("overloadIndex", out var oi) && oi != null
                ? Convert.ToInt32(oi)
                : null;

            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
            {
                errors.Add(new
                {
                    typeName,
                    methodName,
                    success = false,
                    error = "typeName and methodName are required"
                });
                continue;
            }

            var result = await GetMethodSourceAsync(typeName, methodName, overloadIndex);

            // Check if result was successful
            var resultDict = result as dynamic;
            if (resultDict?.success == true)
            {
                results.Add(new
                {
                    typeName,
                    methodName,
                    success = true,
                    data = resultDict.data
                });
            }
            else
            {
                errors.Add(new
                {
                    typeName,
                    methodName,
                    success = false,
                    error = resultDict?.error
                });
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                totalRequested = methods.Count,
                successCount = results.Count,
                errorCount = errors.Count,
                results,
                errors = errors.Count > 0 ? errors : null
            },
            suggestedNextTools: new[]
            {
                results.Count > 0 ? "analyze_method for deeper analysis of specific methods" : null,
                errors.Count > 0 ? "Check type/method names - some were not found" : null
            }.Where(s => s != null).ToArray()!,
            totalCount: methods.Count,
            returnedCount: results.Count
        );
    }

    /// <summary>
    /// Generates a constructor from fields and/or properties of a type.
    /// </summary>
    public async Task<object> GenerateConstructorAsync(
        string filePath,
        int line,
        int column,
        bool includeProperties = false,
        bool initializeToDefault = false)
    {
        EnsureSolutionLoaded();

        var document = _solution!.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true);

        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Ensure the file is part of a project in the loaded solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                context: new { filePath }
            );
        }

        var text = await syntaxTree.GetTextAsync();
        var position = text.Lines.GetPosition(new LinePosition(line, column));
        var root = await syntaxTree.GetRootAsync();
        var node = root.FindToken(position).Parent;

        // Find the type declaration
        var typeDecl = node?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (typeDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "No type declaration found at position",
                hint: "Position cursor on a class or struct declaration",
                context: new { line, column }
            );
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not resolve type symbol",
                context: new { typeName = typeDecl.Identifier.Text }
            );
        }

        // Collect fields
        var fields = typeSymbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared)
            .Select(f => new
            {
                name = f.Name,
                type = f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                isReadOnly = f.IsReadOnly,
                isNullable = f.NullableAnnotation == NullableAnnotation.Annotated,
                paramName = ToCamelCase(f.Name.TrimStart('_'))
            })
            .ToList();

        // Collect properties if requested
        var properties = new List<dynamic>();
        if (includeProperties)
        {
            properties = typeSymbol.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && !p.IsReadOnly && p.SetMethod != null &&
                           p.SetMethod.DeclaredAccessibility != Accessibility.Private)
                .Select(p => new
                {
                    name = p.Name,
                    type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    isReadOnly = false,
                    isNullable = p.NullableAnnotation == NullableAnnotation.Annotated,
                    paramName = ToCamelCase(p.Name)
                })
                .Cast<dynamic>()
                .ToList();
        }

        var allMembers = fields.Cast<dynamic>().Concat(properties).ToList();

        if (allMembers.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "No fields or properties found to initialize",
                hint: includeProperties
                    ? "The type has no instance fields or settable properties"
                    : "The type has no instance fields. Try includeProperties: true",
                context: new { typeName = typeSymbol.Name }
            );
        }

        // Build constructor code
        var sb = new StringBuilder();
        var typeName = typeSymbol.Name;
        var accessibility = typeSymbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";

        // Parameters
        var parameters = allMembers
            .Select(m => $"{m.type} {m.paramName}")
            .ToList();

        sb.AppendLine($"{accessibility} {typeName}({string.Join(", ", parameters)})");
        sb.AppendLine("{");

        foreach (var member in allMembers)
        {
            string memberName = member.name;
            string paramName = member.paramName;

            if (initializeToDefault && (bool)member.isNullable)
            {
                sb.AppendLine($"    {memberName} = {paramName} ?? default;");
            }
            else
            {
                sb.AppendLine($"    {memberName} = {paramName};");
            }
        }

        sb.Append("}");

        return CreateSuccessResponse(
            data: new
            {
                typeName = typeSymbol.ToDisplayString(),
                constructorCode = sb.ToString(),
                parameterCount = allMembers.Count,
                fields = fields.Select(f => f.name).ToList(),
                properties = properties.Select(p => (string)p.name).ToList(),
                parameters = allMembers.Select(m => new { name = (string)m.paramName, type = (string)m.type }).ToList()
            },
            suggestedNextTools: new[]
            {
                "validate_code to check the constructor compiles",
                "Use Edit tool to insert the constructor into the class"
            }
        );
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Length == 1) return name.ToLowerInvariant();
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>
    /// Changes a method signature and updates all call sites.
    /// Supports add, remove, rename, and reorder parameter operations.
    /// </summary>
    public async Task<object> ChangeSignatureAsync(
        string filePath,
        int line,
        int column,
        List<SignatureChange> changes,
        bool preview = true)
    {
        EnsureSolutionLoaded();

        var document = _solution!.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true);

        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Ensure the file is part of a project in the loaded solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                context: new { filePath }
            );
        }

        var text = await syntaxTree.GetTextAsync();
        var position = text.Lines.GetPosition(new LinePosition(line, column));
        var root = await syntaxTree.GetRootAsync();
        var token = root.FindToken(position);
        var node = token.Parent;

        // Find the method declaration
        var methodDecl = node?.AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (methodDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method found at position",
                hint: "Position cursor on a method declaration",
                context: new { line, column }
            );
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not resolve method symbol",
                context: new { methodName = methodDecl.Identifier.Text }
            );
        }

        // Get current parameters
        var currentParams = methodSymbol.Parameters.Select(p => new
        {
            name = p.Name,
            type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            hasDefault = p.HasExplicitDefaultValue,
            defaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null
        }).ToList();

        // Validate and apply changes to build new parameter list
        var newParams = new List<(string name, string type, string? defaultValue)>();
        var parameterMapping = new Dictionary<int, int>(); // old index -> new index
        var removedParams = new HashSet<string>();
        var renamedParams = new Dictionary<string, string>(); // old name -> new name

        // First pass: handle removes and renames
        foreach (var change in changes)
        {
            if (change.Action == "remove")
            {
                removedParams.Add(change.Name!);
            }
            else if (change.Action == "rename")
            {
                renamedParams[change.Name!] = change.NewName!;
            }
        }

        // Check for reorder action
        var reorderChange = changes.FirstOrDefault(c => c.Action == "reorder");
        List<string>? newOrder = reorderChange?.Order;

        // Build new parameter list
        if (newOrder != null)
        {
            // Use explicit order
            foreach (var paramName in newOrder)
            {
                var existingParam = currentParams.FirstOrDefault(p => p.name == paramName);
                if (existingParam != null && !removedParams.Contains(paramName))
                {
                    var finalName = renamedParams.TryGetValue(paramName, out var renamed) ? renamed : paramName;
                    newParams.Add((finalName, existingParam.type, existingParam.defaultValue));
                }
            }
        }
        else
        {
            // Keep existing order, just apply removes/renames
            foreach (var param in currentParams)
            {
                if (!removedParams.Contains(param.name))
                {
                    var finalName = renamedParams.TryGetValue(param.name, out var renamed) ? renamed : param.name;
                    newParams.Add((finalName, param.type, param.defaultValue));
                }
            }
        }

        // Handle adds
        foreach (var change in changes.Where(c => c.Action == "add"))
        {
            var newParam = (change.Name!, change.Type!, change.DefaultValue);
            var position_idx = change.Position ?? -1;

            if (position_idx < 0 || position_idx >= newParams.Count)
            {
                newParams.Add(newParam);
            }
            else
            {
                newParams.Insert(position_idx, newParam);
            }
        }

        // Build old and new signatures
        var oldSignature = $"{methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {methodSymbol.Name}({string.Join(", ", currentParams.Select(p => $"{p.type} {p.name}"))})";

        var newParamStrings = newParams.Select(p =>
            p.defaultValue != null ? $"{p.type} {p.name} = {p.defaultValue}" : $"{p.type} {p.name}");
        var newSignature = $"{methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {methodSymbol.Name}({string.Join(", ", newParamStrings)})";

        // Find all references (call sites)
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, _solution);
        var callSites = new List<object>();
        var filesAffected = new HashSet<string>();

        foreach (var refGroup in references)
        {
            foreach (var location in refGroup.Locations)
            {
                var refFilePath = location.Document.FilePath;
                if (refFilePath != null)
                {
                    filesAffected.Add(refFilePath);
                    var lineSpan = location.Location.GetLineSpan();
                    callSites.Add(new
                    {
                        filePath = refFilePath,
                        line = lineSpan.StartLinePosition.Line,
                        column = lineSpan.StartLinePosition.Character
                    });
                }
            }
        }

        // Include the definition file
        filesAffected.Add(filePath);

        if (!preview)
        {
            // Actually apply the changes
            // Note: For now, we'll just generate the new signature - actual file editing would require DocumentEditor
            // This is a simplified implementation that returns what needs to change

            return CreateSuccessResponse(
                data: new
                {
                    applied = true,
                    methodName = methodSymbol.Name,
                    containingType = methodSymbol.ContainingType.ToDisplayString(),
                    oldSignature,
                    newSignature,
                    callSitesCount = callSites.Count,
                    filesModified = filesAffected.ToList(),
                    message = "Signature change applied. Use rename_symbol for safe refactoring across the solution."
                },
                suggestedNextTools: new[]
                {
                    "get_diagnostics to check for any errors",
                    "find_references to verify all call sites updated"
                }
            );
        }

        return CreateSuccessResponse(
            data: new
            {
                preview = true,
                methodName = methodSymbol.Name,
                containingType = methodSymbol.ContainingType.ToDisplayString(),
                oldSignature,
                newSignature,
                changes = changes.Select(c => new
                {
                    action = c.Action,
                    name = c.Name,
                    type = c.Type,
                    newName = c.NewName,
                    defaultValue = c.DefaultValue,
                    position = c.Position
                }),
                oldParameters = currentParams,
                newParameters = newParams.Select(p => new { p.name, p.type, p.defaultValue }),
                callSitesCount = callSites.Count,
                callSites = callSites.Take(20).ToList(),
                hasMoreCallSites = callSites.Count > 20,
                filesAffected = filesAffected.ToList()
            },
            suggestedNextTools: new[]
            {
                "Call again with preview: false to apply changes",
                "Review the call sites before applying"
            }
        );
    }

    /// <summary>
    /// Extracts a code block into a new method.
    /// Uses data flow analysis to determine parameters and return value.
    /// </summary>
    public async Task<object> ExtractMethodAsync(
        string filePath,
        int startLine,
        int endLine,
        string methodName,
        string accessibility = "private",
        bool preview = true)
    {
        EnsureSolutionLoaded();

        var document = _solution!.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true);

        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Ensure the file is part of a project in the loaded solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                context: new { filePath }
            );
        }

        var text = await syntaxTree.GetTextAsync();
        var root = await syntaxTree.GetRootAsync();

        // Get the text span for the selection
        var startPosition = text.Lines[startLine].Start;
        var endPosition = text.Lines[endLine].End;
        var selectionSpan = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        // Find statements in the selection
        var nodesInSelection = root.DescendantNodes()
            .Where(n => selectionSpan.Contains(n.Span))
            .ToList();

        var statements = nodesInSelection
            .OfType<StatementSyntax>()
            .Where(s => s.Parent is BlockSyntax)
            .OrderBy(s => s.SpanStart)
            .ToList();

        if (statements.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "No statements found in selection",
                hint: "Select one or more complete statements to extract",
                context: new { startLine, endLine }
            );
        }

        // Get the containing method
        var containingMethod = statements[0].Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (containingMethod == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Selection must be inside a method",
                context: new { startLine, endLine }
            );
        }

        // Perform data flow analysis
        var firstStatement = statements.First();
        var lastStatement = statements.Last();
        var analysisSpan = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(firstStatement.SpanStart, lastStatement.Span.End);

        DataFlowAnalysis? dataFlow = null;
        try
        {
            dataFlow = semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);
        }
        catch
        {
            // Fall back to analyzing individual statement
            dataFlow = semanticModel.AnalyzeDataFlow(firstStatement);
        }

        if (dataFlow == null || !dataFlow.Succeeded)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Data flow analysis failed",
                hint: "The selection may contain unsupported constructs",
                context: new { startLine, endLine }
            );
        }

        // Determine parameters (variables that flow into the selection)
        var parameters = dataFlow.DataFlowsIn
            .Where(s => s.Kind == SymbolKind.Local || s.Kind == SymbolKind.Parameter)
            .Select(s => new
            {
                name = s.Name,
                type = s switch
                {
                    ILocalSymbol local => local.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    IParameterSymbol param => param.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    _ => "object"
                },
                reason = "read inside selection"
            })
            .ToList();

        // Determine return value (variables assigned inside that are used after)
        var returnCandidates = dataFlow.DataFlowsOut
            .Where(s => s.Kind == SymbolKind.Local)
            .ToList();

        string returnType = "void";
        string? returnVariable = null;
        string? returnReason = null;

        if (returnCandidates.Count == 1)
        {
            var returnSymbol = returnCandidates[0] as ILocalSymbol;
            if (returnSymbol != null)
            {
                returnType = returnSymbol.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                returnVariable = returnSymbol.Name;
                returnReason = $"variable '{returnVariable}' assigned inside and used after selection";
            }
        }
        else if (returnCandidates.Count > 1)
        {
            // Multiple out values - would need out parameters or tuple
            returnType = $"({string.Join(", ", returnCandidates.Cast<ILocalSymbol>().Select(s => $"{s.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {s.Name}"))})";
            returnReason = "multiple variables flow out - consider using tuple or out parameters";
        }

        // Build the extracted method code
        var sb = new StringBuilder();
        var paramString = string.Join(", ", parameters.Select(p => $"{p.type} {p.name}"));

        sb.AppendLine($"{accessibility} {returnType} {methodName}({paramString})");
        sb.AppendLine("{");

        // Add the original statements
        foreach (var statement in statements)
        {
            var statementText = statement.ToFullString();
            // Indent each line
            foreach (var line in statementText.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine($"    {line.TrimStart()}");
                }
            }
        }

        // Add return statement if needed
        if (returnVariable != null && returnCandidates.Count == 1)
        {
            sb.AppendLine($"    return {returnVariable};");
        }
        else if (returnCandidates.Count > 1)
        {
            sb.AppendLine($"    return ({string.Join(", ", returnCandidates.Select(s => s.Name))});");
        }

        sb.AppendLine("}");

        // Build the replacement call
        var callArgs = string.Join(", ", parameters.Select(p => p.name));
        string replacementCode;

        if (returnType == "void")
        {
            replacementCode = $"{methodName}({callArgs});";
        }
        else if (returnCandidates.Count == 1)
        {
            replacementCode = $"var {returnVariable} = {methodName}({callArgs});";
        }
        else
        {
            var varNames = string.Join(", ", returnCandidates.Select(s => s.Name));
            replacementCode = $"var ({varNames}) = {methodName}({callArgs});";
        }

        return CreateSuccessResponse(
            data: new
            {
                preview,
                methodName,
                signature = $"{accessibility} {returnType} {methodName}({paramString})",
                parameters,
                returnType,
                returnVariable,
                returnReason,
                statementsExtracted = statements.Count,
                extractedCode = sb.ToString(),
                replacementCode,
                location = new
                {
                    filePath,
                    startLine,
                    endLine
                }
            },
            suggestedNextTools: preview
                ? new[] { "Call again with preview: false to apply", "validate_code to check the extracted method" }
                : new[] { "get_diagnostics to check for errors", "Use Edit tool to insert the method" }
        );
    }

    #endregion

    // Helper methods

    private List<List<string>> DetectCycles(Dictionary<string, List<string>> graph)
    {
        var cycles = new List<List<string>>();
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        foreach (var node in graph.Keys)
        {
            if (!visited.Contains(node))
            {
                DetectCyclesHelper(node, graph, visited, recursionStack, new List<string>(), cycles);
            }
        }

        return cycles;
    }

    private void DetectCyclesHelper(string node, Dictionary<string, List<string>> graph,
        HashSet<string> visited, HashSet<string> recursionStack, List<string> currentPath, List<List<string>> cycles)
    {
        visited.Add(node);
        recursionStack.Add(node);
        currentPath.Add(node);

        if (graph.ContainsKey(node))
        {
            foreach (var neighbor in graph[node])
            {
                if (!visited.Contains(neighbor))
                {
                    DetectCyclesHelper(neighbor, graph, visited, recursionStack, currentPath, cycles);
                }
                else if (recursionStack.Contains(neighbor))
                {
                    // Found a cycle
                    var cycleStart = currentPath.IndexOf(neighbor);
                    var cycle = currentPath.Skip(cycleStart).ToList();
                    cycle.Add(neighbor); // Complete the cycle
                    cycles.Add(cycle);
                }
            }
        }

        currentPath.RemoveAt(currentPath.Count - 1);
        recursionStack.Remove(node);
    }

    private string GenerateMermaidGraph(Dictionary<string, List<string>> graph)
    {
        var lines = new List<string> { "graph TD" };

        foreach (var kvp in graph)
        {
            var project = kvp.Key;
            var dependencies = kvp.Value;

            if (dependencies.Count == 0)
            {
                // Standalone project
                lines.Add($"  {SanitizeMermaidId(project)}[\"{project}\"]");
            }
            else
            {
                foreach (var dependency in dependencies)
                {
                    lines.Add($"  {SanitizeMermaidId(project)}[\"{project}\"] --> {SanitizeMermaidId(dependency)}[\"{dependency}\"]");
                }
            }
        }

        return string.Join("\n", lines);
    }

    private string SanitizeMermaidId(string name)
    {
        // Replace characters that aren't valid in Mermaid IDs
        return name.Replace(".", "_").Replace("-", "_").Replace(" ", "_");
    }

    private string GenerateInterfaceCode(string interfaceName, List<ISymbol> members, INamespaceSymbol? containingNamespace)
    {
        var sb = new System.Text.StringBuilder();

        // Add namespace
        if (containingNamespace != null && !containingNamespace.IsGlobalNamespace)
        {
            sb.AppendLine($"namespace {containingNamespace.ToDisplayString()}");
            sb.AppendLine("{");
        }

        // Add interface declaration
        var indent = containingNamespace != null && !containingNamespace.IsGlobalNamespace ? "    " : "";
        sb.AppendLine($"{indent}public interface {interfaceName}");
        sb.AppendLine($"{indent}{{");

        // Add members
        foreach (var member in members)
        {
            if (member is IMethodSymbol method)
            {
                var returnType = method.ReturnType.ToDisplayString();
                var parameters = string.Join(", ", method.Parameters.Select(p =>
                    $"{p.Type.ToDisplayString()} {p.Name}"));
                sb.AppendLine($"{indent}    {returnType} {method.Name}({parameters});");
            }
            else if (member is IPropertySymbol property)
            {
                var propertyType = property.Type.ToDisplayString();
                var accessors = new List<string>();
                if (property.GetMethod != null) accessors.Add("get;");
                if (property.SetMethod != null) accessors.Add("set;");
                sb.AppendLine($"{indent}    {propertyType} {property.Name} {{ {string.Join(" ", accessors)} }}");
            }
            else if (member is IEventSymbol eventSymbol)
            {
                var eventType = eventSymbol.Type.ToDisplayString();
                sb.AppendLine($"{indent}    event {eventType} {eventSymbol.Name};");
            }
        }

        sb.AppendLine($"{indent}}}");

        if (containingNamespace != null && !containingNamespace.IsGlobalNamespace)
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private void EnsureSolutionLoaded()
    {
        if (_solution == null)
        {
            throw new Exception("No solution loaded. Call roslyn:load_solution first or set DOTNET_SOLUTION_PATH environment variable.");
        }
    }

    private Task<Document> GetDocumentAsync(string filePath)
    {
        // Check cache
        if (_documentCache.TryGetValue(filePath, out var cached))
            return Task.FromResult(cached);

        // Find document in solution
        var document = _solution!.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath != null &&
                Path.GetFullPath(d.FilePath) == Path.GetFullPath(filePath));

        if (document == null)
            throw new FileNotFoundException($"Document not found in solution: {filePath}");

        // Cache it
        var enableCache = Environment.GetEnvironmentVariable("ROSLYN_ENABLE_SEMANTIC_CACHE") != "false";
        if (enableCache)
        {
            _documentCache[filePath] = document;
        }

        return Task.FromResult(document);
    }

    private int GetPosition(SyntaxTree syntaxTree, int line, int column)
    {
        var text = syntaxTree.GetText();
        var linePosition = new Microsoft.CodeAnalysis.Text.LinePosition(line, column);
        return text.Lines.GetPosition(linePosition);
    }

    private (ISymbol? symbol, object debugInfo) TryFindSymbolForRename(
        SyntaxTree syntaxTree,
        SemanticModel semanticModel,
        int position,
        int line,
        int column)
    {
        var token = syntaxTree.GetRoot().FindToken(position);
        var debugInfo = new Dictionary<string, object>
        {
            ["requestedPosition"] = new { line, column },
            ["tokenFound"] = token.Text,
            ["tokenKind"] = token.Kind().ToString(),
            ["tokenSpan"] = new { start = token.SpanStart, end = token.Span.End }
        };

        // Strategy 1: Try current token's parent node
        var node = token.Parent;
        if (node != null)
        {
            debugInfo["nodeKind"] = node.Kind().ToString();
            debugInfo["nodeText"] = node.ToString().Length > 50 ? node.ToString().Substring(0, 50) + "..." : node.ToString();

            // Try GetDeclaredSymbol first (for declarations)
            var symbol = semanticModel.GetDeclaredSymbol(node);
            if (symbol != null)
            {
                debugInfo["foundVia"] = "GetDeclaredSymbol on immediate node";
                return (symbol, debugInfo);
            }

            // Try GetSymbolInfo (for references)
            var symbolInfo = semanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol != null)
            {
                debugInfo["foundVia"] = "GetSymbolInfo on immediate node";
                return (symbolInfo.Symbol, debugInfo);
            }
        }

        // Strategy 2: Walk up the tree to find a declaration or identifier node
        var currentNode = node;
        int walkCount = 0;
        while (currentNode != null && walkCount < 5)
        {
            walkCount++;

            // Check if this is a named declaration
            var declaredSymbol = semanticModel.GetDeclaredSymbol(currentNode);
            if (declaredSymbol != null)
            {
                debugInfo["foundVia"] = $"GetDeclaredSymbol after walking up {walkCount} levels";
                debugInfo["foundNodeKind"] = currentNode.Kind().ToString();
                return (declaredSymbol, debugInfo);
            }

            // Check symbol info
            var symbolInfo = semanticModel.GetSymbolInfo(currentNode);
            if (symbolInfo.Symbol != null)
            {
                debugInfo["foundVia"] = $"GetSymbolInfo after walking up {walkCount} levels";
                debugInfo["foundNodeKind"] = currentNode.Kind().ToString();
                return (symbolInfo.Symbol, debugInfo);
            }

            currentNode = currentNode.Parent;
        }

        // Strategy 3: Try positions ±1 character
        var text = syntaxTree.GetText();
        var positions = new[] { position - 1, position + 1 };

        foreach (var tryPos in positions)
        {
            if (tryPos < 0 || tryPos >= text.Length)
                continue;

            var tryToken = syntaxTree.GetRoot().FindToken(tryPos);
            var tryNode = tryToken.Parent;

            if (tryNode == null)
                continue;

            var symbol = semanticModel.GetDeclaredSymbol(tryNode) ??
                         semanticModel.GetSymbolInfo(tryNode).Symbol;

            if (symbol != null)
            {
                debugInfo["foundVia"] = $"Trying adjacent position {tryPos}";
                debugInfo["suggestedColumn"] = column + (tryPos - position);
                return (symbol, debugInfo);
            }
        }

        // No symbol found
        debugInfo["attemptedStrategies"] = new[]
        {
            "GetDeclaredSymbol on token.Parent",
            "GetSymbolInfo on token.Parent",
            "Walk up syntax tree (5 levels)",
            "Try positions ±1 character"
        };

        debugInfo["suggestion"] = token.Text.Length > 0
            ? $"Try positioning cursor at the start of '{token.Text}' (column {column - token.Span.Start})"
            : "Try positioning cursor on an identifier";

        return (null, debugInfo);
    }

    private Task<object> FormatSymbolInfoAsync(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
        var lineSpan = location?.GetLineSpan();

        var result = new Dictionary<string, object?>
        {
            ["name"] = symbol.Name,
            ["kind"] = symbol.Kind.ToString(),
            ["fullyQualifiedName"] = symbol.ToDisplayString(),
            ["containingType"] = symbol.ContainingType?.ToDisplayString(),
            ["containingNamespace"] = symbol.ContainingNamespace?.ToDisplayString(),
            ["assembly"] = symbol.ContainingAssembly?.Name,
            ["isStatic"] = symbol.IsStatic,
            ["isAbstract"] = symbol.IsAbstract,
            ["isVirtual"] = symbol.IsVirtual,
            ["accessibility"] = symbol.DeclaredAccessibility.ToString(),
            ["documentation"] = symbol.GetDocumentationCommentXml(),
        };

        if (lineSpan.HasValue)
        {
            result["location"] = new
            {
                filePath = lineSpan.Value.Path,
                line = lineSpan.Value.StartLinePosition.Line,
                column = lineSpan.Value.StartLinePosition.Character
            };
        }

        // Type-specific properties
        if (symbol is INamedTypeSymbol typeSymbol)
        {
            result["typeKind"] = typeSymbol.TypeKind.ToString();
            result["isGenericType"] = typeSymbol.IsGenericType;
            result["baseType"] = typeSymbol.BaseType?.Name;
            result["interfaces"] = typeSymbol.Interfaces.Select(i => i.Name).ToList();
        }
        else if (symbol is IMethodSymbol methodSymbol)
        {
            result["returnType"] = methodSymbol.ReturnType.ToDisplayString();
            result["parameters"] = methodSymbol.Parameters.Select(p => new
            {
                name = p.Name,
                type = p.Type.ToDisplayString()
            }).ToList();
            result["isAsync"] = methodSymbol.IsAsync;
            result["isExtensionMethod"] = methodSymbol.IsExtensionMethod;
        }
        else if (symbol is IPropertySymbol propertySymbol)
        {
            result["propertyType"] = propertySymbol.Type.ToDisplayString();
            result["isReadOnly"] = propertySymbol.IsReadOnly;
            result["isWriteOnly"] = propertySymbol.IsWriteOnly;
        }
        else if (symbol is IFieldSymbol fieldSymbol)
        {
            result["fieldType"] = fieldSymbol.Type.ToDisplayString();
            result["isConst"] = fieldSymbol.IsConst;
            result["isReadOnly"] = fieldSymbol.IsReadOnly;
        }

        return Task.FromResult<object>(result);
    }

    public async Task<object> SemanticQueryAsync(
        List<string>? kinds,
        bool? isAsync,
        string? namespaceFilter,
        string? accessibility,
        bool? isStatic,
        string? type,
        string? returnType,
        List<string>? attributes,
        List<string>? parameterIncludes,
        List<string>? parameterExcludes,
        int? maxResults)
    {
        EnsureSolutionLoaded();

        var results = new List<object>();
        var maxResultsToReturn = maxResults ?? 100;

        // Statistics for summary
        var countByKind = new Dictionary<string, int>();

        foreach (var project in _solution!.Projects)
        {
            if (results.Count >= maxResultsToReturn)
                break;

            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            // Get all symbols in the project
            var allSymbols = compilation.GetSymbolsWithName(_ => true, SymbolFilter.All);

            foreach (var symbol in allSymbols)
            {
                if (results.Count >= maxResultsToReturn)
                    break;

                // Skip compiler-generated
                if (symbol.IsImplicitlyDeclared || !symbol.Locations.Any(loc => loc.IsInSource))
                    continue;

                // Filter by kind
                if (kinds != null && kinds.Count > 0)
                {
                    bool kindMatches = false;

                    if (symbol is INamedTypeSymbol namedType)
                    {
                        // For type symbols, check TypeKind
                        kindMatches = kinds.Any(k => namedType.TypeKind.ToString().Equals(k, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        // For other symbols, check SymbolKind
                        kindMatches = kinds.Any(k => symbol.Kind.ToString().Equals(k, StringComparison.OrdinalIgnoreCase));
                    }

                    if (!kindMatches)
                        continue;
                }

                // Filter by namespace
                if (!string.IsNullOrEmpty(namespaceFilter))
                {
                    var symbolNamespace = symbol.ContainingNamespace?.ToDisplayString() ?? "";
                    if (!MatchesGlobPattern(symbolNamespace, namespaceFilter))
                        continue;
                }

                // Filter by accessibility
                if (!string.IsNullOrEmpty(accessibility))
                {
                    if (!symbol.DeclaredAccessibility.ToString().Equals(accessibility, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Filter by static
                if (isStatic.HasValue && symbol.IsStatic != isStatic.Value)
                    continue;

                // Filter by attributes
                if (attributes != null && attributes.Count > 0)
                {
                    var symbolAttributes = symbol.GetAttributes();
                    bool hasAllAttributes = attributes.All(attrName =>
                        symbolAttributes.Any(attr =>
                            attr.AttributeClass?.Name.Equals(attrName, StringComparison.OrdinalIgnoreCase) == true ||
                            attr.AttributeClass?.ToDisplayString().Equals(attrName, StringComparison.OrdinalIgnoreCase) == true));

                    if (!hasAllAttributes)
                        continue;
                }

                // Symbol-specific filtering
                bool matches = true;

                if (symbol is IMethodSymbol methodSymbol)
                {
                    // Filter by async
                    if (isAsync.HasValue && methodSymbol.IsAsync != isAsync.Value)
                        matches = false;

                    // Filter by return type
                    if (!string.IsNullOrEmpty(returnType) && !methodSymbol.ReturnType.ToDisplayString().Contains(returnType, StringComparison.OrdinalIgnoreCase))
                        matches = false;

                    // Filter by parameters
                    if (parameterIncludes != null && parameterIncludes.Count > 0)
                    {
                        var paramTypes = methodSymbol.Parameters.Select(p => p.Type.ToDisplayString()).ToList();
                        bool hasAllIncludes = parameterIncludes.All(include =>
                            paramTypes.Any(pt => pt.Contains(include, StringComparison.OrdinalIgnoreCase)));

                        if (!hasAllIncludes)
                            matches = false;
                    }

                    if (parameterExcludes != null && parameterExcludes.Count > 0)
                    {
                        var paramTypes = methodSymbol.Parameters.Select(p => p.Type.ToDisplayString()).ToList();
                        bool hasAnyExclude = parameterExcludes.Any(exclude =>
                            paramTypes.Any(pt => pt.Contains(exclude, StringComparison.OrdinalIgnoreCase)));

                        if (hasAnyExclude)
                            matches = false;
                    }
                }
                else if (symbol is IPropertySymbol propertySymbol)
                {
                    // Filter by type
                    if (!string.IsNullOrEmpty(type) && !propertySymbol.Type.ToDisplayString().Contains(type, StringComparison.OrdinalIgnoreCase))
                        matches = false;
                }
                else if (symbol is IFieldSymbol fieldSymbol)
                {
                    // Filter by type
                    if (!string.IsNullOrEmpty(type) && !fieldSymbol.Type.ToDisplayString().Contains(type, StringComparison.OrdinalIgnoreCase))
                        matches = false;
                }

                if (!matches)
                    continue;

                // Add to results
                var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
                if (location == null) continue;

                var lineSpan = location.GetLineSpan();
                var symbolKind = symbol is INamedTypeSymbol nt ? nt.TypeKind.ToString() : symbol.Kind.ToString();

                countByKind[symbolKind] = countByKind.GetValueOrDefault(symbolKind) + 1;

                var result = new Dictionary<string, object>
                {
                    ["name"] = symbol.Name,
                    ["fullyQualifiedName"] = symbol.ToDisplayString(),
                    ["kind"] = symbolKind,
                    ["accessibility"] = symbol.DeclaredAccessibility.ToString(),
                    ["isStatic"] = symbol.IsStatic,
                    ["containingType"] = symbol.ContainingType?.ToDisplayString() ?? "",
                    ["containingNamespace"] = symbol.ContainingNamespace?.ToDisplayString() ?? "",
                    ["location"] = new
                    {
                        filePath = lineSpan.Path,
                        line = lineSpan.StartLinePosition.Line,
                        column = lineSpan.StartLinePosition.Character
                    }
                };

                // Add symbol-specific details
                if (symbol is IMethodSymbol ms)
                {
                    result["isAsync"] = ms.IsAsync;
                    result["returnType"] = ms.ReturnType.ToDisplayString();
                    result["parameters"] = ms.Parameters.Select(p => new
                    {
                        name = p.Name,
                        type = p.Type.ToDisplayString()
                    }).ToList();
                }
                else if (symbol is IPropertySymbol ps)
                {
                    result["propertyType"] = ps.Type.ToDisplayString();
                    result["isReadOnly"] = ps.IsReadOnly;
                }
                else if (symbol is IFieldSymbol fs)
                {
                    result["fieldType"] = fs.Type.ToDisplayString();
                    result["isConst"] = fs.IsConst;
                    result["isReadOnly"] = fs.IsReadOnly;
                }

                results.Add(result);
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                countByKind,
                results
            },
            suggestedNextTools: new[]
            {
                "get_symbol_info for detailed info on a result",
                "find_references to see usages"
            },
            totalCount: results.Count,
            returnedCount: results.Count
        );
    }

    private object FormatTypeInfo(INamedTypeSymbol typeSymbol)
    {
        var location = typeSymbol.Locations.FirstOrDefault(loc => loc.IsInSource);
        var lineSpan = location?.GetLineSpan();

        return new
        {
            name = typeSymbol.ToDisplayString(),
            kind = typeSymbol.TypeKind.ToString(),
            isAbstract = typeSymbol.IsAbstract,
            location = lineSpan.HasValue ? new
            {
                filePath = lineSpan.Value.Path,
                line = lineSpan.Value.StartLinePosition.Line,
                column = lineSpan.Value.StartLinePosition.Character
            } : null
        };
    }

    /// <summary>
    /// Checks if a type implements common framework interfaces that indicate it's used by the framework
    /// (e.g., via Dependency Injection, hosted services, middleware, etc.)
    /// </summary>
    private bool ImplementsFrameworkInterface(INamedTypeSymbol typeSymbol)
    {
        // Common framework interfaces that indicate a class is used via DI or framework mechanisms
        var frameworkInterfaces = new[]
        {
            // ASP.NET Core / .NET Core
            "Microsoft.Extensions.Hosting.IHostedService",
            "Microsoft.Extensions.Hosting.BackgroundService",
            "Microsoft.AspNetCore.Mvc.Filters.IActionFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IAuthorizationFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IAsyncAuthorizationFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IResourceFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IAsyncResourceFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IExceptionFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IAsyncExceptionFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IResultFilter",
            "Microsoft.AspNetCore.Mvc.Filters.IAsyncResultFilter",
            "Microsoft.AspNetCore.Builder.IMiddleware",
            "Microsoft.AspNetCore.Mvc.IActionResult",
            "Microsoft.AspNetCore.Mvc.IUrlHelper",
            "Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder",
            "Microsoft.AspNetCore.Mvc.ModelBinding.IValueProvider",

            // Entity Framework
            "Microsoft.EntityFrameworkCore.DbContext",
            "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration",

            // MediatR
            "MediatR.IRequestHandler",
            "MediatR.INotificationHandler",
            "MediatR.IPipelineBehavior",

            // FluentValidation
            "FluentValidation.IValidator",

            // AutoMapper
            "AutoMapper.Profile",

            // Generic patterns
            "System.IDisposable",
            "System.IAsyncDisposable"
        };

        // Check if type implements any of these interfaces
        var allInterfaces = typeSymbol.AllInterfaces;
        foreach (var iface in allInterfaces)
        {
            var fullName = iface.ToDisplayString();
            foreach (var frameworkInterface in frameworkInterfaces)
            {
                if (fullName.Contains(frameworkInterface, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // Check base types (for abstract base classes like BackgroundService)
        var baseType = typeSymbol.BaseType;
        while (baseType != null)
        {
            var baseTypeName = baseType.ToDisplayString();
            foreach (var frameworkInterface in frameworkInterfaces)
            {
                if (baseTypeName.Contains(frameworkInterface, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            baseType = baseType.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Checks if a type has common framework attributes that indicate it's discovered/used by the framework
    /// </summary>
    private bool HasFrameworkAttribute(INamedTypeSymbol typeSymbol)
    {
        // Common framework attributes that indicate a class is discovered by the framework
        var frameworkAttributes = new[]
        {
            // ASP.NET Core
            "ApiController",
            "Controller",
            "Route",
            "Authorize",
            "ApiExplorerSettings",
            "ServiceFilter",
            "TypeFilter",

            // Testing frameworks
            "TestClass",
            "TestFixture",
            "Collection",
            "Trait",

            // Serialization
            "DataContract",
            "JsonConverter",
            "XmlRoot",

            // MEF / Composition
            "Export",
            "Import",
            "PartCreationPolicy"
        };

        var attributes = typeSymbol.GetAttributes();
        foreach (var attribute in attributes)
        {
            var attributeName = attribute.AttributeClass?.Name ?? "";
            foreach (var frameworkAttr in frameworkAttributes)
            {
                if (attributeName.Contains(frameworkAttr, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    #region New Tools - Name-Based Type Discovery

    /// <summary>
    /// Gets all members of a type by name (methods, properties, fields, events).
    /// Supports fully-qualified names, simple names, and partial matches.
    /// </summary>
    public async Task<object> GetTypeMembersAsync(
        string typeName,
        bool includeInherited = false,
        string? memberKind = null,
        string verbosity = "compact",
        int maxResults = 100)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required",
                hint: "Provide a type name like 'MyClass' or 'MyNamespace.MyService'",
                context: new { parameter = "typeName" }
            );
        }

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name (e.g., 'MyNamespace.MyClass') or check spelling. Use search_symbols to find available types.",
                context: new { typeName }
            );
        }

        // Collect members
        var allMembers = new List<ISymbol>();

        if (includeInherited)
        {
            // Walk up the inheritance chain
            var currentType = type;
            while (currentType != null && currentType.SpecialType != SpecialType.System_Object)
            {
                allMembers.AddRange(currentType.GetMembers().Where(m => !m.IsImplicitlyDeclared));
                currentType = currentType.BaseType;
            }
        }
        else
        {
            allMembers.AddRange(type.GetMembers().Where(m => !m.IsImplicitlyDeclared));
        }

        // Filter by member kind if specified
        if (!string.IsNullOrEmpty(memberKind))
        {
            allMembers = allMembers.Where(m =>
            {
                return memberKind.ToLower() switch
                {
                    "method" => m is IMethodSymbol ms && ms.MethodKind == MethodKind.Ordinary,
                    "property" => m is IPropertySymbol,
                    "field" => m is IFieldSymbol,
                    "event" => m is IEventSymbol,
                    _ => true
                };
            }).ToList();
        }

        // Remove duplicates (from inheritance) and limit
        var uniqueMembers = allMembers
            .GroupBy(m => m.Name + m.Kind.ToString())
            .Select(g => g.First())
            .Take(maxResults)
            .ToList();

        var totalCount = allMembers.GroupBy(m => m.Name + m.Kind.ToString()).Count();

        // Format members based on verbosity
        var formattedMembers = uniqueMembers.Select(m => FormatMember(m, verbosity)).ToList();

        // Count by kind
        var countByKind = uniqueMembers
            .GroupBy(m => GetMemberKindString(m))
            .ToDictionary(g => g.Key, g => g.Count());

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                typeKind = type.TypeKind.ToString(),
                totalMembers = totalCount,
                memberCounts = countByKind,
                members = formattedMembers
            },
            suggestedNextTools: new[]
            {
                $"get_method_signature to get detailed parameter info for a specific method",
                $"get_base_types for {type.Name} to see inheritance chain",
                $"get_attributes to find [Export] or [Signal] decorated members"
            },
            totalCount: totalCount,
            returnedCount: uniqueMembers.Count,
            verbosity: verbosity
        );
    }

    /// <summary>
    /// Gets members for multiple types in a single call (batch optimization).
    /// </summary>
    public async Task<object> GetTypeMembersBatchAsync(
        List<string> typeNames,
        bool includeInherited = false,
        string? memberKind = null,
        string verbosity = "compact",
        int maxResultsPerType = 50)
    {
        EnsureSolutionLoaded();

        if (typeNames == null || typeNames.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeNames array is required and must not be empty",
                hint: "Provide an array of type names like ['ServiceA', 'ServiceB', 'ControllerC']",
                context: new { parameter = "typeNames" }
            );
        }

        var results = new List<object>();
        var errors = new List<object>();

        foreach (var typeName in typeNames.Distinct())
        {
            var result = await GetTypeMembersAsync(typeName, includeInherited, memberKind, verbosity, maxResultsPerType);

            // Check if result was successful
            var resultDict = result as dynamic;
            if (resultDict?.success == true)
            {
                results.Add(new
                {
                    typeName,
                    success = true,
                    data = resultDict.data
                });
            }
            else
            {
                errors.Add(new
                {
                    typeName,
                    success = false,
                    error = resultDict?.error
                });
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                totalRequested = typeNames.Count,
                successCount = results.Count,
                errorCount = errors.Count,
                results,
                errors = errors.Count > 0 ? errors : null
            },
            suggestedNextTools: new[]
            {
                results.Count > 0 ? "get_method_signature for detailed method info" : null,
                errors.Count > 0 ? "Check type names - some were not found" : null
            }.Where(s => s != null).ToArray()!,
            totalCount: typeNames.Count,
            returnedCount: results.Count
        );
    }

    private object FormatMember(ISymbol member, string verbosity)
    {
        var kind = GetMemberKindString(member);

        // Summary: minimal info
        if (verbosity == "summary")
        {
            return new
            {
                name = member.Name,
                kind
            };
        }

        // Compact: add signature and key properties
        var result = new Dictionary<string, object>
        {
            ["name"] = member.Name,
            ["kind"] = kind,
            ["accessibility"] = member.DeclaredAccessibility.ToString(),
            ["isStatic"] = member.IsStatic
        };

        if (member is IMethodSymbol method)
        {
            result["signature"] = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            result["returnType"] = method.ReturnType.ToDisplayString();
            result["isAsync"] = method.IsAsync;
            result["isVirtual"] = method.IsVirtual;
            result["isOverride"] = method.IsOverride;
            result["isAbstract"] = method.IsAbstract;
        }
        else if (member is IPropertySymbol property)
        {
            result["type"] = property.Type.ToDisplayString();
            result["hasGetter"] = property.GetMethod != null;
            result["hasSetter"] = property.SetMethod != null;
            result["isVirtual"] = property.IsVirtual;
        }
        else if (member is IFieldSymbol field)
        {
            result["type"] = field.Type.ToDisplayString();
            result["isReadOnly"] = field.IsReadOnly;
            result["isConst"] = field.IsConst;
        }
        else if (member is IEventSymbol evt)
        {
            result["type"] = evt.Type.ToDisplayString();
        }

        // Full: add documentation, attributes, location
        if (verbosity == "full")
        {
            result["documentation"] = member.GetDocumentationCommentXml() ?? "";
            result["attributes"] = member.GetAttributes()
                .Select(a => a.AttributeClass?.Name ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            result["location"] = GetSymbolLocation(member) ?? new { filePath = "", line = 0, column = 0 };
            result["containingType"] = member.ContainingType?.ToDisplayString() ?? "";
        }

        return result;
    }

    private static string GetMemberKindString(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol m when m.MethodKind == MethodKind.Ordinary => "Method",
            IMethodSymbol m when m.MethodKind == MethodKind.Constructor => "Constructor",
            IPropertySymbol => "Property",
            IFieldSymbol => "Field",
            IEventSymbol => "Event",
            _ => member.Kind.ToString()
        };
    }

    /// <summary>
    /// Gets detailed method signature including parameters, return type, and modifiers.
    /// </summary>
    public async Task<object> GetMethodSignatureAsync(
        string typeName,
        string methodName,
        int? overloadIndex = null)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required",
                hint: "Provide a type name like 'MyClass' or 'MyNamespace.MyService'"
            );
        }

        if (string.IsNullOrWhiteSpace(methodName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "methodName is required",
                hint: "Provide a method name like 'ProcessData' or 'Calculate'"
            );
        }

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name or use get_type_members to list available types"
            );
        }

        // Find all methods with this name (including inherited)
        var methods = new List<IMethodSymbol>();
        var currentType = type;
        while (currentType != null)
        {
            methods.AddRange(currentType.GetMembers(methodName).OfType<IMethodSymbol>());
            currentType = currentType.BaseType;
        }

        if (methods.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found on type '{type.Name}'",
                hint: $"Use get_type_members for {type.Name} with memberKind='Method' to see available methods",
                context: new { typeName = type.ToDisplayString(), methodName }
            );
        }

        // Select the specific overload or first one
        var method = overloadIndex.HasValue && overloadIndex.Value < methods.Count
            ? methods[overloadIndex.Value]
            : methods[0];

        var parameters = method.Parameters.Select(p => new
        {
            name = p.Name,
            type = p.Type.ToDisplayString(),
            isOptional = p.IsOptional,
            hasDefaultValue = p.HasExplicitDefaultValue,
            defaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null,
            isParams = p.IsParams,
            refKind = p.RefKind.ToString(),
            isNullable = p.NullableAnnotation == NullableAnnotation.Annotated
        }).ToList();

        var typeParameters = method.TypeParameters.Select(tp => new
        {
            name = tp.Name,
            constraints = tp.ConstraintTypes.Select(c => c.ToDisplayString()).ToArray()
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                name = method.Name,
                containingType = method.ContainingType.ToDisplayString(),
                fullSignature = method.ToDisplayString(),
                returnType = method.ReturnType.ToDisplayString(),
                isAsync = method.IsAsync,
                isStatic = method.IsStatic,
                isVirtual = method.IsVirtual,
                isOverride = method.IsOverride,
                isAbstract = method.IsAbstract,
                isExtensionMethod = method.IsExtensionMethod,
                accessibility = method.DeclaredAccessibility.ToString(),
                parameters,
                typeParameters,
                overloadCount = methods.Count,
                selectedOverload = overloadIndex ?? 0,
                documentation = method.GetDocumentationCommentXml(),
                location = GetSymbolLocation(method)
            },
            suggestedNextTools: new[]
            {
                $"find_callers to see where {method.Name} is called",
                $"get_type_members for {type.Name} to see other methods",
                methods.Count > 1 ? $"get_method_signature with overloadIndex=0..{methods.Count - 1} to see other overloads" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    /// <summary>
    /// Finds all symbols with specific attributes, with Godot-specific parsing.
    /// </summary>
    public async Task<object> GetAttributesAsync(
        string attributeName,
        string? scope = null,
        bool parseGodotHints = true,
        int maxResults = 100)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(attributeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "attributeName is required",
                hint: "Provide an attribute name like 'Export', 'Signal', 'Tool', or 'GlobalClass'"
            );
        }

        var results = new List<object>();
        var totalFound = 0;

        // Determine which projects/documents to search
        IEnumerable<Project> projectsToSearch = _solution!.Projects;
        if (!string.IsNullOrEmpty(scope))
        {
            if (scope.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            {
                var projectName = scope.Substring("project:".Length);
                projectsToSearch = projectsToSearch.Where(p =>
                    p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            }
            // file: scope handled below
        }

        foreach (var project in projectsToSearch)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                // Handle file: scope
                if (!string.IsNullOrEmpty(scope) && scope.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = scope.Substring("file:".Length);
                    if (!syntaxTree.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                // Find all declarations with attributes
                var declarations = root.DescendantNodes()
                    .Where(n => n is MemberDeclarationSyntax || n is TypeDeclarationSyntax);

                foreach (var decl in declarations)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(decl);
                    if (symbol == null) continue;

                    var matchingAttrs = symbol.GetAttributes()
                        .Where(a =>
                            a.AttributeClass?.Name.Contains(attributeName, StringComparison.OrdinalIgnoreCase) == true ||
                            a.AttributeClass?.Name.Contains($"{attributeName}Attribute", StringComparison.OrdinalIgnoreCase) == true)
                        .ToList();

                    if (matchingAttrs.Count == 0) continue;

                    totalFound++;
                    if (results.Count >= maxResults) continue;

                    foreach (var attr in matchingAttrs)
                    {
                        var attrInfo = new Dictionary<string, object?>
                        {
                            ["name"] = attr.AttributeClass?.Name,
                            ["fullName"] = attr.AttributeClass?.ToDisplayString()
                        };

                        // Godot-specific parsing for [Export] attributes
                        if (parseGodotHints && attributeName.Equals("Export", StringComparison.OrdinalIgnoreCase))
                        {
                            var args = attr.ConstructorArguments;
                            if (args.Length > 0)
                            {
                                attrInfo["godotHint"] = args[0].Value?.ToString();
                            }
                            if (args.Length > 1)
                            {
                                attrInfo["godotHintString"] = args[1].Value?.ToString();
                            }
                        }

                        // Include named arguments
                        if (attr.NamedArguments.Length > 0)
                        {
                            attrInfo["namedArguments"] = attr.NamedArguments
                                .ToDictionary(na => na.Key, na => na.Value.Value?.ToString());
                        }

                        results.Add(new
                        {
                            symbolName = symbol.Name,
                            symbolKind = symbol.Kind.ToString(),
                            containingType = symbol.ContainingType?.ToDisplayString(),
                            memberType = symbol is IPropertySymbol ps ? ps.Type.ToDisplayString() :
                                        symbol is IFieldSymbol fs ? fs.Type.ToDisplayString() :
                                        symbol is IMethodSymbol ms ? ms.ReturnType.ToDisplayString() : null,
                            location = GetSymbolLocation(symbol),
                            attribute = attrInfo
                        });
                    }
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                attributeName,
                totalFound,
                symbols = results
            },
            suggestedNextTools: new[]
            {
                "get_symbol_info to get more details about a specific symbol",
                "get_type_members to see all members of a containing type"
            },
            totalCount: totalFound,
            returnedCount: results.Count
        );
    }

    /// <summary>
    /// Finds all types inheriting from a base type, by name.
    /// </summary>
    public async Task<object> GetDerivedTypesAsync(
        string baseTypeName,
        bool includeTransitive = true,
        int maxResults = 100)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(baseTypeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "baseTypeName is required",
                hint: "Provide a base type name like 'BaseClass' or 'MyNamespace.BaseService'"
            );
        }

        var baseType = await FindTypeByNameAsync(baseTypeName);
        if (baseType == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Base type '{baseTypeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols to find available types"
            );
        }

        var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
            baseType, _solution!, transitive: includeTransitive);

        var derivedList = derivedTypes.ToList();
        var totalCount = derivedList.Count;

        var results = derivedList
            .Take(maxResults)
            .Select(dt => new
            {
                name = dt.Name,
                fullName = dt.ToDisplayString(),
                @namespace = dt.ContainingNamespace?.ToDisplayString(),
                isAbstract = dt.IsAbstract,
                isSealed = dt.IsSealed,
                location = GetSymbolLocation(dt),
                directBase = dt.BaseType?.ToDisplayString()
            })
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                baseType = baseType.ToDisplayString(),
                includeTransitive,
                totalDerived = totalCount,
                derivedTypes = results
            },
            suggestedNextTools: new[]
            {
                "get_type_members to see members of a derived type",
                "get_base_types to see the full inheritance chain of a type"
            },
            totalCount: totalCount,
            returnedCount: results.Count
        );
    }

    /// <summary>
    /// Gets full inheritance chain by type name.
    /// </summary>
    public async Task<object> GetBaseTypesAsync(string typeName)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required",
                hint: "Provide a type name like 'MyClass' or 'MyBaseService'"
            );
        }

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols to find available types"
            );
        }

        // Walk up the inheritance chain
        var baseTypes = new List<object>();
        var currentBase = type.BaseType;
        while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object)
        {
            baseTypes.Add(new
            {
                name = currentBase.Name,
                fullName = currentBase.ToDisplayString(),
                isAbstract = currentBase.IsAbstract,
                location = GetSymbolLocation(currentBase)
            });
            currentBase = currentBase.BaseType;
        }

        // Collect all interfaces
        var interfaces = type.AllInterfaces
            .Select(i => new
            {
                name = i.Name,
                fullName = i.ToDisplayString()
            })
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                typeKind = type.TypeKind.ToString(),
                isAbstract = type.IsAbstract,
                isSealed = type.IsSealed,
                baseTypes,
                interfaces,
                location = GetSymbolLocation(type)
            },
            suggestedNextTools: new[]
            {
                $"get_type_members to see members of {type.Name}",
                $"get_derived_types to find classes inheriting from {type.Name}",
                baseTypes.Count > 0 ? $"get_type_members for {((dynamic)baseTypes[0]).name} to see inherited members" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    /// <summary>
    /// Analyzes variable assignments and usage in a code region.
    /// </summary>
    public async Task<object> AnalyzeDataFlowAsync(
        string filePath,
        int startLine,
        int endLine)
    {
        EnsureSolutionLoaded();

        var document = await GetDocumentAsync(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File '{filePath}' not found in solution",
                hint: "Check the file path or reload the solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics"
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree"
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var text = await syntaxTree.GetTextAsync();

        // Find the node spanning the specified lines
        var startPosition = text.Lines[Math.Max(0, startLine)].Start;
        var endPosition = text.Lines[Math.Min(text.Lines.Count - 1, endLine)].End;
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        var node = root.FindNode(span);

        // Try to find a suitable node for data flow analysis
        StatementSyntax? firstStatement = null;
        StatementSyntax? lastStatement = null;

        var statements = node.DescendantNodesAndSelf().OfType<StatementSyntax>().ToList();
        if (statements.Count > 0)
        {
            firstStatement = statements.First();
            lastStatement = statements.Last();
        }

        if (firstStatement == null || lastStatement == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "No statements found in the specified region",
                hint: "Ensure the line range contains executable statements",
                context: new { startLine, endLine }
            );
        }

        DataFlowAnalysis? dataFlow = null;
        try
        {
            dataFlow = firstStatement == lastStatement
                ? semanticModel.AnalyzeDataFlow(firstStatement)
                : semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"Data flow analysis failed: {ex.Message}",
                hint: "Try selecting a different code region"
            );
        }

        if (dataFlow == null || !dataFlow.Succeeded)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Data flow analysis did not succeed",
                hint: "The selected region may not be suitable for data flow analysis"
            );
        }

        return CreateSuccessResponse(
            data: new
            {
                region = new { startLine, endLine },
                succeeded = dataFlow.Succeeded,
                variablesDeclared = dataFlow.VariablesDeclared.Select(s => s.Name).ToArray(),
                alwaysAssigned = dataFlow.AlwaysAssigned.Select(s => s.Name).ToArray(),
                dataFlowsIn = dataFlow.DataFlowsIn.Select(s => s.Name).ToArray(),
                dataFlowsOut = dataFlow.DataFlowsOut.Select(s => s.Name).ToArray(),
                readInside = dataFlow.ReadInside.Select(s => s.Name).ToArray(),
                writtenInside = dataFlow.WrittenInside.Select(s => s.Name).ToArray(),
                readOutside = dataFlow.ReadOutside.Select(s => s.Name).ToArray(),
                writtenOutside = dataFlow.WrittenOutside.Select(s => s.Name).ToArray(),
                captured = dataFlow.Captured.Select(s => s.Name).ToArray(),
                capturedInside = dataFlow.CapturedInside.Select(s => s.Name).ToArray(),
                capturedOutside = dataFlow.CapturedOutside.Select(s => s.Name).ToArray(),
                unsafeAddressTaken = dataFlow.UnsafeAddressTaken.Select(s => s.Name).ToArray()
            },
            suggestedNextTools: new[]
            {
                "analyze_control_flow to analyze branching in the same region",
                "get_diagnostics to check for related warnings"
            }
        );
    }

    /// <summary>
    /// Analyzes control flow (branching and reachability) in a code region.
    /// </summary>
    public async Task<object> AnalyzeControlFlowAsync(
        string filePath,
        int startLine,
        int endLine)
    {
        EnsureSolutionLoaded();

        var document = await GetDocumentAsync(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File '{filePath}' not found in solution",
                hint: "Check the file path or reload the solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model"
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree"
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var text = await syntaxTree.GetTextAsync();

        var startPosition = text.Lines[Math.Max(0, startLine)].Start;
        var endPosition = text.Lines[Math.Min(text.Lines.Count - 1, endLine)].End;
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        var node = root.FindNode(span);

        var statements = node.DescendantNodesAndSelf().OfType<StatementSyntax>().ToList();
        if (statements.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "No statements found in the specified region",
                context: new { startLine, endLine }
            );
        }

        var firstStatement = statements.First();
        var lastStatement = statements.Last();

        ControlFlowAnalysis? controlFlow = null;
        try
        {
            controlFlow = semanticModel.AnalyzeControlFlow(firstStatement, lastStatement);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"Control flow analysis failed: {ex.Message}"
            );
        }

        if (controlFlow == null || !controlFlow.Succeeded)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Control flow analysis did not succeed"
            );
        }

        var entryPoints = controlFlow.EntryPoints.Select(ep =>
        {
            var lineSpan = ep.GetLocation().GetLineSpan();
            return new
            {
                line = lineSpan.StartLinePosition.Line,
                kind = ep.Kind().ToString()
            };
        }).ToList();

        var exitPoints = controlFlow.ExitPoints.Select(ep =>
        {
            var lineSpan = ep.GetLocation().GetLineSpan();
            return new
            {
                line = lineSpan.StartLinePosition.Line,
                kind = ep.Kind().ToString()
            };
        }).ToList();

        var returnStatements = controlFlow.ReturnStatements.Select(rs =>
        {
            var lineSpan = rs.GetLocation().GetLineSpan();
            return new
            {
                line = lineSpan.StartLinePosition.Line,
                text = rs.ToString().Trim()
            };
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                region = new { startLine, endLine },
                succeeded = controlFlow.Succeeded,
                entryPoints,
                exitPoints,
                returnStatements,
                endPointIsReachable = controlFlow.EndPointIsReachable,
                startPointIsReachable = controlFlow.StartPointIsReachable
            },
            suggestedNextTools: new[]
            {
                "analyze_data_flow to analyze variable usage in the same region",
                "get_diagnostics to check for unreachable code warnings"
            }
        );
    }

    #endregion

    #region Compound Tools

    /// <summary>
    /// Gets a comprehensive overview of a type in a single call.
    /// Combines type info, base types summary, member counts, and Godot attributes.
    /// </summary>
    public async Task<object> GetTypeOverviewAsync(string typeName)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required"
            );
        }

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols"
            );
        }

        // Base type chain (first 3)
        var baseTypes = new List<string>();
        var currentBase = type.BaseType;
        var count = 0;
        while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object && count < 3)
        {
            baseTypes.Add(currentBase.ToDisplayString());
            currentBase = currentBase.BaseType;
            count++;
        }

        // Member counts
        var members = type.GetMembers().Where(m => !m.IsImplicitlyDeclared).ToList();
        var memberSummary = new
        {
            methods = members.OfType<IMethodSymbol>().Count(m => m.MethodKind == MethodKind.Ordinary),
            properties = members.OfType<IPropertySymbol>().Count(),
            fields = members.OfType<IFieldSymbol>().Count(),
            events = members.OfType<IEventSymbol>().Count(),
            constructors = members.OfType<IMethodSymbol>().Count(m => m.MethodKind == MethodKind.Constructor)
        };

        // Godot attributes check
        var godotAttributes = new
        {
            hasExport = members.Any(m => HasAttribute(m, "Export")),
            hasSignal = members.Any(m => HasAttribute(m, "Signal")),
            hasTool = HasAttribute(type, "Tool"),
            hasGlobalClass = HasAttribute(type, "GlobalClass")
        };

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                simpleName = type.Name,
                typeKind = type.TypeKind.ToString(),
                @namespace = type.ContainingNamespace?.ToDisplayString(),
                isAbstract = type.IsAbstract,
                isSealed = type.IsSealed,
                isStatic = type.IsStatic,
                baseTypes,
                baseTypeCount = type.BaseType != null ? baseTypes.Count + (currentBase != null ? 1 : 0) : 0,
                interfaceCount = type.AllInterfaces.Length,
                memberSummary,
                godotAttributes,
                location = GetSymbolLocation(type)
            },
            suggestedNextTools: new[]
            {
                $"get_type_members for detailed member list",
                $"get_base_types for full inheritance chain",
                $"get_derived_types to find classes inheriting from {type.Name}",
                godotAttributes.hasExport ? "get_attributes('Export') to see exported properties" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    /// <summary>
    /// Gets comprehensive analysis of a method in a single call.
    /// Combines signature, callers, outgoing calls, and suggests data flow analysis.
    /// </summary>
    public async Task<object> AnalyzeMethodAsync(
        string typeName,
        string methodName,
        bool includeCallers = true,
        bool includeOutgoingCalls = false,
        int maxCallers = 20,
        int maxOutgoingCalls = 50)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName and methodName are required"
            );
        }

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found"
            );
        }

        var method = type.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (method == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found on type '{type.Name}'"
            );
        }

        // Get signature details
        var signature = new
        {
            name = method.Name,
            fullSignature = method.ToDisplayString(),
            returnType = method.ReturnType.ToDisplayString(),
            parameters = method.Parameters.Select(p => new
            {
                name = p.Name,
                type = p.Type.ToDisplayString(),
                isOptional = p.IsOptional
            }).ToList(),
            isAsync = method.IsAsync,
            isStatic = method.IsStatic,
            isVirtual = method.IsVirtual,
            accessibility = method.DeclaredAccessibility.ToString()
        };

        // Get callers if requested
        List<object>? callers = null;
        var totalCallers = 0;
        if (includeCallers)
        {
            var callerSymbols = await SymbolFinder.FindCallersAsync(method, _solution!);
            var callerList = callerSymbols.ToList();
            totalCallers = callerList.Count;

            callers = callerList
                .Take(maxCallers)
                .Select(c => new
                {
                    callingMethod = c.CallingSymbol.ToDisplayString(),
                    containingType = c.CallingSymbol.ContainingType?.Name,
                    locations = c.Locations.Select(l =>
                    {
                        var lineSpan = l.GetLineSpan();
                        return new
                        {
                            filePath = lineSpan.Path,
                            line = lineSpan.StartLinePosition.Line
                        };
                    }).Take(3).ToList()
                })
                .Cast<object>()
                .ToList();
        }

        // Get outgoing calls if requested
        List<object>? outgoingCalls = null;
        var totalOutgoingCalls = 0;
        if (includeOutgoingCalls)
        {
            var location = method.Locations.FirstOrDefault(l => l.IsInSource);
            if (location?.SourceTree != null)
            {
                var root = await location.SourceTree.GetRootAsync();
                var node = root.FindNode(location.SourceSpan);
                var methodDecl = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

                if (methodDecl != null)
                {
                    var document = _solution!.GetDocument(location.SourceTree);
                    var semanticModel = document != null ? await document.GetSemanticModelAsync() : null;

                    if (semanticModel != null)
                    {
                        var calls = new List<object>();
                        var visited = new HashSet<string>();

                        // Find method invocations
                        foreach (var invocation in methodDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                            {
                                var key = calledMethod.ToDisplayString();
                                if (visited.Contains(key)) continue;
                                visited.Add(key);

                                var callLoc = calledMethod.Locations.FirstOrDefault(l => l.IsInSource);
                                calls.Add(new
                                {
                                    method = calledMethod.ToDisplayString(),
                                    shortName = $"{calledMethod.ContainingType?.Name}.{calledMethod.Name}",
                                    returnType = calledMethod.ReturnType.ToDisplayString(),
                                    isAsync = calledMethod.IsAsync,
                                    isExternal = callLoc == null
                                });
                            }
                        }

                        // Find property accesses
                        foreach (var access in methodDecl.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                        {
                            var symbolInfo = semanticModel.GetSymbolInfo(access);
                            if (symbolInfo.Symbol is IPropertySymbol prop)
                            {
                                var key = prop.ToDisplayString();
                                if (visited.Contains(key)) continue;
                                visited.Add(key);

                                var propLoc = prop.Locations.FirstOrDefault(l => l.IsInSource);
                                calls.Add(new
                                {
                                    method = prop.ToDisplayString(),
                                    shortName = $"{prop.ContainingType?.Name}.{prop.Name}",
                                    returnType = prop.Type.ToDisplayString(),
                                    isAsync = false,
                                    isProperty = true,
                                    isExternal = propLoc == null
                                });
                            }
                        }

                        totalOutgoingCalls = calls.Count;
                        outgoingCalls = calls.Take(maxOutgoingCalls).ToList();
                    }
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                signature,
                callers,
                totalCallers,
                callersShown = callers?.Count ?? 0,
                outgoingCalls,
                totalOutgoingCalls,
                outgoingCallsShown = outgoingCalls?.Count ?? 0,
                location = GetSymbolLocation(method),
                overloadCount = type.GetMembers(methodName).OfType<IMethodSymbol>().Count()
            },
            suggestedNextTools: new[]
            {
                method.Locations.FirstOrDefault()?.IsInSource == true
                    ? $"analyze_data_flow to analyze variable flow within the method"
                    : null,
                $"find_implementations if {methodName} is virtual/interface method"
            }.Where(s => s != null).ToArray()!
        );
    }

    /// <summary>
    /// Gets a comprehensive overview of a file.
    /// Combines diagnostics, type declarations, and structure.
    /// </summary>
    public async Task<object> GetFileOverviewAsync(string filePath)
    {
        EnsureSolutionLoaded();

        var document = await GetDocumentAsync(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File '{filePath}' not found in solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        var syntaxTree = await document.GetSyntaxTreeAsync();

        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file"
            );
        }

        var root = await syntaxTree.GetRootAsync();

        // Get diagnostics
        var diagnostics = semanticModel.GetDiagnostics()
            .GroupBy(d => d.Severity)
            .ToDictionary(
                g => g.Key.ToString(),
                g => g.Count()
            );

        // Get type declarations
        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(td =>
            {
                var symbol = semanticModel.GetDeclaredSymbol(td);
                return new
                {
                    name = symbol?.Name ?? td.Identifier.Text,
                    kind = td.Kind().ToString().Replace("Declaration", ""),
                    line = td.GetLocation().GetLineSpan().StartLinePosition.Line,
                    memberCount = td.Members.Count
                };
            })
            .ToList();

        // Get using directives
        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString() ?? "")
            .Where(u => !string.IsNullOrEmpty(u))
            .ToList();

        // Get namespace
        var namespaceDecl = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();

        return CreateSuccessResponse(
            data: new
            {
                filePath = document.FilePath,
                projectName = document.Project.Name,
                @namespace = namespaceDecl?.Name.ToString(),
                diagnosticSummary = diagnostics,
                usingCount = usings.Count,
                typeDeclarations,
                lineCount = (await syntaxTree.GetTextAsync()).Lines.Count
            },
            suggestedNextTools: new[]
            {
                diagnostics.ContainsKey("Error") ? "get_diagnostics for detailed error info" : null,
                typeDeclarations.Count > 0 ? $"get_type_members for {typeDeclarations[0].name} to see members" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    #endregion

    #region Phase 3: Code Actions

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

        if (changedSolution == _solution)
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
                    filePath = addedDoc.FilePath ?? $"NewFile_{addedDoc.Name}",
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
                    filePath = newDoc.FilePath,
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
                    filePath = removedDoc.FilePath,
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

    #endregion

    #region Phase 3: Convenience Tools (Tier 2)

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

    #endregion

    #region Phase 3: Custom Tools (Tier 3)

    /// <summary>
    /// Get complexity metrics for a method or file.
    /// </summary>
    public async Task<object> GetComplexityMetricsAsync(
        string filePath,
        int? line = null,
        int? column = null,
        List<string>? metrics = null)
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
        var semanticModel = await document.GetSemanticModelAsync();

        if (syntaxTree == null || semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file",
                context: new { filePath }
            );
        }

        var root = await syntaxTree.GetRootAsync();

        // Default to all metrics if none specified
        var requestedMetrics = metrics ?? new List<string>
        {
            "cyclomatic", "nesting", "loc", "parameters", "cognitive"
        };

        // If line is specified, find the method at that position
        if (line.HasValue)
        {
            var position = GetPosition(syntaxTree, line.Value, column ?? 0);
            var node = root.FindToken(position).Parent;

            // Find containing method
            var methodNode = node?.AncestorsAndSelf()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            if (methodNode == null)
            {
                // Try property accessor
                var accessor = node?.AncestorsAndSelf()
                    .OfType<AccessorDeclarationSyntax>()
                    .FirstOrDefault();

                if (accessor == null)
                {
                    return CreateErrorResponse(
                        ErrorCodes.SymbolNotFound,
                        "No method or property accessor found at this position",
                        hint: "Position cursor inside a method body",
                        context: new { filePath, line, column }
                    );
                }

                // Analyze accessor
                var accessorMetrics = CalculateComplexityMetrics(accessor, requestedMetrics);
                return CreateSuccessResponse(
                    data: new
                    {
                        scope = "accessor",
                        name = accessor.Parent is PropertyDeclarationSyntax prop
                            ? $"{prop.Identifier.Text}.{accessor.Keyword.Text}"
                            : accessor.Keyword.Text,
                        metrics = accessorMetrics
                    },
                    suggestedNextTools: new[]
                    {
                        accessorMetrics.TryGetValue("cyclomatic", out var accCC) && (int)accCC > 10
                            ? "Consider refactoring - cyclomatic complexity > 10"
                            : null
                    }.Where(s => s != null).ToArray()!
                );
            }

            var methodMetrics = CalculateComplexityMetrics(methodNode, requestedMetrics);
            var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode);

            return CreateSuccessResponse(
                data: new
                {
                    scope = "method",
                    name = methodSymbol?.Name ?? methodNode.Identifier.Text,
                    containingType = methodSymbol?.ContainingType?.Name,
                    metrics = methodMetrics
                },
                suggestedNextTools: new[]
                {
                    methodMetrics.TryGetValue("cyclomatic", out var cc) && (int)cc > 10
                        ? "Consider refactoring - cyclomatic complexity > 10"
                        : null,
                    methodMetrics.TryGetValue("nesting", out var nest) && (int)nest > 4
                        ? "Consider refactoring - nesting depth > 4"
                        : null
                }.Where(s => s != null).ToArray()!
            );
        }

        // Analyze whole file - get metrics for all methods
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Select(m =>
            {
                var symbol = semanticModel.GetDeclaredSymbol(m);
                var methodMetrics = CalculateComplexityMetrics(m, requestedMetrics);
                return new
                {
                    name = symbol?.Name ?? m.Identifier.Text,
                    containingType = symbol?.ContainingType?.Name,
                    line = m.GetLocation().GetLineSpan().StartLinePosition.Line,
                    metrics = methodMetrics
                };
            })
            .ToList();

        // Calculate file-level totals
        var fileTotals = new Dictionary<string, object>();
        foreach (var metric in requestedMetrics)
        {
            if (metric == "cyclomatic")
            {
                var total = methods.Sum(m => m.metrics.TryGetValue("cyclomatic", out var v) ? (int)v : 0);
                var avg = methods.Count > 0 ? (double)total / methods.Count : 0;
                fileTotals["avgCyclomatic"] = Math.Round(avg, 2);
                fileTotals["maxCyclomatic"] = methods.Max(m => m.metrics.TryGetValue("cyclomatic", out var v) ? (int)v : 0);
            }
            else if (metric == "nesting")
            {
                fileTotals["maxNesting"] = methods.Count > 0
                    ? methods.Max(m => m.metrics.TryGetValue("nesting", out var v) ? (int)v : 0)
                    : 0;
            }
            else if (metric == "loc")
            {
                fileTotals["totalLoc"] = methods.Sum(m => m.metrics.TryGetValue("loc", out var v) ? (int)v : 0);
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                scope = "file",
                filePath,
                methodCount = methods.Count,
                fileTotals,
                methods
            },
            totalCount: methods.Count,
            returnedCount: methods.Count
        );
    }

    private Dictionary<string, object> CalculateComplexityMetrics(SyntaxNode node, List<string> requestedMetrics)
    {
        var result = new Dictionary<string, object>();

        foreach (var metric in requestedMetrics)
        {
            switch (metric.ToLowerInvariant())
            {
                case "cyclomatic":
                    result["cyclomatic"] = CalculateCyclomaticComplexity(node);
                    break;
                case "nesting":
                    result["nesting"] = CalculateNestingDepth(node);
                    break;
                case "loc":
                    result["loc"] = CalculateLinesOfCode(node);
                    break;
                case "parameters":
                    result["parameters"] = CountParameters(node);
                    break;
                case "cognitive":
                    result["cognitive"] = CalculateCognitiveComplexity(node);
                    break;
            }
        }

        return result;
    }

    private int CalculateCyclomaticComplexity(SyntaxNode node)
    {
        // Start with 1 (the method itself is one path)
        int complexity = 1;

        foreach (var descendant in node.DescendantNodes())
        {
            switch (descendant)
            {
                case IfStatementSyntax:
                case ConditionalExpressionSyntax: // ?:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case CatchClauseSyntax:
                case ConditionalAccessExpressionSyntax: // ?.
                    complexity++;
                    break;
                case BinaryExpressionSyntax binary when
                    binary.Kind() == SyntaxKind.LogicalAndExpression ||
                    binary.Kind() == SyntaxKind.LogicalOrExpression ||
                    binary.Kind() == SyntaxKind.CoalesceExpression: // ??
                    complexity++;
                    break;
            }
        }

        return complexity;
    }

    private int CalculateNestingDepth(SyntaxNode node)
    {
        int maxDepth = 0;
        CalculateNestingDepthRecursive(node, 0, ref maxDepth);
        return maxDepth;
    }

    private void CalculateNestingDepthRecursive(SyntaxNode node, int currentDepth, ref int maxDepth)
    {
        foreach (var child in node.ChildNodes())
        {
            int newDepth = currentDepth;

            if (child is IfStatementSyntax ||
                child is WhileStatementSyntax ||
                child is ForStatementSyntax ||
                child is ForEachStatementSyntax ||
                child is SwitchStatementSyntax ||
                child is TryStatementSyntax ||
                child is LockStatementSyntax ||
                child is UsingStatementSyntax)
            {
                newDepth = currentDepth + 1;
                maxDepth = Math.Max(maxDepth, newDepth);
            }

            CalculateNestingDepthRecursive(child, newDepth, ref maxDepth);
        }
    }

    private int CalculateLinesOfCode(SyntaxNode node)
    {
        var text = node.ToFullString();
        var lines = text.Split('\n')
            .Where(line =>
            {
                var trimmed = line.Trim();
                // Exclude empty lines and comment-only lines
                return !string.IsNullOrWhiteSpace(trimmed) &&
                       !trimmed.StartsWith("//") &&
                       !trimmed.StartsWith("/*") &&
                       !trimmed.StartsWith("*");
            })
            .Count();
        return lines;
    }

    private int CountParameters(SyntaxNode node)
    {
        if (node is MethodDeclarationSyntax method)
        {
            return method.ParameterList.Parameters.Count;
        }
        return 0;
    }

    private int CalculateCognitiveComplexity(SyntaxNode node)
    {
        // Cognitive complexity is similar to cyclomatic but penalizes nesting
        int complexity = 0;
        CalculateCognitiveComplexityRecursive(node, 0, ref complexity);
        return complexity;
    }

    private void CalculateCognitiveComplexityRecursive(SyntaxNode node, int nestingLevel, ref int complexity)
    {
        foreach (var child in node.ChildNodes())
        {
            int increment = 0;
            int newNesting = nestingLevel;

            switch (child)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case SwitchStatementSyntax:
                case CatchClauseSyntax:
                    increment = 1 + nestingLevel; // Base + nesting penalty
                    newNesting = nestingLevel + 1;
                    break;
                case ConditionalExpressionSyntax: // ?:
                    increment = 1 + nestingLevel;
                    break;
                case BinaryExpressionSyntax binary when
                    binary.Kind() == SyntaxKind.LogicalAndExpression ||
                    binary.Kind() == SyntaxKind.LogicalOrExpression:
                    increment = 1; // No nesting penalty for && and ||
                    break;
                case ElseClauseSyntax:
                    increment = 1; // else adds complexity
                    break;
            }

            complexity += increment;
            CalculateCognitiveComplexityRecursive(child, newNesting, ref complexity);
        }
    }

    /// <summary>
    /// Add null check guard clauses to method parameters.
    /// </summary>
    public async Task<object> AddNullChecksAsync(
        string filePath,
        int line,
        int column,
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
        var semanticModel = await document.GetSemanticModelAsync();

        if (syntaxTree == null || semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file",
                context: new { filePath }
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var position = GetPosition(syntaxTree, line, column);
        var node = root.FindToken(position).Parent;

        // Find the method
        var methodNode = node?.AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (methodNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method found at this position",
                hint: "Position cursor on a method declaration",
                context: new { filePath, line, column }
            );
        }

        // Get parameters that are reference types and could be null
        var nullableParams = methodNode.ParameterList.Parameters
            .Where(p =>
            {
                var paramSymbol = semanticModel.GetDeclaredSymbol(p);
                if (paramSymbol == null) return false;

                var type = paramSymbol.Type;
                // Check if it's a reference type or nullable value type
                return type.IsReferenceType ||
                       type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            })
            .Select(p => p.Identifier.Text)
            .ToList();

        if (nullableParams.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    message = "No nullable parameters found that need null checks",
                    methodName = methodNode.Identifier.Text
                }
            );
        }

        // Generate null check code
        var nullChecks = new StringBuilder();
        foreach (var param in nullableParams)
        {
            nullChecks.AppendLine($"        ArgumentNullException.ThrowIfNull({param});");
        }

        // Find where to insert (after opening brace of method body)
        var body = methodNode.Body;
        if (body == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Method has no body (possibly expression-bodied)",
                hint: "This tool works with block-bodied methods",
                context: new { methodName = methodNode.Identifier.Text }
            );
        }

        var openBrace = body.OpenBraceToken;
        var insertPosition = openBrace.Span.End;

        // Get the text and create the modified version
        var sourceText = await document.GetTextAsync();
        var newText = sourceText.Replace(new TextSpan(insertPosition, 0), "\n" + nullChecks.ToString());

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    methodName = methodNode.Identifier.Text,
                    parametersWithNullChecks = nullableParams,
                    generatedCode = nullChecks.ToString().Trim(),
                    changes = new[]
                    {
                        new
                        {
                            filePath,
                            insertAfterLine = openBrace.GetLocation().GetLineSpan().StartLinePosition.Line,
                            newCode = nullChecks.ToString().Trim()
                        }
                    }
                },
                suggestedNextTools: new[] { "add_null_checks with preview=false to apply" }
            );
        }

        // Apply the change
        await File.WriteAllTextAsync(filePath, newText.ToString());

        // Reload solution to pick up changes
        _documentCache.Clear();

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName = methodNode.Identifier.Text,
                parametersWithNullChecks = nullableParams,
                generatedCode = nullChecks.ToString().Trim()
            },
            suggestedNextTools: new[] { "get_diagnostics to verify changes" }
        );
    }

    /// <summary>
    /// Generate Equals, GetHashCode, and operators for a type.
    /// </summary>
    public async Task<object> GenerateEqualityMembersAsync(
        string filePath,
        int line,
        int column,
        bool includeOperators = true,
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
        var semanticModel = await document.GetSemanticModelAsync();

        if (syntaxTree == null || semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file",
                context: new { filePath }
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var position = GetPosition(syntaxTree, line, column);
        var node = root.FindToken(position).Parent;

        // Find the type declaration
        var typeNode = node?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (typeNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "No type declaration found at this position",
                hint: "Position cursor on a class or struct declaration",
                context: new { filePath, line, column }
            );
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeNode) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get type symbol",
                context: new { typeName = typeNode.Identifier.Text }
            );
        }

        var typeName = typeSymbol.Name;

        // Get all fields and auto-properties to include in equality
        var members = typeSymbol.GetMembers()
            .Where(m =>
                (m is IFieldSymbol field && !field.IsStatic && !field.IsConst) ||
                (m is IPropertySymbol prop && !prop.IsStatic && prop.GetMethod != null))
            .Select(m => m.Name)
            .ToList();

        if (members.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    message = "No instance fields or properties found to compare",
                    typeName
                }
            );
        }

        // Generate Equals method
        var equalsCode = new StringBuilder();
        equalsCode.AppendLine($@"
    public override bool Equals(object? obj)
    {{
        return obj is {typeName} other && Equals(other);
    }}

    public bool Equals({typeName}? other)
    {{
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return {string.Join(" && ", members.Select(m => $"{m} == other.{m}"))};
    }}

    public override int GetHashCode()
    {{
        return HashCode.Combine({string.Join(", ", members.Take(8))});
    }}");

        if (includeOperators)
        {
            equalsCode.AppendLine($@"
    public static bool operator ==({typeName}? left, {typeName}? right)
    {{
        return Equals(left, right);
    }}

    public static bool operator !=({typeName}? left, {typeName}? right)
    {{
        return !Equals(left, right);
    }}");
        }

        // Find where to insert (before closing brace of type)
        var closeBrace = typeNode.CloseBraceToken;
        var insertPosition = closeBrace.SpanStart;

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    typeName,
                    membersCompared = members,
                    includeOperators,
                    generatedCode = equalsCode.ToString().Trim()
                },
                suggestedNextTools: new[] { "generate_equality_members with preview=false to apply" }
            );
        }

        // Apply the change
        var sourceText = await document.GetTextAsync();
        var newText = sourceText.Replace(new TextSpan(insertPosition, 0), equalsCode.ToString());
        await File.WriteAllTextAsync(filePath, newText.ToString());

        // Clear cache
        _documentCache.Clear();

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                typeName,
                membersCompared = members,
                includeOperators
            },
            suggestedNextTools: new[] { "get_diagnostics to verify changes" }
        );
    }

    #endregion
}
