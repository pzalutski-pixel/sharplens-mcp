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
    // How faithfully the loaded solution reflects disk (declared vs loaded projects + MSBuild
    // load diagnostics). Persisted so health_check can report it without a reload.
    private LoadFidelity? _loadFidelity;
    // The last load that did not complete, until a load succeeds. Reported by health_check.
    private LoadFailure? _lastLoadFailure;

    // Single source for the semantic-cache flag, read once at construction by both the
    // cache gate and the health_check configuration report so the reported state can't
    // drift from the enforced one. On unless SHARPLENS_ENABLE_SEMANTIC_CACHE (legacy
    // ROSLYN_ENABLE_SEMANTIC_CACHE) is false, case-insensitively.
    private readonly bool _semanticCacheEnabled;
    private bool SemanticCacheEnabled => _semanticCacheEnabled;

    public RoslynService()
    {
        _maxDiagnostics = EnvironmentConfig.GetInt("SHARPLENS_MAX_DIAGNOSTICS", "ROSLYN_MAX_DIAGNOSTICS",
            defaultValue: 100, minimum: 1, maximum: int.MaxValue);
        // The same parser McpServer uses for the enforced per-call budget, so the value
        // health_check reports can't diverge from the timeout actually enforced.
        _timeoutSeconds = EnvironmentConfig.GetToolCallTimeoutSeconds();
        _useAbsolutePaths = EnvironmentConfig.GetBool("SHARPLENS_ABSOLUTE_PATHS", "ROSLYN_ABSOLUTE_PATHS", defaultValue: false);
        _semanticCacheEnabled = EnvironmentConfig.GetBool("SHARPLENS_ENABLE_SEMANTIC_CACHE", "ROSLYN_ENABLE_SEMANTIC_CACHE", defaultValue: true);
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
    /// Creates a standardized success response with metadata whose truncation is
    /// inferred from the counts.
    /// </summary>
    internal static object CreateSuccessResponse(object data, string[]? suggestedNextTools = null,
        int? totalCount = null, int? returnedCount = null, string? verbosity = null,
        bool? truncated = null)
    {
        // Truncated derives from the counts (returnedCount < totalCount), OR-combined
        // with an explicit `truncated` for a complete-but-bounded result (get_call_graph,
        // get_outgoing_calls) whose whole array has totalCount == returnedCount. The
        // inference reads a last page, a filtered list counted against the unfiltered
        // total, and a batch with failed items as truncated; the ResultCounts overload
        // below states truncation explicitly.
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
    /// Creates a standardized success response whose counts and truncation the tool
    /// states explicitly: meta.truncated is <see cref="ResultCounts.Truncated"/>, never
    /// inferred from the counts (see <see cref="ResultCounts"/> for the factories).
    /// </summary>
    internal static object CreateSuccessResponse(object data, ResultCounts counts,
        string[]? suggestedNextTools = null, string? verbosity = null)
    {
        return new ToolSuccessResponse(
            success: true,
            data: data,
            meta: new ResponseMetadata
            {
                TotalCount = counts.TotalCount,
                ReturnedCount = counts.ReturnedCount,
                Truncated = counts.Truncated,
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
    /// Finds a type by name: ResolveTypeByNameAsync reduced to (type, error). The error
    /// (second tuple element) is AMBIGUOUS_TYPE or a TYPE_NOT_FOUND listing partial
    /// matches; both elements are null when nothing matches. The ShadowNotice of a
    /// source type that shadows a framework type is not part of this shape — a tool
    /// that surfaces it calls ResolveTypeByNameAsync.
    /// </summary>
    private async Task<(INamedTypeSymbol? type, object? ambiguityError)> FindTypeByNameAsync(
        string typeName, CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        return (resolution.Type, resolution.Error);
    }

    /// <summary>
    /// Resolves a by-name type reference. Accepts every spelling TypeNameQuery parses —
    /// keyword aliases, metadata names, C# display names (nested Outer.Inner, generic
    /// Repo&lt;T&gt;) and dotted suffixes of them. Tiers, first match wins:
    /// 1. an exact metadata name (fully-qualified, source or referenced);
    /// 2. an exact source type name (or dotted suffix) — when several match it is
    ///    AMBIGUOUS_TYPE, the same-named framework types listed among the candidates;
    ///    when one matches it wins, and the same-named framework types are reported in
    ///    ShadowedMatches;
    /// 3. an exact public framework/package type name (or dotted suffix; a bare name
    ///    matches top-level framework types only);
    /// 4. a partial (contains) source name never resolves: TYPE_NOT_FOUND lists the
    ///    partial matches as suggestions.
    /// A name matching several distinct types is never silently picked — downstream
    /// refactorings would act on the wrong type.
    /// </summary>
    internal async Task<TypeResolution> ResolveTypeByNameAsync(
        string typeName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // Prefer base compilations so SymbolFinder (FindDerivedClasses, FindImplementations,
        // FindReferences) recognizes the returned symbol. The augmented compilation from
        // GetProjectCompilationAsync has different symbol identity that SymbolFinder won't
        // resolve. Fall back to augmented compilations only when no base compilation has
        // a source type of that name (e.g., it's purely source-generated): the base pass
        // resolves source types only, so a generated source type still wins over a
        // framework namesake.
        var baseCompilations = new List<Compilation>();
        foreach (var project in _solution!.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation != null) baseCompilations.Add(compilation);
        }

        var resolved = ResolveTypeAcross(baseCompilations, typeName, frameworkResolves: false);
        if (resolved.Type != null || resolved.Error != null) return resolved.Resolution;

        var augmentedCompilations = new List<Compilation>();
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation != null) augmentedCompilations.Add(compilation);
        }

        resolved = ResolveTypeAcross(augmentedCompilations, typeName, frameworkResolves: true);
        if (resolved.Type != null || resolved.Error != null || resolved.PartialMatches.Count == 0)
            return resolved.Resolution;
        return new TypeResolution(typeName, null, PartialMatchError(typeName, resolved.PartialMatches), Array.Empty<string>());
    }

    // One resolution pass over a set of compilations (see ResolveTypeByNameAsync).
    // Without frameworkResolves the pass stops after the source tiers (framework types
    // still feed the shadowed list and the ambiguity candidates).
    private TypeResolutionPass ResolveTypeAcross(List<Compilation> compilations, string typeName, bool frameworkResolves)
    {
        TypeResolutionPass Resolved(INamedTypeSymbol? type, object? error, IReadOnlyList<string>? shadowed = null) =>
            new(new TypeResolution(typeName, type, error, shadowed ?? Array.Empty<string>()), new List<INamedTypeSymbol>());

        var query = TypeNameQuery.Parse(typeName);
        if (query == null) return Resolved(null, null);

        // Strategy 1: an exact metadata name — the keyword alias's (int →
        // System.Int32), or each metadata name the spelling can denote
        // (Lib.Outer.Inner → Lib.Outer+Inner, Lib.Repo<T> → Lib.Repo`1). Genuine
        // ambiguity is two readings denoting different types, or two different
        // SOURCE assemblies DECLARING the same name — not the same referenced/BCL
        // type surfacing under different facade assemblies across target frameworks
        // (e.g. System.String via System.Private.CoreLib vs a netstandard facade).
        var metadataNames = TypeNameQuery.KeywordMetadataName(typeName) is { } aliased
            ? new[] { aliased }
            : query.MetadataNameCandidates().ToArray();
        var metadataMatches = new List<(INamedTypeSymbol type, Compilation compilation)>();
        foreach (var compilation in compilations)
        {
            foreach (var metadataName in metadataNames)
            {
                if (compilation.GetTypeByMetadataName(metadataName) is { } exact) metadataMatches.Add((exact, compilation));
            }
        }
        if (metadataMatches.Count > 0)
        {
            var byFullName = metadataMatches
                .GroupBy(m => FullMetadataName(m.type), StringComparer.Ordinal)
                .ToList();
            if (byFullName.Count > 1)
                return Resolved(null, AmbiguousTypeError(typeName, byFullName.Select(g => g.First().type).ToList()));

            // Only count matches the compilation declares in its own source; if several
            // distinct source assemblies do, the one name denotes several types.
            var sourceDeclarations = metadataMatches
                .Where(m => SymbolEqualityComparer.Default.Equals(m.type.ContainingAssembly, m.compilation.Assembly)
                         && m.type.Locations.Any(l => l.IsInSource))
                .Select(m => m.type)
                .GroupBy(t => t.ContainingAssembly?.Name ?? "", StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            if (sourceDeclarations.Count > 1) return Resolved(null, AmbiguousTypeError(typeName, sourceDeclarations));
            return Resolved(sourceDeclarations.Count == 1 ? sourceDeclarations[0] : metadataMatches[0].type, null);
        }

        // Strategies 2 and 3: a source type, else a public framework/package type, whose
        // name — and, for a qualified or generic spelling, containing types/namespaces
        // and arity — matches (case-insensitive). A single source match wins over the
        // framework namesakes, which are disclosed; several source matches are ambiguous
        // together with them.
        var sourceMatches = CollectSourceTypes(compilations,
            name => name.Equals(query.SimpleName, StringComparison.OrdinalIgnoreCase))
            .Where(query.Matches)
            .ToList();
        var frameworkMatches = CollectFrameworkTypes(compilations, query);
        if (sourceMatches.Count == 1)
        {
            var shadowed = frameworkMatches
                .Select(t => t.ToDisplayString())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return Resolved(sourceMatches[0], null, shadowed);
        }
        if (sourceMatches.Count > 1) return Resolved(null, AmbiguousTypeError(typeName, sourceMatches.Concat(frameworkMatches).ToList()));
        if (!frameworkResolves) return Resolved(null, null);
        if (frameworkMatches.Count == 1) return Resolved(frameworkMatches[0], null);
        if (frameworkMatches.Count > 1) return Resolved(null, AmbiguousTypeError(typeName, frameworkMatches));

        // Strategy 4: partial (contains) source matches are suggestions, never a resolution.
        var partial = CollectSourceTypes(compilations,
            name => name.Contains(typeName, StringComparison.OrdinalIgnoreCase));
        return new TypeResolutionPass(new TypeResolution(typeName, null, null, Array.Empty<string>()), partial);
    }

    // The public framework/package types (not solution source) matching the query,
    // one per metadata name across the compilations. A single-segment name matches
    // top-level types only: outside its container a nested type is reachable only
    // qualified (List.Enumerator), and user code is never inside a framework type.
    private static List<INamedTypeSymbol> CollectFrameworkTypes(List<Compilation> compilations, TypeNameQuery query)
    {
        var byMetadataName = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            foreach (var type in MetadataTypeIndex.For(compilation).Named(query.SimpleName))
            {
                if (query.Segments.Count == 1 && type.ContainingType != null) continue;
                if (!query.Matches(type)) continue;
                byMetadataName.TryAdd(FullMetadataName(type), type);
            }
        }
        return byMetadataName.Values.ToList();
    }

    private object PartialMatchError(string typeName, List<INamedTypeSymbol> partialMatches)
    {
        const int maxShown = 10;
        var shown = partialMatches
            .Select(c => c.ToDisplayString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .Take(maxShown)
            .ToList();
        var truncated = partialMatches.Count > maxShown;
        return CreateErrorResponse(
            ErrorCodes.TypeNotFound,
            $"Type not found: {typeName}",
            hint: $"No type is named '{typeName}'. Partial matches: {string.Join(", ", shown)}"
                  + (truncated ? $" ({shown.Count} of {partialMatches.Count} shown)" : "")
                  + ". Repeat the call with one of them.",
            context: new
            {
                typeName,
                totalSuggestions = partialMatches.Count,
                suggestionsShown = shown.Count,
                truncated,
                suggestions = shown
            });
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

    // The location a tool reports a symbol at, as { filePath, line, column }, or null for a
    // symbol without source (metadata). See SymbolDisplayLocation for which part.
    private object? GetSymbolLocation(ISymbol symbol) => FormatLocation(SymbolDisplayLocation(symbol));

    // The source location that stands for a symbol: the part that carries the body of a
    // partial member, within the solution's own documents (GetPreferredSourceLocation),
    // else the earliest location of any part — a symbol declared only in generator output
    // is shown there. Null for a symbol without source.
    private Location? SymbolDisplayLocation(ISymbol symbol) =>
        GetPreferredSourceLocation(symbol) ?? SymbolParts.SourceLocations(symbol).FirstOrDefault();

    // A source location as { filePath, line, column } (zero-based, the path through
    // FormatPath); null for a location outside source.
    private object? FormatLocation(Location? location)
    {
        if (location is not { IsInSource: true }) return null;
        var lineSpan = location.GetLineSpan();
        return new
        {
            filePath = FormatPath(lineSpan.Path),
            line = lineSpan.StartLinePosition.Line,
            column = lineSpan.StartLinePosition.Character
        };
    }

    // The ResultLocation of a reported location (the formatted path), for ordering results
    // with CanonicalOrder.ByLocation exactly as the caller sees them; a symbol or location
    // without source sorts as the empty path.
    private ResultLocation ReportedLocation(Location? location)
    {
        if (location is not { IsInSource: true }) return new ResultLocation("", 0, 0);
        var lineSpan = location.GetLineSpan();
        return new ResultLocation(FormatPath(lineSpan.Path), lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character);
    }

    // The target framework of the project that compiled a symbol (LogicalProject's
    // "<name>(<tfm>)" suffix), or null for a symbol of a single-target project or outside
    // the solution's compilations.
    private string? TargetFrameworkOf(ISymbol symbol) =>
        symbol.ContainingAssembly is { } assembly && _solution?.GetProject(assembly) is { } project
            ? LogicalProject.TargetFrameworkOf(project)
            : null;

    // Adds targetFrameworks to a result entry when the result occurred in a multi-targeted
    // project (MergedResult.TargetFrameworks is empty for a single-target project).
    private static Dictionary<string, object?> WithTargetFrameworks(
        Dictionary<string, object?> entry, IReadOnlyList<string> targetFrameworks)
    {
        if (targetFrameworks.Count > 0) entry["targetFrameworks"] = targetFrameworks;
        return entry;
    }


    // The solution file kinds MSBuildWorkspace.OpenSolutionAsync reads: a classic solution,
    // an XML solution, and a solution filter.
    private static readonly string[] SolutionFileExtensions = { ".sln", ".slnx", ".slnf" };

    // Loads a solution transactionally: the new workspace is built and opened on the side,
    // and only a load that completes replaces the current solution (then the previous
    // workspace is disposed). A rejected, failed, or cancelled load disposes the workspace
    // it created and leaves the previously loaded solution, if any, fully usable; the
    // failure is recorded for health_check.
    public async Task<object> LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            return RecordLoadFailure(solutionPath ?? "", ErrorCodes.InvalidParameter,
                "solutionPath is empty. load_solution takes the path of a .sln, .slnx or .slnf file.",
                hint: "Pass the absolute path of the solution file.");
        }

        if (!SolutionFileExtensions.Contains(Path.GetExtension(solutionPath), StringComparer.OrdinalIgnoreCase))
        {
            return RecordLoadFailure(solutionPath, ErrorCodes.InvalidParameter,
                $"Not a solution file: {solutionPath}. load_solution takes a .sln, .slnx or .slnf file.",
                hint: "To analyze a single project, create a solution for it first: dotnet new sln, then dotnet sln add <project>.");
        }

        if (!File.Exists(solutionPath))
        {
            return RecordLoadFailure(solutionPath, ErrorCodes.FileNotFound,
                $"Solution file not found: {solutionPath}",
                hint: "Verify the path is correct and the file exists");
        }

        // MSBuildWorkspace loads the solution; on commit the loaded solution moves into a
        // SourceFileWorkspace, the base-typed _workspace that owns it (and disposes the
        // loader) from then on - see SourceFileWorkspace. The HostServices are MEF-composed
        // from Roslyn's Workspaces + Features assemblies so the language services that
        // built-in refactoring providers depend on (e.g. IExtractMethodService<TStatementSyntax>)
        // are wired up — without this, the C# refactoring providers loaded for
        // get_code_actions_at_position fire but their language-service lookups return null
        // and they emit no actions.
        var hostServices = Microsoft.CodeAnalysis.Host.Mef.MefHostServices.Create(GetRoslynMefAssemblies());
        var msbuild = MSBuildWorkspace.Create(hostServices);
        // Log to stderr for the server operator; the same diagnostics are read from
        // msbuild.Diagnostics below and surfaced to the caller, so a dropped project is
        // never invisible in the response.
        msbuild.RegisterWorkspaceFailedHandler(args =>
        {
            Console.Error.WriteLine($"[Warning] Workspace: {args.Diagnostic.Message}");
        });

        var committed = false;
        try
        {
            Solution solution;
            try
            {
                solution = await msbuild.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The solution file itself could not be read (malformed content, an unsupported
                // format version). That is a property of the input, reported in-band.
                return RecordLoadFailure(solutionPath, ErrorCodes.AnalysisFailed,
                    $"Could not load solution '{solutionPath}': {ex.Message}",
                    hint: "Check that the file is a valid solution file. The previously loaded solution, if any, is still loaded and unchanged.");
            }
            WorkspaceOpenedForTesting?.Invoke(msbuild);
            cancellationToken.ThrowIfCancellationRequested();

            var projectCount = solution.ProjectIds.Count;
            var documentCount = solution.Projects.Sum(p => p.DocumentIds.Count);
            var fidelity = await ComputeLoadFidelityAsync(solutionPath, solution, msbuild.Diagnostics, cancellationToken);

            // Commit: from here the new solution is the loaded one, owned by a
            // SourceFileWorkspace that also takes ownership of (and later disposes) msbuild.
            var previous = _workspace;
            _workspace = new SourceFileWorkspace(hostServices, WorkspaceKind.MSBuild, solution, loader: msbuild);
            _solution = _workspace.CurrentSolution;
            _loadFidelity = fidelity;
            _solutionLoadedAt = DateTime.UtcNow;
            _lastLoadFailure = null;
            _documentCache.Clear();
            _compilationCache.Clear();
            committed = true;
            previous?.Dispose();

            return BuildLoadSolutionResponse(solutionPath, projectCount, documentCount, fidelity);
        }
        catch (OperationCanceledException)
        {
            RecordLoadFailure(solutionPath, ErrorCodes.Timeout,
                $"Loading '{solutionPath}' was cancelled before it finished (the time budget ran out); the previously loaded solution, if any, is unchanged.",
                hint: null);
            throw;
        }
        finally
        {
            if (!committed) msbuild.Dispose();
        }
    }

    // Records a load that did not complete (for health_check) and returns its error envelope.
    private object RecordLoadFailure(string solutionPath, string code, string message, string? hint)
    {
        _lastLoadFailure = new LoadFailure(solutionPath, code, message, DateTime.UtcNow);
        return CreateErrorResponse(code, message, hint, context: new { solutionPath });
    }

    // The failure is also recorded when it happens outside LoadSolutionAsync — the startup
    // auto-load resolving DOTNET_SOLUTION_PATH, or exceeding its time budget.
    internal void RecordLoadFailure(LoadFailure failure) => _lastLoadFailure = failure;

    // Timestamps in tool output: invariant ISO-8601 UTC, independent of the current culture.
    internal static string FormatTimestamp(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    private object BuildLoadSolutionResponse(string solutionPath, int projectCount, int documentCount, LoadFidelity fidelity)
    {
        return CreateSuccessResponse(
            data: new
            {
                solutionPath,
                projectCount,
                documentCount,
                declaredProjectCount = fidelity.DeclaredProjectCount,
                partial = fidelity.IsPartial,
                loadFailures = fidelity.Failures,
                loadWarnings = fidelity.Warnings,
                loadWarningCount = fidelity.Warnings.Count
            },
            suggestedNextTools: fidelity.IsPartial
                ? new[]
                {
                    "PARTIAL LOAD: the loaded solution is incomplete - see loadFailures; fix the projects or run dotnet restore, then reload",
                    "get_project_structure to see what did load"
                }
                : new[]
                {
                    "get_project_structure to see solution structure",
                    "search_symbols to find code",
                    "get_diagnostics to check for issues"
                }
        );
    }

    // Classifies the load by what actually loaded (see LoadFidelityClassifier): the projects
    // the solution file declares, each loaded project's state, the workspace diagnostics, and
    // each project's NuGet restore log. Honors cancellation between projects.
    private static async Task<LoadFidelity> ComputeLoadFidelityAsync(
        string solutionPath, Solution solution, IReadOnlyList<WorkspaceDiagnostic> workspaceDiagnostics,
        CancellationToken cancellationToken)
    {
        var declared = await SolutionProjectReader.ReadAsync(solutionPath, cancellationToken);

        var diagnostics = workspaceDiagnostics.ToList();
        var restoreLog = new List<RestoreLogEntry>();
        var unrestored = new HashSet<string>(LoadFidelityClassifier.PathComparer);
        var completeProjects = solution.Projects
            .Where(p => p.FilePath != null && !IsPlaceholderProject(p))
            .GroupBy(p => Path.GetFullPath(p.FilePath!), LoadFidelityClassifier.PathComparer);
        foreach (var projectInstances in completeProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entries, warning, found) = ProjectAssetsReader.Read(
                projectInstances.Key, projectInstances.First().CompilationOutputInfo.AssemblyPath);
            restoreLog.AddRange(entries);
            if (warning != null) diagnostics.Add(new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, warning));
            if (!found && ProjectFileStyle.IsSdkStyle(projectInstances.Key)) unrestored.Add(projectInstances.Key);
        }

        var loaded = new List<LoadedProjectState>();
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.FilePath != null)
            {
                loaded.Add(DescribeLoadedProject(project, unrestored.Contains(Path.GetFullPath(project.FilePath))));
            }
        }

        return LoadFidelityClassifier.Classify(declared, loaded, diagnostics, restoreLog);
    }

    // MSBuildWorkspace substitutes an empty project (no documents, no output path) for a
    // project whose MSBuild evaluation failed.
    private static bool IsPlaceholderProject(Project project) =>
        project.OutputFilePath == null && project.DocumentIds.Count == 0;

    // Whether the project's references can bind its code: an unrestored SDK project loads
    // with no metadata references at all, and without a reference defining System.Object
    // nothing binds. The probe compilation holds only the references, so resolving the core
    // library reads reference metadata the workspace already caches, not the project's source.
    // isUnrestored: the project is SDK-style and has no project.assets.json.
    private static LoadedProjectState DescribeLoadedProject(Project project, bool isUnrestored)
    {
        var isPlaceholder = IsPlaceholderProject(project);
        var referenceCount = project.MetadataReferences.Count;
        var resolvesCoreLibrary = !isPlaceholder && referenceCount > 0
            && CSharpCompilation.Create("SharpLensReferenceProbe", references: project.MetadataReferences)
                .GetSpecialType(SpecialType.System_Object).TypeKind != TypeKind.Error;
        return new LoadedProjectState(project.FilePath!, project.Name, isPlaceholder, referenceCount, resolvesCoreLibrary, isUnrestored);
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
                workspace = (object?)null,
                lastLoadFailure = BuildLastLoadFailureReport(),
                configuration = BuildConfigurationReport()
            });
        }

        // Default is O(1): only load state, counts, and configuration. The
        // diagnostics sweep compiles up to five projects (~tens of seconds on a
        // large solution), so it runs only when includeDiagnostics is set.
        int? errorCount = null;
        int? warningCount = null;
        int? sampledProjects = null;
        int? totalProjects = null;

        if (includeDiagnostics)
        {
            // A project is a project FILE: a multi-targeted project loads once per target
            // framework, so the sample takes the first five files with all their target
            // frameworks, and a diagnostic reported identically by several of them is
            // counted once. A compilation failure propagates (the call fails) rather than
            // being reported as a clean partial count.
            var projectFiles = _solution.Projects
                .GroupBy(p => p.FilePath ?? p.Name, LoadFidelityClassifier.PathComparer)
                .ToList();
            var sample = projectFiles.Take(HealthDiagnosticsSampleSize).ToList();
            var errors = new HashSet<string>(StringComparer.Ordinal);
            var warnings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var project in sample.SelectMany(projectFile => projectFile))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var compilation = await GetProjectCompilationAsync(project, cancellationToken);
                if (compilation == null) continue;
                foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
                {
                    if (diagnostic.Severity == DiagnosticSeverity.Error) errors.Add(DiagnosticIdentity(diagnostic));
                    else if (diagnostic.Severity == DiagnosticSeverity.Warning) warnings.Add(DiagnosticIdentity(diagnostic));
                }
            }
            errorCount = errors.Count;
            warningCount = warnings.Count;
            sampledProjects = sample.Count;
            totalProjects = projectFiles.Count;
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
                declaredProjects = _loadFidelity?.DeclaredProjectCount,
                partial = _loadFidelity?.IsPartial ?? false,
                loadFailures = _loadFidelity?.Failures ?? (IReadOnlyList<string>)Array.Empty<string>(),
                loadWarnings = _loadFidelity?.Warnings ?? (IReadOnlyList<string>)Array.Empty<string>(),
                loadWarningCount = _loadFidelity?.Warnings.Count ?? 0,
                documents = documentCount,
                loadedAt = _solutionLoadedAt is { } loadedAtUtc ? FormatTimestamp(loadedAtUtc) : null,
                diagnosticsIncluded = includeDiagnostics,
                diagnosticsSampledProjects = sampledProjects,
                diagnosticsTotalProjects = totalProjects,
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
            lastLoadFailure = BuildLastLoadFailureReport(),
            configuration = BuildConfigurationReport()
        });
    }

    // How many project files health_check's includeDiagnostics sweep compiles.
    private const int HealthDiagnosticsSampleSize = 5;

    // One diagnostic's identity across target-framework compilations of the same source:
    // the same id, at the same place, with the same message, is the same finding.
    private static string DiagnosticIdentity(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return string.Join("|", diagnostic.Id, span.Path, diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    // The last load that did not complete since the last successful one, or null.
    private object? BuildLastLoadFailureReport() => _lastLoadFailure is not { } failure
        ? null
        : new
        {
            solutionPath = failure.SolutionPath,
            code = failure.Code,
            message = failure.Message,
            failedAt = FormatTimestamp(failure.FailedAtUtc)
        };

    // The effective environment configuration, reported by health_check whether or not a
    // solution is loaded: these are server settings, independent of any solution.
    private object BuildConfigurationReport() => new
    {
        maxDiagnostics = _maxDiagnostics,
        timeoutSeconds = _timeoutSeconds,
        semanticCacheEnabled = SemanticCacheEnabled,
        absolutePaths = _useAbsolutePaths
    };

    // Helper methods

    // The cycles of a dependency graph (node -> the nodes it depends on): every elementary
    // cycle, up to DependencyCycles.DefaultMaxCycles, and every strongly connected
    // component (see DependencyCycles and CycleAnalysis).
    internal CycleAnalysis DetectCycles(Dictionary<string, List<string>> graph) => DependencyCycles.Analyze(graph);

    // Builds the interface as syntax from the symbol model: each member's
    // declaration comes from SyntaxGenerator.Declaration (so parameter lists,
    // generics, ref kinds, and keyword escaping come out right), reduced to what an
    // interface member can declare (AsInterfaceMemberDeclaration), and
    // InterfaceDeclaration normalizes them to interface shape (bodies and
    // accessibility stripped). NormalizeWhitespace is correct here — fresh
    // generated syntax has no user formatting to preserve — with the source
    // document's line break.
    private string GenerateInterfaceCode(string interfaceName, List<ISymbol> members, INamedTypeSymbol sourceType, string lineBreak)
    {
        var generator = Microsoft.CodeAnalysis.Editing.SyntaxGenerator.GetGenerator(
            _workspace!, LanguageNames.CSharp);

        var memberDeclarations = members
            .Select(member => AsInterfaceMemberDeclaration(generator.Declaration(member), member))
            .ToList();

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

        return unit.NormalizeWhitespace(eol: lineBreak).ToFullString();
    }

    // A member declaration as an interface declares it: a property or indexer keeps only
    // the accessors every caller of the member can use (a private or protected setter is
    // dropped, the kept accessors lose their modifiers), and implementation-only
    // modifiers (async, virtual, override, sealed, abstract, new, extern) are removed.
    private static SyntaxNode AsInterfaceMemberDeclaration(SyntaxNode declaration, ISymbol member)
    {
        if (declaration is BasePropertyDeclarationSyntax { AccessorList: { } accessorList } property
            && member is IPropertySymbol propertySymbol)
        {
            var kept = accessorList.Accessors
                .Where(accessor => (accessor.IsKind(SyntaxKind.GetAccessorDeclaration) ? propertySymbol.GetMethod : propertySymbol.SetMethod)
                    ?.DeclaredAccessibility == Accessibility.Public)
                .Select(accessor => accessor.WithModifiers(default))
                .ToList();
            declaration = property.WithAccessorList(accessorList.WithAccessors(SyntaxFactory.List(kept)));
        }
        if (declaration is MemberDeclarationSyntax memberDeclaration)
        {
            var modifiers = memberDeclaration.Modifiers.Where(m => !m.IsKind(SyntaxKind.AsyncKeyword)
                && !m.IsKind(SyntaxKind.VirtualKeyword) && !m.IsKind(SyntaxKind.OverrideKeyword)
                && !m.IsKind(SyntaxKind.SealedKeyword) && !m.IsKind(SyntaxKind.AbstractKeyword)
                && !m.IsKind(SyntaxKind.NewKeyword) && !m.IsKind(SyntaxKind.ExternKeyword));
            declaration = memberDeclaration.WithModifiers(SyntaxFactory.TokenList(modifiers));
        }
        return declaration;
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
                Hint = "Use load_solution with the absolute path to a .sln, .slnx or .slnf file, then retry this call. health_check reports lastLoadFailure if a load did not complete."
            }
        };
    }

    /// <summary>Test-only accessor for the loaded solution. Do not use outside tests.</summary>
    internal Solution? GetSolutionForTesting() => _solution;

    /// <summary>Test-only accessor for the live workspace. Do not use outside tests.</summary>
    internal Workspace? GetWorkspaceForTesting() => _workspace;

    /// <summary>
    /// Test-only hook invoked with the new workspace right after it opened the solution and
    /// before the load commits — a deterministic point to cancel a load. Null in production.
    /// </summary>
    internal Action<Workspace>? WorkspaceOpenedForTesting { get; set; }

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
        _lastLoadFailure = null;
        // An in-memory workspace has no MSBuild load step, so every project is present.
        _loadFidelity = LoadFidelity.Complete(_solution.ProjectIds.Count);
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
    // A path that cannot name a file (empty, whitespace, or containing a character no
    // path may hold, such as NUL) matches nothing.
    // A caller-supplied path with its separators made native. Windows accepts both '\' and
    // '/'; elsewhere '\' is an ordinary file-name character, but in a path a client passes
    // (often copied from a solution file or a Windows tool) it is a Windows separator, so it
    // becomes '/'. Every resolution of a caller-supplied path goes through this.
    internal static string WithNativeSeparators(string path) =>
        Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);

    internal Document? TryFindDocument(string filePath)
    {
        if (!IsNameablePath(filePath)) return null;

        if (_documentCache.TryGetValue(filePath, out var cached))
            return cached;

        var resolved = WithNativeSeparators(filePath);
        if (!Path.IsPathRooted(resolved) && _solution?.FilePath != null)
        {
            var solutionDir = Path.GetDirectoryName(_solution.FilePath);
            if (!string.IsNullOrEmpty(solutionDir))
            {
                resolved = Path.GetFullPath(Path.Combine(solutionDir, resolved));
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

    // Whether a caller-supplied path can name a file at all: not empty or whitespace,
    // and free of characters no path may contain (NUL and the other invalid path
    // characters), which Path.GetFullPath would reject by throwing.
    internal static bool IsNameablePath(string? filePath) =>
        !string.IsNullOrWhiteSpace(filePath) && filePath.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    // Resolves the symbol a cursor position denotes, the way an editor's
    // go-to-definition does. The position selects the token it touches: the token whose
    // span contains it, except that a word token (identifier or keyword) ending exactly
    // there — a caret just past an identifier — wins over punctuation starting there;
    // documentation-comment trivia is searched, so a cref name is a token; and
    // indentation before a line's first token selects that token. The token decides:
    //   - in a documentation comment, a cref (or paramref/typeparamref) name → its
    //     target; any other comment text → nothing;
    //   - a declaration's identifier, modifier or keyword (private, readonly, class,
    //     operator, an indexer's this, a destructor's ~) → the declared symbol; a
    //     field, event or local declaration of several variables → nothing, with a hint
    //     naming them;
    //   - another word token, or the operator of an operator expression → the symbol
    //     its node binds to (a reference, this/base, a predefined type, new, a
    //     user-defined operator);
    //   - other punctuation, whitespace, comments, literals and end-of-file → nothing:
    //     never the enclosing or the next declaration.
    // On failure, hint names the nearest token on the line that does resolve, by its
    // zero-based line and line-relative column.
    private (ISymbol? symbol, object debugInfo, string? hint) TryFindSymbolForRename(
        SyntaxTree syntaxTree,
        SemanticModel semanticModel,
        int position,
        int line,
        int column)
    {
        var root = syntaxTree.GetRoot();
        var text = syntaxTree.GetText();
        var token = TouchingToken(root, text, position);
        var debugInfo = new Dictionary<string, object>
        {
            ["requestedPosition"] = new { line, column },
            ["tokenFound"] = token.Text,
            ["tokenKind"] = token.Kind().ToString(),
            ["tokenSpan"] = new { start = token.SpanStart, end = token.Span.End }
        };
        if (token.Parent is { } node)
        {
            debugInfo["nodeKind"] = node.Kind().ToString();
            debugInfo["nodeText"] = TruncateForSnippet(node.ToString(), 50, "...");
        }

        var (symbol, reason) = BindToken(token, semanticModel);
        if (symbol != null) return (symbol, debugInfo, null);

        var hint = reason ?? NearestResolvableToken(root, text, semanticModel, position, token) switch
        {
            { } nearest => $"Try positioning the cursor on '{nearest.Text}' " +
                           $"(line {text.Lines.GetLinePosition(nearest.SpanStart).Line}, " +
                           $"column {text.Lines.GetLinePosition(nearest.SpanStart).Character}).",
            null => $"Line {line} has no identifier that resolves to a symbol; place the cursor on one, or use search_symbols to find symbols by name."
        };
        return (null, debugInfo, hint);
    }

    private static SyntaxToken TouchingToken(SyntaxNode root, SourceText text, int position)
    {
        var token = root.FindToken(position, findInsideTrivia: true);
        if (token.Span.Contains(position) && IsWordToken(token)) return token;

        if (position > 0)
        {
            var previous = root.FindToken(position - 1, findInsideTrivia: true);
            if (previous.Span.Length > 0 && previous.Span.End == position && IsWordToken(previous)) return previous;
        }

        if (token.Span.Contains(position)) return token;

        // Indentation before the first token of the line the token starts on.
        if (position < token.SpanStart
            && text.Lines.GetLineFromPosition(position).LineNumber == text.Lines.GetLineFromPosition(token.SpanStart).LineNumber
            && text.ToString(TextSpan.FromBounds(position, token.SpanStart)).All(char.IsWhiteSpace))
        {
            return token;
        }
        return default;
    }

    private static bool IsWordToken(SyntaxToken token) =>
        token.IsKind(SyntaxKind.IdentifierToken) || SyntaxFacts.IsKeywordKind(token.Kind());

    // Binds a token to the symbol it denotes (see TryFindSymbolForRename), or returns
    // why it denotes none when that is more specific than "no symbol here".
    private static (ISymbol? symbol, string? reason) BindToken(SyntaxToken token, SemanticModel model)
    {
        if (token.Parent is not { } node) return (null, null);

        if (token.IsPartOfStructuredTrivia())
        {
            if (!IsWordToken(token)) return (null, null);
            var cref = node.AncestorsAndSelf(ascendOutOfTrivia: false).OfType<CrefSyntax>().FirstOrDefault();
            var nameAttribute = node.AncestorsAndSelf(ascendOutOfTrivia: false).OfType<XmlNameAttributeSyntax>().FirstOrDefault();
            if (cref == null && nameAttribute == null) return (null, null);
            return (BoundSymbol(model, node) ?? (cref != null ? BoundSymbol(model, cref) : null), null);
        }

        if (IsDeclarationToken(token, node))
        {
            var variables = node switch
            {
                BaseFieldDeclarationSyntax field => field.Declaration.Variables,
                LocalDeclarationStatementSyntax local => local.Declaration.Variables,
                _ => default(SeparatedSyntaxList<VariableDeclaratorSyntax>?)
            };
            if (variables is { } declarators)
            {
                if (declarators.Count == 1) return (model.GetDeclaredSymbol(declarators[0]), null);
                return (null, $"This declaration declares {declarators.Count} variables " +
                              $"({string.Join(", ", declarators.Select(v => v.Identifier.Text))}); " +
                              "place the cursor on the variable's name.");
            }
            if (model.GetDeclaredSymbol(node) is { } declared) return (declared, null);
        }

        if (IsWordToken(token) || IsOperatorToken(token, node)) return (BoundSymbol(model, node), null);
        return (null, null);
    }

    // Whether a token, owned directly by its node, names that node's declaration: an
    // identifier of any declaring node; a modifier or keyword of a member, accessor,
    // local function, local declaration, parameter or type parameter; an operator
    // declaration's operator; a destructor's ~.
    private static bool IsDeclarationToken(SyntaxToken token, SyntaxNode node)
    {
        if (token.IsKind(SyntaxKind.IdentifierToken)) return true;
        if (SyntaxFacts.IsKeywordKind(token.Kind()))
        {
            return node is MemberDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax
                or LocalDeclarationStatementSyntax or ParameterSyntax or TypeParameterSyntax;
        }
        return node switch
        {
            OperatorDeclarationSyntax op => token == op.OperatorToken,
            DestructorDeclarationSyntax destructor => token == destructor.TildeToken,
            _ => false
        };
    }

    private static bool IsOperatorToken(SyntaxToken token, SyntaxNode node) => node switch
    {
        BinaryExpressionSyntax binary => token == binary.OperatorToken,
        PrefixUnaryExpressionSyntax prefix => token == prefix.OperatorToken,
        PostfixUnaryExpressionSyntax postfix => token == postfix.OperatorToken,
        AssignmentExpressionSyntax assignment => token == assignment.OperatorToken,
        _ => false
    };

    // The symbol a node binds to, or its only candidate when binding is ambiguous in a
    // single way (an inaccessible member, a method group in nameof).
    private static ISymbol? BoundSymbol(SemanticModel model, SyntaxNode node)
    {
        var info = model.GetSymbolInfo(node);
        return info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
    }

    // The word token on the position's line, other than `excluded`, that binds to a
    // symbol and starts nearest the position.
    private static SyntaxToken? NearestResolvableToken(
        SyntaxNode root, SourceText text, SemanticModel model, int position, SyntaxToken excluded)
    {
        var textLine = text.Lines.GetLineFromPosition(position);
        return root.DescendantTokens(textLine.Span, descendIntoTrivia: true)
            .Where(t => t.Span.Length > 0 && textLine.Span.Contains(t.SpanStart) && t != excluded && IsWordToken(t))
            .OrderBy(t => Math.Abs(t.SpanStart - position))
            .ThenBy(t => t.SpanStart)
            .Select(t => (SyntaxToken?)t)
            .FirstOrDefault(t => BindToken(t!.Value, model).symbol != null);
    }

    // get_symbol_info's description of a symbol. location is where the symbol is shown
    // (SymbolDisplayLocation: the body-carrying part of a partial member); locations lists
    // every declaring part's source location, in path order (both parts of a partial
    // member, every declaration of a partial type).
    private Task<object> FormatSymbolInfoAsync(ISymbol symbol)
    {
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
            ["location"] = GetSymbolLocation(symbol),
            ["locations"] = SymbolParts.SourceLocations(symbol).Select(FormatLocation).ToList()
        };

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

        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);
        if (ParameterValidation.Choice("accessibility", accessibility, AccessibilityNames, out var canonicalAccessibility) is { } invalidAccessibility)
            return CreateErrorResponse(invalidAccessibility);
        var kindFilters = new List<string>();
        foreach (var kind in kinds ?? new List<string>())
        {
            if (ParameterValidation.Choice("kinds", kind, SymbolKindVocabulary.TypeAndMemberKinds, out var canonicalKind) is { } invalidKind)
                return CreateErrorResponse(invalidKind);
            kindFilters.Add(canonicalKind!);
        }
        var maxResultsToReturn = maxResults ?? 100;

        // Every filter is a constraint each result satisfies: a filter that only a method
        // can satisfy (isAsync, returnType) keeps only methods, a parameter filter only
        // symbols with a parameter list, the type filter only symbols with a type.
        bool Matches(ISymbol symbol, string label)
        {
            if (kindFilters.Count > 0 && !kindFilters.Any(k => SymbolKindVocabulary.Selects(k, label))) return false;
            if (!string.IsNullOrEmpty(namespaceFilter)
                && !MatchesGlobPattern(symbol.ContainingNamespace?.ToDisplayString() ?? "", namespaceFilter)) return false;
            if (canonicalAccessibility != null && symbol.DeclaredAccessibility.ToString() != canonicalAccessibility) return false;
            if (isStatic.HasValue && symbol.IsStatic != isStatic.Value) return false;
            if (attributes is { Count: > 0 })
            {
                var symbolAttributes = symbol.GetAttributes();
                if (!attributes.All(name => symbolAttributes.Any(a => MatchesAttribute(a, name)))) return false;
            }
            if (isAsync.HasValue && !(symbol is IMethodSymbol { } asyncCandidate && asyncCandidate.IsAsync == isAsync.Value)) return false;
            if (!string.IsNullOrEmpty(returnType)
                && !(symbol is IMethodSymbol method && TypeDisplayMatches(method.ReturnType.ToDisplayString(), returnType))) return false;
            if (parameterIncludes is { Count: > 0 } || parameterExcludes is { Count: > 0 })
            {
                if (ParametersOf(symbol) is not { } parameters) return false;
                var parameterTypes = parameters.Select(p => p.Type.ToDisplayString()).ToList();
                if (parameterIncludes is { Count: > 0 }
                    && !parameterIncludes.All(include => parameterTypes.Any(t => TypeDisplayMatches(t, include)))) return false;
                if (parameterExcludes is { Count: > 0 }
                    && parameterExcludes.Any(exclude => parameterTypes.Any(t => TypeDisplayMatches(t, exclude)))) return false;
            }
            if (!string.IsNullOrEmpty(type) && !(TypeOf(symbol) is { } symbolType && TypeDisplayMatches(symbolType.ToDisplayString(), type)))
                return false;
            return true;
        }

        var found = new List<(ISymbol Symbol, string Kind, string? TargetFramework)>();
        foreach (var project in GetLogicalProjects().Projects.SelectMany(p => p.Projects))
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);

            foreach (var symbol in compilation.GetSymbolsWithName(_ => true, SymbolFilter.TypeAndMember, cancellationToken))
            {
                if (symbol.IsImplicitlyDeclared || !symbol.Locations.Any(loc => loc.IsInSource)) continue;
                if (SymbolKindVocabulary.Label(symbol) is not { } label || !Matches(symbol, label)) continue;
                found.Add((symbol, label, targetFramework));
            }
        }

        var merged = CanonicalOrder.BySymbol(
            TargetFrameworkDeduplication.BySymbol(found, f => f.Symbol, f => f.TargetFramework),
            m => m.Item.Symbol);
        var countByKind = merged
            .GroupBy(m => m.Item.Kind)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        var results = merged.Take(maxResultsToReturn).Select(m =>
        {
            var symbol = m.Item.Symbol;
            var result = new Dictionary<string, object?>
            {
                ["name"] = symbol.Name,
                ["fullyQualifiedName"] = symbol.ToDisplayString(),
                ["kind"] = m.Item.Kind,
                ["accessibility"] = symbol.DeclaredAccessibility.ToString(),
                ["isStatic"] = symbol.IsStatic,
                ["containingType"] = symbol.ContainingType?.ToDisplayString() ?? "",
                ["containingNamespace"] = symbol.ContainingNamespace?.ToDisplayString() ?? "",
                ["location"] = GetSymbolLocation(symbol)
            };
            switch (symbol)
            {
                case IMethodSymbol method:
                    result["isAsync"] = method.IsAsync;
                    result["returnType"] = method.ReturnType.ToDisplayString();
                    result["parameters"] = method.Parameters.Select(p => new { name = p.Name, type = p.Type.ToDisplayString() }).ToList();
                    break;
                case IPropertySymbol property:
                    result["propertyType"] = property.Type.ToDisplayString();
                    result["isReadOnly"] = property.IsReadOnly;
                    break;
                case IFieldSymbol field:
                    result["fieldType"] = field.Type.ToDisplayString();
                    result["isConst"] = field.IsConst;
                    result["isReadOnly"] = field.IsReadOnly;
                    break;
            }
            return WithTargetFrameworks(result, m.TargetFrameworks);
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                countByKind,
                results
            },
            ResultCounts.Capped(merged.Count, results.Count),
            suggestedNextTools: new[]
            {
                "get_symbol_info for detailed info on a result",
                "find_references to see usages"
            }
        );
    }

    // The DeclaredAccessibility names semantic_query's accessibility filter accepts.
    private static readonly IReadOnlyList<string> AccessibilityNames = new[]
    {
        nameof(Accessibility.Public), nameof(Accessibility.Private), nameof(Accessibility.Internal),
        nameof(Accessibility.Protected), nameof(Accessibility.ProtectedOrInternal), nameof(Accessibility.ProtectedAndInternal)
    };

    // The largest cap or page size a list-returning navigation tool accepts.
    private const int MaxListLimit = 10000;

    // The parameter list of a method or indexer; null for a symbol without one.
    private static IReadOnlyList<IParameterSymbol>? ParametersOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters,
        IPropertySymbol { IsIndexer: true } indexer => indexer.Parameters,
        _ => null
    };

    // The type of a field, property or event; null for a symbol without one.
    private static ITypeSymbol? TypeOf(ISymbol symbol) => symbol switch
    {
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        IEventSymbol @event => @event.Type,
        _ => null
    };

    // A type as a hierarchy tool lists it: { name, kind, isAbstract, location } plus the
    // target frameworks it occurred in when its project is multi-targeted.
    private Dictionary<string, object?> FormatTypeInfo(INamedTypeSymbol typeSymbol, IReadOnlyList<string>? targetFrameworks = null) =>
        WithTargetFrameworks(new Dictionary<string, object?>
        {
            ["name"] = typeSymbol.ToDisplayString(),
            ["kind"] = GetTypeKindString(typeSymbol),
            ["isAbstract"] = typeSymbol.IsAbstract,
            ["location"] = GetSymbolLocation(typeSymbol)
        }, targetFrameworks ?? Array.Empty<string>());

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
