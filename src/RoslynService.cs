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
using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;

namespace SharpLensMcp;

public partial class RoslynService
{
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly Dictionary<string, Document> _documentCache = new();
    internal readonly ConcurrentDictionary<ProjectId, Compilation> _compilationCache = new();
    private readonly int _maxDiagnostics;
    private readonly int _timeoutSeconds;
    private readonly bool _useAbsolutePaths;

    private DateTime? _solutionLoadedAt;

    public RoslynService()
    {
        _maxDiagnostics = int.TryParse(Environment.GetEnvironmentVariable("ROSLYN_MAX_DIAGNOSTICS"), out var maxDiag)
            ? maxDiag : 100;
        _timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("ROSLYN_TIMEOUT_SECONDS"), out var timeout)
            ? timeout : 30;
        _useAbsolutePaths = Environment.GetEnvironmentVariable("SHARPLENS_ABSOLUTE_PATHS")?.ToLower() == "true";
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

    /// <summary>
    /// Formats a file path as relative or absolute based on configuration.
    /// Default is relative paths to save tokens. Set SHARPLENS_ABSOLUTE_PATHS=true for absolute.
    /// Normalizes path separators to forward slashes for cross-platform consistency.
    /// </summary>
    private string FormatPath(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return absolutePath ?? "";

        if (_useAbsolutePaths || _solution?.FilePath == null)
            return NormalizePath(absolutePath);

        var solutionDir = Path.GetDirectoryName(_solution.FilePath);
        if (string.IsNullOrEmpty(solutionDir))
            return NormalizePath(absolutePath);

        try
        {
            var relativePath = Path.GetRelativePath(solutionDir, absolutePath);
            return NormalizePath(relativePath);
        }
        catch
        {
            return NormalizePath(absolutePath); // Fallback to absolute on any error
        }
    }

    /// <summary>
    /// Normalizes path separators to forward slashes for cross-platform consistency.
    /// </summary>
    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Platform-aware path comparison: case-insensitive on Windows, case-sensitive on Linux/macOS.
    /// Matches file system behavior on each platform.
    /// </summary>
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;



    /// <summary>
    /// Finds a type by name, trying multiple resolution strategies.
    /// Supports fully-qualified names, simple names, and Godot namespace prefixing.
    /// </summary>
    private async Task<INamedTypeSymbol?> FindTypeByNameAsync(string typeName)
    {
        EnsureSolutionLoaded();

        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project);
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
    private object? GetSymbolLocation(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
        if (location == null) return null;

        var lineSpan = location.GetLineSpan();
        return new
        {
            filePath = FormatPath(lineSpan.Path),
            line = lineSpan.StartLinePosition.Line,
            column = lineSpan.StartLinePosition.Character
        };
    }


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
        _compilationCache.Clear();

        _workspace = MSBuildWorkspace.Create();
        _workspace.RegisterWorkspaceFailedHandler(args =>
        {
            Console.Error.WriteLine($"[Warning] Workspace: {args.Diagnostic.Message}");
        });

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

    /// <summary>
    /// Synchronize document changes from disk into the loaded solution.
    /// Agent is responsible for calling this after using Edit/Write tools.
    /// </summary>
    public async Task<object> SyncDocumentsAsync(List<string>? filePaths)
    {
        if (_solution == null || _workspace == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SolutionNotLoaded,
                "No solution loaded. Call load_solution first.",
                hint: "Use load_solution before syncing documents"
            );
        }

        var solutionDir = Path.GetDirectoryName(_solution.FilePath);
        var updated = new List<string>();
        var added = new List<string>();
        var removed = new List<string>();
        var errors = new List<object>();

