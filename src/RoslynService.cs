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
    // Stored as base Workspace so tests can substitute an AdhocWorkspace for
    // in-memory scenarios (broken code that CS0535 won't let us put on disk,
    // hermetic per-test isolation, sub-second test runs). Production code path
    // still uses MSBuildWorkspace; LoadSolutionAsync constructs it locally.
    private Workspace? _workspace;
    private Solution? _solution;
    private readonly Dictionary<string, Document> _documentCache = new();
    internal readonly ConcurrentDictionary<ProjectId, Compilation> _compilationCache = new();
    private int _maxDiagnostics;
    private readonly int _timeoutSeconds;
    private readonly bool _useAbsolutePaths;

    private DateTime? _solutionLoadedAt;

    // Single source for the semantic-cache flag, read by both the cache gate and the
    // health_check configuration report so the reported state can't drift from the
    // enforced one. On unless SHARPLENS_ENABLE_SEMANTIC_CACHE (legacy
    // ROSLYN_ENABLE_SEMANTIC_CACHE) is explicitly "false".
    private static bool SemanticCacheEnabled =>
        EnvironmentConfig.Get("SHARPLENS_ENABLE_SEMANTIC_CACHE", "ROSLYN_ENABLE_SEMANTIC_CACHE") != "false";

    public RoslynService()
    {
        _maxDiagnostics = int.TryParse(EnvironmentConfig.Get("SHARPLENS_MAX_DIAGNOSTICS", "ROSLYN_MAX_DIAGNOSTICS"), out var maxDiag)
            ? maxDiag : 100;
        // Guard > 0 to match McpServer.ReadConfiguredToolCallTimeout, so the value
        // health_check reports can't diverge from the timeout actually enforced.
        _timeoutSeconds = int.TryParse(EnvironmentConfig.Get("SHARPLENS_TIMEOUT_SECONDS", "ROSLYN_TIMEOUT_SECONDS"), out var timeout) && timeout > 0
            ? timeout : 30;
        _useAbsolutePaths = Environment.GetEnvironmentVariable("SHARPLENS_ABSOLUTE_PATHS")?.ToLower() == "true";
    }

    // Helper method for glob pattern matching (supports * and ? wildcards).
    // Internal so unit tests can verify regex-special-character escaping
    // independently of the callers (search_symbols, namespace filters, etc.).
    internal static bool MatchesGlobPattern(string input, string pattern)
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

    // Records have IsRecord=true but TypeKind=Class/Struct. Return the user-meaningful kind.
    internal static string GetTypeKindString(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named && named.IsRecord)
        {
            return named.TypeKind == TypeKind.Struct ? "RecordStruct" : "Record";
        }
        return type.TypeKind.ToString();
    }


    /// <summary>
    /// Creates a standardized success response with metadata.
    /// </summary>
    private object CreateSuccessResponse(object data, string[]? suggestedNextTools = null,
        int? totalCount = null, int? returnedCount = null, string? verbosity = null,
        bool? truncated = null)
    {
        // Truncated normally derives from the counts (returned a prefix of a larger
        // set). Tools whose result is complete-but-bounded by a depth/node cap rather
        // than a list prefix — get_call_graph, get_outgoing_calls — can't express that
        // through the counts (their array is whole, so totalCount==returnedCount), so
        // they pass `truncated` explicitly; it is OR-combined with the count signal.
        return new ToolSuccessResponse(
            success: true,
            data: data,
            meta: new ResponseMetadata
            {
                TotalCount = totalCount,
                ReturnedCount = returnedCount,
                Truncated = (totalCount.HasValue && returnedCount.HasValue && returnedCount < totalCount)
                    || truncated == true,
                SuggestedNextTools = suggestedNextTools,
                Verbosity = verbosity
            }
        );
    }

    /// <summary>
    /// Creates a standardized error response with recovery hints.
    /// </summary>
    private object CreateErrorResponse(string code, string message, string? hint = null, object? context = null)
    {
        return new ToolErrorResponse(
            success: false,
            error: new RoslynError
            {
                Code = code,
                Message = message,
                Hint = hint,
                Context = context
            });
    }

    // Single typed boundary for reading back a tool-response object created by
    // CreateSuccessResponse / CreateErrorResponse. Both are named records now
    // (ToolSuccessResponse / ToolErrorResponse), so internal callers read them by
    // typed pattern match. The reflection fallback covers any tool that still
    // returns a hand-built anonymous {success, ...} envelope.
    private static bool IsSuccessResponse(object? response) => response switch
    {
        ToolSuccessResponse s => s.success,
        ToolErrorResponse => false,
        null => false,
        _ => response.GetType().GetProperty("success")?.GetValue(response) is bool b && b
    };

    private static object? GetResponseData(object? response) => response switch
    {
        ToolSuccessResponse s => s.data,
        ToolErrorResponse => null,
        _ => response?.GetType().GetProperty("data")?.GetValue(response)
    };

    private static object? GetResponseError(object? response) => response switch
    {
        ToolErrorResponse e => e.error,
        ToolSuccessResponse => null,
        _ => response?.GetType().GetProperty("error")?.GetValue(response)
    };

    private static string? GetResponseErrorCode(object? response)
        => (GetResponseError(response) as RoslynError)?.Code;

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
    /// Supports fully-qualified names, simple names, and partial matches.
    /// A name matching several distinct types yields an AMBIGUOUS_TYPE error
    /// (second tuple element) listing fully-qualified candidates — silently
    /// picking one would hand downstream refactorings the wrong type.
    /// </summary>
    private async Task<(INamedTypeSymbol? type, object? ambiguityError)> FindTypeByNameAsync(
        string typeName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // Prefer base compilations so SymbolFinder (FindDerivedClasses, FindImplementations,
        // FindReferences) recognizes the returned symbol. The augmented compilation from
        // GetProjectCompilationAsync has different symbol identity that SymbolFinder won't
        // resolve. Fall back to augmented compilations only when no base compilation has
        // the type (e.g., it's purely source-generated).
        var baseCompilations = new List<Compilation>();
        foreach (var project in _solution!.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation != null) baseCompilations.Add(compilation);
        }

        var resolved = ResolveTypeAcross(baseCompilations, typeName);
        if (resolved.type != null || resolved.ambiguityError != null) return resolved;

        var augmentedCompilations = new List<Compilation>();
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation != null) augmentedCompilations.Add(compilation);
        }

        return ResolveTypeAcross(augmentedCompilations, typeName);
    }

    private (INamedTypeSymbol? type, object? ambiguityError) ResolveTypeAcross(
        List<Compilation> compilations, string typeName)
    {
        // Strategy 1: fully-qualified metadata name. Genuine ambiguity is two
        // different SOURCE assemblies DECLARING the FQN — not the same
        // referenced/BCL type surfacing under different facade assemblies across
        // target frameworks (e.g. System.String via System.Private.CoreLib vs a
        // netstandard facade). So only count matches the compilation declares in
        // its own source; if several distinct source assemblies do, it is ambiguous.
        var metadataMatches = new List<(INamedTypeSymbol type, Compilation compilation)>();
        foreach (var compilation in compilations)
        {
            if (compilation.GetTypeByMetadataName(typeName) is { } exact) metadataMatches.Add((exact, compilation));
        }
        if (metadataMatches.Count > 0)
        {
            var sourceDeclarations = metadataMatches
                .Where(m => SymbolEqualityComparer.Default.Equals(m.type.ContainingAssembly, m.compilation.Assembly)
                         && m.type.Locations.Any(l => l.IsInSource))
                .Select(m => m.type)
                .GroupBy(t => t.ContainingAssembly?.Name ?? "", StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (sourceDeclarations.Count > 1) return (null, AmbiguousTypeError(typeName, sourceDeclarations));
            return (sourceDeclarations.Count == 1 ? sourceDeclarations[0] : metadataMatches[0].type, null);
        }

        // Strategy 2: exact simple name (case-insensitive) across all projects.
        var byName = CollectSourceTypes(compilations,
            name => name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
        if (byName.Count == 1) return (byName[0], null);
        if (byName.Count > 1) return (null, AmbiguousTypeError(typeName, byName));

        // Strategy 3: partial match (contains).
        var partial = CollectSourceTypes(compilations,
            name => name.Contains(typeName, StringComparison.OrdinalIgnoreCase));
        if (partial.Count == 1) return (partial[0], null);
        if (partial.Count > 1) return (null, AmbiguousTypeError(typeName, partial));

        return (null, null);
    }

    // Distinct types only: the same type surfaces from several compilations when
    // multi-targeting splits a project per TFM — key by fully-qualified name and
    // keep the first occurrence (earliest project, matching the old preference).
    private static List<INamedTypeSymbol> CollectSourceTypes(
        List<Compilation> compilations, Func<string, bool> namePredicate)
    {
        var byFqn = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            var matches = compilation
                .GetSymbolsWithName(namePredicate, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>();
            foreach (var type in matches)
            {
                var fqn = type.ToDisplayString();
                if (!byFqn.ContainsKey(fqn)) byFqn[fqn] = type;
            }
        }
        return byFqn.Values.ToList();
    }

    private object AmbiguousTypeError(string typeName, List<INamedTypeSymbol> candidates)
    {
        const int maxShown = 10;
        var shown = candidates
            .Select(c => c.ToDisplayString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .Take(maxShown)
            .ToList();
        // The candidate list is capped at maxShown but the message reports the true
        // total; surface totalCandidates and a truncated flag so the count and the list
        // never read as disagreeing, and tell the caller how to see the rest.
        var truncated = candidates.Count > maxShown;
        return CreateErrorResponse(
            ErrorCodes.AmbiguousType,
            $"Type name '{typeName}' matches {candidates.Count} types",
            hint: truncated
                ? $"Repeat the call with one of the fully-qualified candidate names ({shown.Count} of {candidates.Count} shown — narrow the name to reveal the rest)."
                : "Repeat the call with one of the fully-qualified candidate names.",
            context: new
            {
                typeName,
                totalCandidates = candidates.Count,
                candidatesShown = shown.Count,
                truncated,
                candidates = shown
            });
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


    public async Task<object> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
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

        // MSBuild-specific construction lives in a local variable; once configured
        // we hand it to the base-typed _workspace field. The workspace's HostServices
        // are MEF-composed from Roslyn's Workspaces + Features assemblies so the
        // language services that built-in refactoring providers depend on (e.g.
        // IExtractMethodService<TStatementSyntax>) are wired up — without this, the
        // C# refactoring providers loaded for get_code_actions_at_position fire but
        // their language-service lookups return null and they emit no actions.
        var hostServices = Microsoft.CodeAnalysis.Host.Mef.MefHostServices.Create(GetRoslynMefAssemblies());
        var msbuild = MSBuildWorkspace.Create(hostServices);
        msbuild.RegisterWorkspaceFailedHandler(args =>
        {
            Console.Error.WriteLine($"[Warning] Workspace: {args.Diagnostic.Message}");
        });

        _solution = await msbuild.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
        _workspace = msbuild;
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
    public async Task<object> SyncDocumentsAsync(List<string>? filePaths, CancellationToken cancellationToken = default)
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

        // A file path is not unique: a multi-targeted project has one Project per TFM over
        // the same .cs files, and linked/shared files appear in several projects. Group by
        // path (never ToDictionary on a non-unique key — it throws on the first duplicate,
        // before the per-file try/catch) and, on a change, sync into EVERY document sharing
        // the path.
        var documentsByPath = _solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null)
            .GroupBy(
                d => Path.GetFullPath(d.FilePath!),
                PathComparison == StringComparison.OrdinalIgnoreCase
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList());

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
                var docExists = documentsByPath.TryGetValue(fullPath, out var existingDocs);

                if (fileExists && docExists)
                {
                    // Update the document in EVERY project that includes this file
                    // (multi-targeted TFMs and linked/shared files share one path).
                    var newText = await File.ReadAllTextAsync(fullPath, cancellationToken);
                    var sourceText = SourceText.From(newText, Encoding.UTF8);
                    foreach (var doc in existingDocs!)
                        _solution = _solution.WithDocumentText(doc.Id, sourceText);
                    updated.Add(FormatPath(fullPath));
                }
                else if (fileExists && !docExists)
                {
                    // Add new document - need to find the right project
                    var project = FindProjectForFile(fullPath);
                    if (project != null)
                    {
                        var newText = await File.ReadAllTextAsync(fullPath, cancellationToken);
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
                    // Remove the deleted document from every project that included it.
                    foreach (var doc in existingDocs!)
                        _solution = _solution.RemoveDocument(doc.Id);
                    removed.Add(FormatPath(fullPath));
                }
                // else: file doesn't exist and not in solution - nothing to do
            }
            catch (OperationCanceledException)
            {
                // A per-call timeout mid-sync must surface as cancellation (→ TIMEOUT),
                // not be folded into a per-file error and returned as partial success.
                throw;
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

    public async Task<object> GetHealthCheckAsync(
        bool includeDiagnostics = false, CancellationToken cancellationToken = default)
    {
        if (_solution == null || _workspace == null)
        {
            return CreateSuccessResponse(data: new
            {
                status = "Not Ready",
                message = "No solution loaded. Call load_solution first or set DOTNET_SOLUTION_PATH environment variable.",
                solution = (object?)null,
                workspace = (object?)null
            });
        }

        // Default is O(1): only load state, counts, and configuration. The
        // diagnostics sweep compiles up to five projects (~tens of seconds on a
        // large solution), so it runs only when includeDiagnostics is set.
        int? errorCount = null;
        int? warningCount = null;

        if (includeDiagnostics)
        {
            errorCount = 0;
            warningCount = 0;
            try
            {
                foreach (var project in _solution.Projects.Take(5)) // Sample first 5 projects for quick health check
                {
                    var compilation = await GetProjectCompilationAsync(project, cancellationToken);
                    if (compilation != null)
                    {
                        var diagnostics = compilation.GetDiagnostics(cancellationToken);
                        errorCount += diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
                        warningCount += diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A per-call timeout must surface as cancellation (→ TIMEOUT), not be
                // swallowed and reported as a healthy zero-error sweep.
                throw;
            }
            catch
            {
                // Ignore errors during health check diagnostics
            }
        }

        var projectCount = _solution.ProjectIds.Count;
        var documentCount = _solution.Projects.Sum(p => p.DocumentIds.Count);

        return CreateSuccessResponse(data: new
        {
            status = "Ready",
            message = "SharpLens MCP server is operational",
            solution = new
            {
                loaded = true,
                path = _solution.FilePath,
                projects = projectCount,
                documents = documentCount,
                loadedAt = _solutionLoadedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                diagnosticsIncluded = includeDiagnostics,
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
                semanticCacheEnabled = SemanticCacheEnabled
            }
        });
    }

    // Helper methods

    internal List<List<string>> DetectCycles(Dictionary<string, List<string>> graph)
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

    // Builds the interface as syntax from the symbol model: each member's
    // declaration comes from SyntaxGenerator.Declaration (so parameter lists,
    // generics, ref kinds, and keyword escaping come out right) and
    // InterfaceDeclaration normalizes them to interface shape (bodies and
    // accessibility stripped). NormalizeWhitespace is correct here — fresh
    // generated syntax has no user formatting to preserve.
    private string GenerateInterfaceCode(string interfaceName, List<ISymbol> members, INamedTypeSymbol sourceType)
    {
        var generator = Microsoft.CodeAnalysis.Editing.SyntaxGenerator.GetGenerator(
            _workspace!, LanguageNames.CSharp);

        var memberDeclarations = members.Select(generator.Declaration).ToList();

        // A generic source type extracts to a generic interface: its members
        // reference the type parameters, so the interface must declare them (with
        // their constraints) or the generated code references undeclared T.
        var typeParameterNames = sourceType.TypeParameters.Select(tp => tp.Name).ToList();
        var interfaceDeclaration = generator.InterfaceDeclaration(
            interfaceName,
            typeParameters: typeParameterNames.Count > 0 ? typeParameterNames : null,
            accessibility: Accessibility.Public,
            members: memberDeclarations);

        foreach (var tp in sourceType.TypeParameters)
        {
            var kinds = Microsoft.CodeAnalysis.Editing.SpecialTypeConstraintKind.None;
            if (tp.HasReferenceTypeConstraint)
                kinds |= Microsoft.CodeAnalysis.Editing.SpecialTypeConstraintKind.ReferenceType;
            if (tp.HasValueTypeConstraint)
                kinds |= Microsoft.CodeAnalysis.Editing.SpecialTypeConstraintKind.ValueType;
            if (tp.HasConstructorConstraint)
                kinds |= Microsoft.CodeAnalysis.Editing.SpecialTypeConstraintKind.Constructor;
            var constraintTypes = tp.ConstraintTypes.Select(generator.TypeExpression).ToList();
            if (kinds != Microsoft.CodeAnalysis.Editing.SpecialTypeConstraintKind.None || constraintTypes.Count > 0)
            {
                interfaceDeclaration = generator.WithTypeConstraint(
                    interfaceDeclaration, tp.Name, kinds, constraintTypes);
            }
        }

        var containingNamespace = sourceType.ContainingNamespace;
        var unit = containingNamespace != null && !containingNamespace.IsGlobalNamespace
            ? generator.CompilationUnit(
                generator.NamespaceDeclaration(containingNamespace.ToDisplayString(), interfaceDeclaration))
            : generator.CompilationUnit(interfaceDeclaration);

        return unit.NormalizeWhitespace().ToFullString();
    }

    private void EnsureSolutionLoaded()
    {
        if (_solution == null)
        {
            throw new SolutionNotLoadedException();
        }
    }

    // The structured envelope the dispatcher returns when a tool runs before
    // load_solution. Static because no solution state exists yet by definition.
    internal static object CreateSolutionNotLoadedResponse()
    {
        return new
        {
            success = false,
            error = new RoslynError
            {
                Code = ErrorCodes.SolutionNotLoaded,
                Message = "No solution loaded. Call load_solution first or set DOTNET_SOLUTION_PATH environment variable.",
                Hint = "Use load_solution with the absolute path to a .sln or .slnx file, then retry this call."
            }
        };
    }

    /// <summary>Test-only accessor for the loaded solution. Do not use outside tests.</summary>
    internal Solution? GetSolutionForTesting() => _solution;

    /// <summary>
    /// Test-only override for the diagnostics cap (normally ROSLYN_MAX_DIAGNOSTICS).
    /// Setting the env var in tests would race parallel test classes constructing
    /// their own RoslynService instances; this scopes the override to one instance.
    /// </summary>
    internal void SetMaxDiagnosticsForTesting(int value) => _maxDiagnostics = value;

    /// <summary>
    /// Test-only entry point: hand RoslynService an already-configured Workspace
    /// (typically an AdhocWorkspace) so tests can exercise tools against in-memory
    /// code — including code that wouldn't compile and therefore can't be a file
    /// on disk. Production code path remains <see cref="LoadSolutionAsync"/>.
    /// </summary>
    internal void LoadFromWorkspaceForTesting(Workspace workspace)
    {
        _workspace?.Dispose();
        _documentCache.Clear();
        _compilationCache.Clear();
        _workspace = workspace;
        _solution = workspace.CurrentSolution;
        _solutionLoadedAt = DateTime.UtcNow;
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
    internal async Task<Compilation?> GetProjectCompilationAsync(
        Project project, CancellationToken cancellationToken = default)
    {
        if (_compilationCache.TryGetValue(project.Id, out var cached))
            return cached;

        var compilation = await ComputeAugmentedCompilationAsync(project, cancellationToken);
        if (compilation != null)
            _compilationCache[project.Id] = compilation;
        return compilation;
    }

    // The generator-aware compilation WITHOUT touching the cache. Safe for a proposed
    // (not-yet-applied) project version, whose ProjectId collides with the live one's —
    // caching it under that id would either return a stale live compilation or poison the
    // cache with an unapplied one. GetProjectCompilationAsync wraps this with the cache.
    private async Task<Compilation?> ComputeAugmentedCompilationAsync(
        Project project, CancellationToken cancellationToken)
    {
        var baseCompilation = await project.GetCompilationAsync(cancellationToken);
        if (baseCompilation == null) return null;

        var generators = project.AnalyzerReferences
            .SelectMany(ar => ar.GetGenerators(LanguageNames.CSharp))
            .ToImmutableArray();

        if (generators.IsEmpty)
            return baseCompilation;

        // Roslyn 5.3 workspaces run source generators themselves: the workspace
        // compilation already contains the generated trees, recognizable as trees
        // whose paths are not among the project's own documents. Running the
        // driver again on such a compilation adds every generated tree a second
        // time and poisons it with duplicate-definition errors.
        var projectDocumentPaths = new HashSet<string>(
            project.Documents.Where(d => d.FilePath != null).Select(d => d.FilePath!),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (baseCompilation.SyntaxTrees.Any(t => !projectDocumentPaths.Contains(t.FilePath)))
            return baseCompilation;

        try
        {
            var driver = CreateGeneratorDriver(project, generators);
            driver.RunGeneratorsAndUpdateCompilation(baseCompilation, out var updated, out _, cancellationToken);
            return updated;
        }
        catch (OperationCanceledException)
        {
            // A per-call timeout during generator execution must surface as cancellation,
            // not be swallowed and silently degrade to the base (pre-generator) compilation.
            throw;
        }
        catch (Exception ex)
        {
            // A misbehaving generator must not break the entire server.
            Console.Error.WriteLine($"[Warning] Source generator failed for '{project.Name}': {ex.Message}");
            return baseCompilation;
        }
    }

    // Truncates a snippet to at most maxChars WITHOUT splitting a UTF-16 surrogate
    // pair (an emoji / astral character at the boundary): a raw Substring(maxChars)
    // can cut between the high and low surrogate and emit a lone, invalid code unit.
    // Returns the text unchanged when it already fits; otherwise the kept prefix plus
    // the ellipsis. Shared by every snippet-truncating tool so they behave alike.
    internal static string TruncateForSnippet(string text, int maxChars, string ellipsis = "…")
    {
        if (text.Length <= maxChars) return text;
        var cut = maxChars;
        // If the last kept unit is a high surrogate, its low half is at index cut —
        // dropping the high half keeps the cut on a rune boundary.
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + ellipsis;
    }

    // The generator driver exactly as the augmented-compilation path runs it:
    // same generators, additional texts, parse options, and analyzer-config
    // options provider — so a timing run (get_source_generators) measures the
    // configuration the server actually compiles with.
    internal static CSharpGeneratorDriver CreateGeneratorDriver(
        Project project, ImmutableArray<ISourceGenerator> generators)
    {
        var additionalTexts = project.AdditionalDocuments
            .Select(d => (AdditionalText)new AdditionalTextFile(d))
            .ToImmutableArray();

        return CSharpGeneratorDriver.Create(
            generators: generators,
            additionalTexts: additionalTexts,
            parseOptions: (CSharpParseOptions)project.ParseOptions!,
            optionsProvider: project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
    }

    private Task<Document> GetDocumentAsync(string filePath)
    {
        var document = TryFindDocument(filePath);
        if (document == null)
            throw new FileNotFoundException($"Document not found in solution: {filePath}");
        return Task.FromResult(document);
    }

    // Returns the solution Document matching `filePath`, accepting either an absolute path
    // or a path relative to the solution directory. FormatPath emits solution-relative
    // paths by default, so callers can pass those values straight back without manual
    // absolutization. Returns null if no match — call sites decide how to report failure.
    internal Document? TryFindDocument(string filePath)
    {
        if (_documentCache.TryGetValue(filePath, out var cached))
            return cached;

        var resolved = filePath;
        if (!Path.IsPathRooted(filePath) && _solution?.FilePath != null)
        {
            var solutionDir = Path.GetDirectoryName(_solution.FilePath);
            if (!string.IsNullOrEmpty(solutionDir))
            {
                resolved = Path.GetFullPath(Path.Combine(solutionDir, filePath));
            }
        }

        var document = _solution!.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath != null &&
                string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(resolved), PathComparison));

        if (document == null) return null;

        if (SemanticCacheEnabled)
        {
            _documentCache[filePath] = document;
        }
        return document;
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
            debugInfo["nodeText"] = TruncateForSnippet(node.ToString(), 50, "...");

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
            result["typeKind"] = GetTypeKindString(typeSymbol);
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

    // Whether a type display string matches a type-name query. A simple-identifier
    // query (the common case) must match a whole word, so `int` does not match
    // `uint` and `Task` does not match `TaskResult`/`TaskCompletionSource`; a
    // complex query (generics, namespaces, arrays) falls back to a substring match.
    private static bool TypeDisplayMatches(string displayString, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        if (query.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                displayString,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(query)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return displayString.Contains(query, StringComparison.OrdinalIgnoreCase);
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
        int? maxResults,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var results = new List<object>();
        var maxResultsToReturn = maxResults ?? 100;
        var matchCount = 0;

        // Statistics for summary
        var countByKind = new Dictionary<string, int>();

        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            // Get all symbols in the project
            var allSymbols = compilation.GetSymbolsWithName(_ => true, SymbolFilter.All, cancellationToken);

            foreach (var symbol in allSymbols)
            {
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
                        kindMatches = kinds.Any(k => GetTypeKindString(namedType).Equals(k, StringComparison.OrdinalIgnoreCase));
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
                    if (!string.IsNullOrEmpty(returnType) && !TypeDisplayMatches(methodSymbol.ReturnType.ToDisplayString(), returnType))
                        matches = false;

                    // Filter by parameters
                    if (parameterIncludes != null && parameterIncludes.Count > 0)
                    {
                        var paramTypes = methodSymbol.Parameters.Select(p => p.Type.ToDisplayString()).ToList();
                        bool hasAllIncludes = parameterIncludes.All(include =>
                            paramTypes.Any(pt => TypeDisplayMatches(pt, include)));

                        if (!hasAllIncludes)
                            matches = false;
                    }

                    if (parameterExcludes != null && parameterExcludes.Count > 0)
                    {
                        var paramTypes = methodSymbol.Parameters.Select(p => p.Type.ToDisplayString()).ToList();
                        bool hasAnyExclude = parameterExcludes.Any(exclude =>
                            paramTypes.Any(pt => TypeDisplayMatches(pt, exclude)));

                        if (hasAnyExclude)
                            matches = false;
                    }
                }
                else if (symbol is IPropertySymbol propertySymbol)
                {
                    // Filter by type
                    if (!string.IsNullOrEmpty(type) && !TypeDisplayMatches(propertySymbol.Type.ToDisplayString(), type))
                        matches = false;
                }
                else if (symbol is IFieldSymbol fieldSymbol)
                {
                    // Filter by type
                    if (!string.IsNullOrEmpty(type) && !TypeDisplayMatches(fieldSymbol.Type.ToDisplayString(), type))
                        matches = false;
                }

                if (!matches)
                    continue;

                // Add to results
                var location = symbol.Locations.FirstOrDefault(loc => loc.IsInSource);
                if (location == null) continue;

                var lineSpan = location.GetLineSpan();
                var symbolKind = symbol is INamedTypeSymbol nt ? GetTypeKindString(nt) : symbol.Kind.ToString();

                countByKind[symbolKind] = countByKind.GetValueOrDefault(symbolKind) + 1;
                matchCount++;

                // Materialize the result only while under the cap; keep counting
                // past it so totalCount and truncated are honest.
                if (results.Count >= maxResultsToReturn)
                    continue;

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
            totalCount: matchCount,
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
            kind = GetTypeKindString(typeSymbol),
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
            "AutoMapper.Profile"
        };

        // Check if type implements any of these interfaces
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var fullName = iface.ToDisplayString();
            if (frameworkInterfaces.Any(fi => MatchesFrameworkType(fullName, fi)))
                return true;
        }

        // Check base types (for abstract base classes like BackgroundService)
        for (var baseType = typeSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            var baseTypeName = baseType.ToDisplayString();
            if (frameworkInterfaces.Any(fi => MatchesFrameworkType(baseTypeName, fi)))
                return true;
        }

        return false;
    }

    // Exact framework-type match that still accepts generic instantiations: the
    // display name equals the framework type, or is the framework type followed by a
    // generic argument list (IRequestHandler vs IRequestHandler<Cmd, Res>). A substring
    // match would false-positive on any type whose name merely contains a framework
    // type's name.
    private static bool MatchesFrameworkType(string displayName, string frameworkType) =>
        displayName == frameworkType
        || displayName.StartsWith(frameworkType + "<", StringComparison.Ordinal);

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
                // Exact class-name match (with the optional 'Attribute' suffix), not a
                // substring test: a user attribute merely containing "Controller",
                // "Collection", "Route" etc. must not be mistaken for the framework one
                // and silently dropped from the unused-code report.
                if (string.Equals(attributeName, frameworkAttr, StringComparison.Ordinal)
                    || string.Equals(attributeName, frameworkAttr + "Attribute", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