        // Get all documents in solution for lookup
        var documentsByPath = _solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null)
            .ToDictionary(
                d => Path.GetFullPath(d.FilePath!),
                d => d,
                PathComparison == StringComparison.OrdinalIgnoreCase
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal
            );

        // Determine which files to sync
        IEnumerable<string> pathsToSync;
        if (filePaths != null && filePaths.Count > 0)
        {
            // Sync specific files
            pathsToSync = filePaths.Select(p =>
            {
                if (Path.IsPathRooted(p))
                    return Path.GetFullPath(p);
                return Path.GetFullPath(Path.Combine(solutionDir ?? "", p));
            });
        }
        else
        {
            // Sync all documents - check each for changes
            pathsToSync = documentsByPath.Keys.ToList();
        }

        foreach (var fullPath in pathsToSync)
        {
            try
            {
                var fileExists = File.Exists(fullPath);
                var docExists = documentsByPath.TryGetValue(fullPath, out var existingDoc);

                if (fileExists && docExists)
                {
                    // Update existing document
                    var newText = await File.ReadAllTextAsync(fullPath);
                    var sourceText = SourceText.From(newText, Encoding.UTF8);
                    _solution = _solution.WithDocumentText(existingDoc!.Id, sourceText);
                    updated.Add(FormatPath(fullPath));
                }
                else if (fileExists && !docExists)
                {
                    // Add new document - need to find the right project
                    var project = FindProjectForFile(fullPath);
                    if (project != null)
                    {
                        var newText = await File.ReadAllTextAsync(fullPath);
                        var sourceText = SourceText.From(newText, Encoding.UTF8);
                        var docId = DocumentId.CreateNewId(project.Id);
                        var fileName = Path.GetFileName(fullPath);

                        // Determine folders from path relative to project
                        var projectDir = Path.GetDirectoryName(project.FilePath);
                        var folders = new List<string>();
                        if (projectDir != null && fullPath.StartsWith(projectDir, PathComparison))
                        {
                            var relativePath = Path.GetRelativePath(projectDir, fullPath);
                            var relativeDir = Path.GetDirectoryName(relativePath);
                            if (!string.IsNullOrEmpty(relativeDir))
                            {
                                folders = relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToList();
                            }
                        }

                        _solution = _solution.AddDocument(docId, fileName, sourceText, folders, fullPath);
                        added.Add(FormatPath(fullPath));
                    }
                    else
                    {
                        errors.Add(new { path = FormatPath(fullPath), error = "Could not determine project for file" });
                    }
                }
                else if (!fileExists && docExists)
                {
                    // Remove deleted document
                    _solution = _solution.RemoveDocument(existingDoc!.Id);
                    removed.Add(FormatPath(fullPath));
                }
                // else: file doesn't exist and not in solution - nothing to do
            }
            catch (Exception ex)
            {
                errors.Add(new { path = FormatPath(fullPath), error = ex.Message });
            }
        }

        // Clear caches after sync (no need to call TryApplyChanges - we're syncing FROM disk, not TO disk)
        if (updated.Count > 0 || added.Count > 0 || removed.Count > 0)
        {
            _documentCache.Clear();
            _compilationCache.Clear();
        }

        return CreateSuccessResponse(
            data: new
            {
                updated = updated.Count,
                added = added.Count,
                removed = removed.Count,
                totalSynced = updated.Count + added.Count + removed.Count,
                updatedFiles = updated,
                addedFiles = added,
                removedFiles = removed,
                errors = errors.Count > 0 ? errors : null
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics to check for errors after sync",
                "search_symbols to find updated code"
            }
        );
    }

    /// <summary>
    /// Find the best project for a file based on its path.
    /// </summary>
    private Project? FindProjectForFile(string filePath)
    {
        if (_solution == null) return null;

        var fileDir = Path.GetDirectoryName(filePath);
        if (fileDir == null) return null;

        // Find project whose directory is an ancestor of the file
        Project? bestMatch = null;
        int bestMatchLength = 0;

        foreach (var project in _solution.Projects)
        {
            var projectDir = Path.GetDirectoryName(project.FilePath);
            if (projectDir != null && fileDir.StartsWith(projectDir, PathComparison))
            {
                if (projectDir.Length > bestMatchLength)
                {
                    bestMatch = project;
                    bestMatchLength = projectDir.Length;
                }
            }
        }

        return bestMatch;
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
                var compilation = await GetProjectCompilationAsync(project);
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

    /// <summary>
    /// Gets the compilation for a project with source generators run.
    /// This is the sanctioned way to get a compilation inside RoslynService —
    /// MSBuildWorkspace.GetCompilationAsync() alone returns a pre-generator compilation,
    /// which causes phantom errors when code references generated members.
    ///
    /// Thread safety: tool calls are serialized through stdio so there are no concurrent
    /// cache misses in practice. ConcurrentDictionary is used for cheap lock-free reads.
    /// </summary>
    /// <summary>Test-only accessor for the loaded solution. Do not use outside tests.</summary>
    internal Solution? GetSolutionForTesting() => _solution;

    internal async Task<Compilation?> GetProjectCompilationAsync(Project project)
    {
        if (_compilationCache.TryGetValue(project.Id, out var cached))
            return cached;

        var baseCompilation = await project.GetCompilationAsync();
        if (baseCompilation == null) return null;

        var generators = project.AnalyzerReferences
            .SelectMany(ar => ar.GetGenerators(LanguageNames.CSharp))
            .ToImmutableArray();

        if (generators.IsEmpty)
        {
            _compilationCache[project.Id] = baseCompilation;
            return baseCompilation;
        }

        try
        {
            var additionalTexts = project.AdditionalDocuments
                .Select(d => (AdditionalText)new AdditionalTextFile(d))
                .ToImmutableArray();

            var driver = CSharpGeneratorDriver.Create(
                generators: generators,
                additionalTexts: additionalTexts,
                parseOptions: (CSharpParseOptions)project.ParseOptions!,
                optionsProvider: project.AnalyzerOptions.AnalyzerConfigOptionsProvider);

            driver.RunGeneratorsAndUpdateCompilation(baseCompilation, out var updated, out _);
            _compilationCache[project.Id] = updated;
            return updated;
        }
        catch (Exception ex)
        {
            // A misbehaving generator must not break the entire server.
            Console.Error.WriteLine($"[Warning] Source generator failed for '{project.Name}': {ex.Message}");
            _compilationCache[project.Id] = baseCompilation;
            return baseCompilation;
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
                string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(filePath), PathComparison));

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
                filePath = FormatPath(lineSpan.Value.Path),
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

            var compilation = await GetProjectCompilationAsync(project);
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
                        filePath = FormatPath(lineSpan.Path),
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
                filePath = FormatPath(lineSpan.Value.Path),
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
}
