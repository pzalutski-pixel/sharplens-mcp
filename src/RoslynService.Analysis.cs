using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;

namespace SharpLensMcp;

public partial class RoslynService
{
    public async Task<object> GetDiagnosticsAsync(
        string? filePath,
        string? projectPath,
        string? severity,
        bool includeHidden,
        bool runAnalyzers = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (!string.IsNullOrEmpty(severity) && !Enum.TryParse<DiagnosticSeverity>(severity, ignoreCase: true, out _))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"severity must be one of: {string.Join(", ", Enum.GetNames<DiagnosticSeverity>())} (got '{severity}')",
                context: new { severity }
            );
        }

        DiagnosticsData data;
        try
        {
            data = await GetDiagnosticsDataAsync(filePath, projectPath, severity, includeHidden, runAnalyzers, cancellationToken);
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

        var diagnosticView = data.Diagnostics.Select(d => new
        {
            id = d.Id,
            severity = d.Severity,
            message = d.Message,
            filePath = d.FilePath,
            line = d.Line
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                errorCount = data.ErrorCount,
                warningCount = data.WarningCount,
                analyzersRan = data.AnalyzersRan,
                analyzerCount = data.AnalyzerCount,
                diagnostics = diagnosticView
            },
            suggestedNextTools: data.ErrorCount > 0 || data.WarningCount > 0
                ? new[]
                {
                    "get_code_fixes for a diagnostic to see available fixes",
                    "apply_code_fix to apply a fix automatically"
                }
                : new[]
                {
                    "No diagnostics found - solution is healthy"
                },
            totalCount: data.TotalCount,
            returnedCount: data.Diagnostics.Count
        );
    }

    // (GetDiagnosticsDataAsync implementation below.)

    // Typed-data variant. GetProjectHealthAsync calls this directly so it doesn't
    // have to reflect over the anonymous-typed public response. Throws
    // FileNotFoundException for unknown filePath; otherwise returns DiagnosticsData.
    internal async Task<DiagnosticsData> GetDiagnosticsDataAsync(
        string? filePath,
        string? projectPath,
        string? severity,
        bool includeHidden,
        bool runAnalyzers,
        CancellationToken cancellationToken = default)
    {
        var allDiagnostics = new List<Diagnostic>();
        var analyzersRanOnAtLeastOneProject = false;
        var analyzersInvoked = 0;
        var projectsToAnalyze = new List<Project>();

        if (!string.IsNullOrEmpty(filePath))
        {
            var document = await GetDocumentAsync(filePath);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel != null)
            {
                allDiagnostics.AddRange(semanticModel.GetDiagnostics(cancellationToken: cancellationToken));
            }
            projectsToAnalyze.Add(document.Project);
        }
        else if (!string.IsNullOrEmpty(projectPath))
        {
            var project = _solution!.Projects.FirstOrDefault(p => p.FilePath == projectPath);
            if (project != null)
            {
                var compilation = await GetProjectCompilationAsync(project, cancellationToken);
                if (compilation != null)
                {
                    allDiagnostics.AddRange(compilation.GetDiagnostics(cancellationToken));
                }
                projectsToAnalyze.Add(project);
            }
        }
        else
        {
            foreach (var project in _solution!.Projects)
            {
                var compilation = await GetProjectCompilationAsync(project, cancellationToken);
                if (compilation != null)
                {
                    allDiagnostics.AddRange(compilation.GetDiagnostics(cancellationToken));
                }
                projectsToAnalyze.Add(project);
            }
        }

        if (runAnalyzers)
        {
            foreach (var project in projectsToAnalyze)
            {
                var analyzers = project.AnalyzerReferences
                    .SelectMany(ar => ar.GetAnalyzers(LanguageNames.CSharp))
                    .ToImmutableArray();
                if (analyzers.IsEmpty) continue;

                var compilation = await GetProjectCompilationAsync(project, cancellationToken);
                if (compilation == null) continue;

                var withAnalyzers = compilation.WithAnalyzers(
                    analyzers,
                    new AnalyzerOptions(
                        project.AnalyzerOptions.AdditionalFiles,
                        project.AnalyzerOptions.AnalyzerConfigOptionsProvider));

                try
                {
                    var analyzerDiagnostics = await withAnalyzers.GetAllDiagnosticsAsync(cancellationToken);
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        var doc = TryFindDocument(filePath);
                        if (doc?.FilePath != null)
                        {
                            // Filter by the physical source tree, not GetLineSpan().Path:
                            // the latter honors #line directives, so a #line-remapped
                            // diagnostic that physically lives in this file would be
                            // dropped (and one remapped INTO it wrongly kept).
                            analyzerDiagnostics = analyzerDiagnostics
                                .Where(d => d.Location.SourceTree?.FilePath is { Length: > 0 } treePath
                                    && string.Equals(
                                        Path.GetFullPath(treePath),
                                        Path.GetFullPath(doc.FilePath),
                                        PathComparison))
                                .ToImmutableArray();
                        }
                    }
                    allDiagnostics.AddRange(analyzerDiagnostics);
                    analyzersRanOnAtLeastOneProject = true;
                    analyzersInvoked += analyzers.Length;
                }
                catch (OperationCanceledException)
                {
                    // A per-call timeout during analyzer execution must surface as
                    // cancellation, not be downgraded to a skipped-project warning.
                    throw;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Warning] Analyzer execution failed for '{project.Name}': {ex.Message}");
                }
            }
        }

        allDiagnostics = allDiagnostics
            .GroupBy(d => (d.Id, d.Location.SourceTree?.FilePath, d.Location.SourceSpan, d.GetMessage()))
            .Select(g => g.First())
            .ToList();

        if (!includeHidden)
        {
            allDiagnostics = allDiagnostics.Where(d => d.Severity != DiagnosticSeverity.Hidden).ToList();
        }

        // Error/warning counts reflect the whole set, so they are taken before the
        // severity filter, which scopes only the returned page and totalCount.
        var errorCount = allDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var warningCount = allDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        if (!string.IsNullOrEmpty(severity))
        {
            var severityEnum = Enum.Parse<DiagnosticSeverity>(severity, ignoreCase: true);
            allDiagnostics = allDiagnostics.Where(d => d.Severity == severityEnum).ToList();
        }

        var totalBeforeCap = allDiagnostics.Count;

        // Deterministic, useful truncation order: errors first (they matter most when
        // the page is capped), then by file and source position.
        var capped = allDiagnostics
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.SourceTree?.FilePath, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .Take(_maxDiagnostics)
            .ToList();

        var entries = capped.Select(d =>
        {
            var lineSpan = d.Location.GetLineSpan();
            return new DiagnosticEntry(
                d.Id,
                d.Severity.ToString(),
                d.GetMessage(),
                FormatPath(lineSpan.Path),
                lineSpan.StartLinePosition.Line);
        }).ToList();

        return new DiagnosticsData(
            ErrorCount: errorCount,
            WarningCount: warningCount,
            AnalyzerCount: analyzersInvoked,
            AnalyzersRan: analyzersRanOnAtLeastOneProject,
            TotalCount: totalBeforeCap,
            Diagnostics: entries);
    }

    public async Task<object> GetCodeFixesAsync(
        string filePath, string diagnosticId, int line, int column,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        // Get all diagnostics for the document (semantic + syntax)
        var allDiagnostics = pos.Model.GetDiagnostics(cancellationToken: cancellationToken).ToList();
        allDiagnostics.AddRange(pos.Tree.GetDiagnostics(cancellationToken));

        var position = pos.Position;

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

        // The real fixes, from the same MEF code-fix providers apply_code_fix uses —
        // index/title/equivalenceKey match apply_code_fix's own listing, so the
        // documented workflow (get_code_fixes → apply_code_fix with fixIndex) shows
        // one consistent reality. Canned guidance strings remain only as a fallback
        // for diagnostics no provider can fix.
        var codeActions = await GetCodeActionsForDiagnosticAsync(pos.Document, diagnostic, cancellationToken);
        var fixes = codeActions.Select((action, index) => new
        {
            index,
            title = action.Title,
            equivalenceKey = action.EquivalenceKey
        }).ToList();

        var fallbackSuggestions = fixes.Count == 0
            ? GetCommonFixSuggestions(diagnostic.Id, diagnostic.GetMessage())
            : null;

        return CreateSuccessResponse(
            data: new
            {
                diagnosticId = diagnostic.Id,
                message = diagnostic.GetMessage(),
                severity = diagnostic.Severity.ToString(),
                location = new
                {
                    filePath = FormatPath(lineSpan.Path),
                    startLine = lineSpan.StartLinePosition.Line,
                    startColumn = lineSpan.StartLinePosition.Character,
                    endLine = lineSpan.EndLinePosition.Line,
                    endColumn = lineSpan.EndLinePosition.Character
                },
                fixes,
                fallbackSuggestions
            },
            suggestedNextTools: fixes.Count > 0
                ? new[]
                {
                    "apply_code_fix with the same fixIndex to apply one of these fixes",
                    "get_diagnostics to see other issues"
                }
                : new[]
                {
                    "No automated fix available - apply the fallback suggestions manually",
                    "get_diagnostics to see other issues"
                },
            totalCount: fixes.Count,
            returnedCount: fixes.Count
        );
    }

    internal List<string> GetCommonFixSuggestions(string diagnosticId, string message)
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
        bool preview = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;
        var document = pos.Document;

        // Get all diagnostics (semantic + syntax)
        var allDiagnostics = pos.Model.GetDiagnostics(cancellationToken: cancellationToken).ToList();
        allDiagnostics.AddRange(pos.Tree.GetDiagnostics(cancellationToken));

        var position = pos.Position;

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
        var codeActions = await GetCodeActionsForDiagnosticAsync(document, diagnostic, cancellationToken);

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
        var operations = await selectedAction.GetOperationsAsync(cancellationToken);
        var changedSolution = _solution;

        foreach (var operation in operations)
        {
            if (operation is ApplyChangesOperation applyChangesOp)
            {
                changedSolution = applyChangesOp.ChangedSolution;
                break;
            }
        }

        if (changedSolution == null || changedSolution == _solution)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Code fix did not produce any changes",
                hint: "The selected fix may not be applicable in this context.",
                context: new { fixTitle = selectedAction.Title, fixIndex }
            );
        }

        if (preview)
        {
            var changedDocuments = await BuildPreviewChangeDetailsAsync(changedSolution, cancellationToken);
            return CreateSuccessResponse(
                data: new
                {
                    applied = false,
                    diagnosticId = diagnostic.Id,
                    fixTitle = selectedAction.Title,
                    fixIndex = fixIndex.Value,
                    changedFiles = changedDocuments,
                    preview = true
                },
                suggestedNextTools: new[] { "apply_code_fix with preview=false to apply changes to disk" },
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
                diagnosticId = diagnostic.Id,
                fixTitle = selectedAction.Title,
                fixIndex = fixIndex.Value,
                changedFiles = FormatAppliedChanges(applyReport),
                preview = false,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[] { "get_diagnostics to verify fix resolved the issue" },
            totalCount: applyReport.Count,
            returnedCount: applyReport.Count
        );
    }

    private async Task<List<CodeAction>> GetCodeActionsForDiagnosticAsync(
        Document document, Diagnostic diagnostic, CancellationToken cancellationToken = default)
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
                cancellationToken);

            try
            {
                await provider.RegisterCodeFixesAsync(context);
            }
            catch (OperationCanceledException)
            {
                // A per-call timeout must surface as cancellation (→ TIMEOUT), not be
                // swallowed and returned as a partial set of fixes.
                throw;
            }
            catch
            {
                // Some providers may throw if they can't handle the diagnostic
                // Silently continue to next provider
            }
        }

        return codeActions;
    }

    // MEF discovery is expensive (scans every assembly's exports). Cache after first use.
    private static readonly object _mefLock = new();
    private static List<CodeFixProvider>? _cachedCodeFixProviders;
    private static List<CodeRefactoringProvider>? _cachedCodeRefactoringProviders;

    // Build the assembly list used for both MEF discovery and the workspace's
    // MefHostServices. Includes Roslyn's Workspaces + Features assemblies so that:
    //   (1) CodeFixProvider/CodeRefactoringProvider exports are discoverable, AND
    //   (2) the workspace's language services (IExtractMethodService<>, etc.) that
    //       those providers depend on at runtime are also wired up.
    // Both halves are required — a MEF-discovered provider whose dependencies live
    // outside the workspace's HostServices will silently produce no actions.
    internal static IEnumerable<System.Reflection.Assembly> GetRoslynMefAssemblies()
    {
        var assemblies = new List<System.Reflection.Assembly>
        {
            typeof(Workspace).Assembly,                                          // Microsoft.CodeAnalysis.Workspaces
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly,     // Microsoft.CodeAnalysis.CSharp
            typeof(Microsoft.CodeAnalysis.CSharp.Formatting.CSharpFormattingOptions).Assembly, // Microsoft.CodeAnalysis.CSharp.Workspaces
        };
        TryAddAssembly(assemblies, "Microsoft.CodeAnalysis.Features");
        TryAddAssembly(assemblies, "Microsoft.CodeAnalysis.CSharp.Features");
        return assemblies.Distinct();
    }

    // Build a MEF container with the same assembly set used for the workspace's
    // HostServices. Roslyn's refactoring/fix providers are MEF-exported with
    // [ImportingConstructor] constructors that take service injections — reflection
    // with a parameterless-ctor filter misses them all. System.Composition resolves
    // the imports and instantiates them correctly.
    private static System.Composition.Hosting.CompositionHost BuildMefContainer()
    {
        var configuration = new System.Composition.Hosting.ContainerConfiguration()
            .WithAssemblies(GetRoslynMefAssemblies());
        return configuration.CreateContainer();
    }

    private static void TryAddAssembly(List<System.Reflection.Assembly> list, string name)
    {
        try { list.Add(System.Reflection.Assembly.Load(name)); }
        catch { /* package not deployed — skip */ }
    }

    private List<CodeFixProvider> GetBuiltInCodeFixProviders()
    {
        if (_cachedCodeFixProviders != null) return _cachedCodeFixProviders;
        lock (_mefLock)
        {
            if (_cachedCodeFixProviders != null) return _cachedCodeFixProviders;
            try
            {
                using var container = BuildMefContainer();
                _cachedCodeFixProviders = container.GetExports<CodeFixProvider>().ToList();
            }
            catch
            {
                // MEF composition failed — fall back to empty list. Tools will surface
                // no fixes rather than crashing the request.
                _cachedCodeFixProviders = new List<CodeFixProvider>();
            }
            return _cachedCodeFixProviders;
        }
    }

    private List<CodeRefactoringProvider> GetBuiltInCodeRefactoringProviders()
    {
        if (_cachedCodeRefactoringProviders != null) return _cachedCodeRefactoringProviders;
        lock (_mefLock)
        {
            if (_cachedCodeRefactoringProviders != null) return _cachedCodeRefactoringProviders;
            try
            {
                using var container = BuildMefContainer();
                _cachedCodeRefactoringProviders = container.GetExports<CodeRefactoringProvider>().ToList();
            }
            catch
            {
                _cachedCodeRefactoringProviders = new List<CodeRefactoringProvider>();
            }
            return _cachedCodeRefactoringProviders;
        }
    }

    private async Task<List<(CodeAction action, string kind)>> GetAllCodeActionsAtPositionAsync(
        Document document,
        int position,
        int? endPosition = null,
        bool includeCodeFixes = true,
        bool includeRefactorings = true,
        CancellationToken cancellationToken = default)
    {
        var allActions = new List<(CodeAction action, string kind)>();
        var span = endPosition.HasValue
            ? TextSpan.FromBounds(position, endPosition.Value)
            : new TextSpan(position, 0);

        // Get code fixes for diagnostics at this position
        if (includeCodeFixes)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);

            if (semanticModel != null && syntaxTree != null)
            {
                // Get all diagnostics and filter to those overlapping our span
                var allDiagnostics = semanticModel.GetDiagnostics(cancellationToken: cancellationToken).ToList();
                allDiagnostics.AddRange(syntaxTree.GetDiagnostics(cancellationToken));

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
                            cancellationToken);

                        try
                        {
                            await provider.RegisterCodeFixesAsync(context);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
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
                    cancellationToken);

                try
                {
                    await provider.ComputeRefactoringsAsync(context);
                }
                catch (OperationCanceledException)
                {
                    throw;
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
        bool summaryOnly = false,
        CancellationToken cancellationToken = default)
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
                        filePath = FormatPath(d.FilePath),
                        folders = d.Folders.ToList()
                    })
                    .ToList()
                : null;

            var referenceCount = project.MetadataReferences.Count();
            var documentCount = project.DocumentIds.Count;

            projects.Add(new
            {
                name = project.Name,
                filePath = FormatPath(project.FilePath),
                language = project.Language,
                outputPath = project.OutputFilePath,
                targetFramework = GetTargetFramework(project),
                documentCount,
                referenceCount,
                references = includeReferences ? (referenceCount > 100 ? references!.Concat(new[] { $"... and {referenceCount - 100} more" }).ToList() : references) : null,
                projectReferences,
                documents = includeDocuments ? (documentCount > 500 ? documents!.Concat(new[] { new { name = $"... and {documentCount - 500} more documents", filePath = string.Empty, folders = new List<string>() } }).ToList() : documents) : null
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

    // Reads the real target framework moniker from the project file. Roslyn's
    // Project model doesn't expose the TFM, and CompilationOptions.Platform is
    // the CPU platform (AnyCpu/x64), not a framework.
    private static string? GetTargetFramework(Project project)
    {
        if (project.FilePath == null || !File.Exists(project.FilePath)) return null;
        try
        {
            return ResolveTargetFrameworkFromXml(File.ReadAllText(project.FilePath), project.Name);
        }
        catch
        {
            // Unreadable or malformed project file — report no TFM rather than a wrong one.
            return null;
        }
    }

    // Pure resolution from csproj XML content. Element lookup is by local name so
    // legacy project files with the MSBuild xmlns resolve too. A multi-targeted
    // project loads once per TFM with the moniker in the project name
    // ("MyProj(net8.0)") — when that instance is identifiable, report its own
    // moniker instead of the full semicolon list.
    internal static string? ResolveTargetFrameworkFromXml(string csprojContent, string projectName)
    {
        var doc = System.Xml.Linq.XDocument.Parse(csprojContent);

        string? ElementValue(string localName) => doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

        var single = ElementValue("TargetFramework");
        if (!string.IsNullOrWhiteSpace(single)) return single;

        var multi = ElementValue("TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(multi))
        {
            var open = projectName.LastIndexOf('(');
            if (open >= 0 && projectName.EndsWith(')'))
            {
                var candidate = projectName[(open + 1)..^1];
                if (multi.Split(';').Select(t => t.Trim()).Contains(candidate)) return candidate;
            }
            return multi;
        }

        // Legacy (non-SDK) project files carry a version ("v4.7.2") instead of a moniker.
        var legacy = ElementValue("TargetFrameworkVersion");
        return string.IsNullOrWhiteSpace(legacy) ? null : legacy;
    }

    // Computes a document's organized usings: directives the compiler marks
    // unnecessary (CS8019) removed, the rest grouped global → plain (System root
    // namespaces first) → static → alias, alphabetical within each group.
    // Returns null when the document has no compilation-unit root.
    private static async Task<(CompilationUnitSyntax newRoot, int totalUsings, int removedUsings, bool changed)?>
        ComputeOrganizedUsingsAsync(Document document, CancellationToken cancellationToken = default)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (semanticModel == null || root is not CompilationUnitSyntax compilationUnit) return null;

        var usings = compilationUnit.Usings;
        if (usings.Count == 0) return (compilationUnit, 0, 0, false);

        // CS8019 "Unnecessary using directive" — the compiler's own verdict, so
        // conditional-compilation and alias subtleties are already accounted for.
        var unusedSpans = semanticModel.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id == "CS8019")
            .Select(d => d.Location.SourceSpan)
            .ToList();

        var kept = usings
            .Where(u => !unusedSpans.Any(span => span.Contains(u.Span) || u.Span.Contains(span)))
            .ToList();

        var organized = kept.OrderBy(UsingSortKey).ToList();
        var changed = !usings.SequenceEqual(organized);

        var newRoot = WithUsingsPreservingHeader(compilationUnit, usings[0], organized);
        return (newRoot, usings.Count, usings.Count - kept.Count, changed);
    }

    // Group order: global, plain (System root first), static, alias —
    // alphabetical within each group. Exact "System"-root check, so a
    // namespace like SystemFoo sorts with the other plain usings.
    private static (int group, int systemFirst, string name) UsingSortKey(UsingDirectiveSyntax u)
    {
        var name = u.Name?.ToString() ?? u.ToString();
        var group =
            u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) ? 0 :
            u.Alias != null ? 3 :
            u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) ? 2 : 1;
        var systemFirst = name == "System" || name.StartsWith("System.", StringComparison.Ordinal) ? 0 : 1;
        return (group, systemFirst, name);
    }

    // Keeps a using block's file-header leading trivia pinned to the top after the
    // directives are reordered (organize_usings) or a new one is inserted before them
    // (add_missing_imports). The header is the leading trivia of the directive that was
    // syntactically first; rather than let it travel to the middle of the file with that
    // directive, it is moved onto whichever directive ends up first — the same contract
    // as Roslyn's "Remove and Sort Usings" / dotnet format. No-op when the original first
    // directive is still first (or the block is empty).
    private static SyntaxList<UsingDirectiveSyntax> PinHeaderTrivia(
        UsingDirectiveSyntax originalFirst, IReadOnlyList<UsingDirectiveSyntax> ordered)
    {
        if (ordered.Count == 0 || ordered[0] == originalFirst)
            return SyntaxFactory.List(ordered);

        var header = originalFirst.GetLeadingTrivia();
        var result = ordered.ToList();
        result[0] = result[0].WithLeadingTrivia(header.AddRange(result[0].GetLeadingTrivia()));
        for (var i = 1; i < result.Count; i++)
        {
            if (result[i] == originalFirst)
            {
                result[i] = originalFirst.WithLeadingTrivia();
                break;
            }
        }
        return SyntaxFactory.List(result);
    }

    // Rebuilds the compilation unit with the reordered/reduced usings while keeping the file
    // header (the original first using's leading trivia) pinned to the top: onto the new
    // first using when one remains, or onto the first member / EOF token when removing the
    // unused usings empties the block — so a header on the sole using is not deleted with it.
    private static CompilationUnitSyntax WithUsingsPreservingHeader(
        CompilationUnitSyntax unit, UsingDirectiveSyntax originalFirst, IReadOnlyList<UsingDirectiveSyntax> newUsings)
    {
        if (newUsings.Count > 0)
            return unit.WithUsings(PinHeaderTrivia(originalFirst, newUsings));

        var header = originalFirst.GetLeadingTrivia();
        var withoutUsings = unit.WithUsings(default);
        if (withoutUsings.Members.Count > 0)
        {
            var firstMember = withoutUsings.Members[0];
            return withoutUsings.ReplaceNode(firstMember,
                firstMember.WithLeadingTrivia(header.AddRange(firstMember.GetLeadingTrivia())));
        }
        var eof = withoutUsings.EndOfFileToken;
        return withoutUsings.WithEndOfFileToken(eof.WithLeadingTrivia(header.AddRange(eof.LeadingTrivia)));
    }

    public async Task<object> OrganizeUsingsAsync(
        string filePath, bool preview = true, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var document = TryFindDocument(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var organizedResult = await ComputeOrganizedUsingsAsync(document, cancellationToken);
        if (organizedResult == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Not a valid C# file",
                hint: "This tool only works with C# source files",
                context: new { filePath }
            );
        }

        var (newRoot, totalUsings, removedUsings, changed) = organizedResult.Value;

        if (preview || !changed)
        {
            return CreateSuccessResponse(
                data: new
                {
                    organizedText = newRoot.ToFullString(),
                    totalUsings,
                    removedUsings,
                    changed,
                    preview = true,
                    applied = false
                },
                suggestedNextTools: changed
                    ? new[] { "organize_usings with preview=false to apply", "organize_usings_batch to process multiple files" }
                    : new[] { "Usings are already organized - nothing to apply" }
            );
        }

        var newSolution = _solution!.WithDocumentText(
            document.Id, SourceText.From(newRoot.ToFullString(), Encoding.UTF8));
        var (ok, applyError, _, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                organizedText = newRoot.ToFullString(),
                totalUsings,
                removedUsings,
                changed,
                preview = false,
                applied = true,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[] { "get_diagnostics to verify the file still compiles" }
        );
    }

    public async Task<object> OrganizeUsingsBatchAsync(
        string? projectName, string? filePattern, bool preview = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var projectsToProcess = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name == projectName);

        var processedFiles = new List<object>();
        var totalFiles = 0;
        var filesWithChanges = 0;
        var changedSolution = _solution!;

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
                    var organizedResult = await ComputeOrganizedUsingsAsync(document, cancellationToken);
                    if (organizedResult == null) continue;

                    var (newRoot, totalUsings, removedUsings, changed) = organizedResult.Value;
                    if (totalUsings == 0 || !changed) continue;

                    filesWithChanges++;

                    processedFiles.Add(new
                    {
                        filePath = FormatPath(document.FilePath),
                        fileName = Path.GetFileName(document.FilePath),
                        projectName = project.Name,
                        totalUsings,
                        removedUsings,
                        preview
                    });

                    if (!preview)
                    {
                        changedSolution = changedSolution.WithDocumentText(
                            document.Id, SourceText.From(newRoot.ToFullString(), Encoding.UTF8));
                    }
                }
                catch (OperationCanceledException)
                {
                    // A per-call timeout must surface as cancellation (→ TIMEOUT), not be
                    // folded into a per-file error entry and returned as partial success.
                    throw;
                }
                catch (Exception ex)
                {
                    processedFiles.Add(new
                    {
                        filePath = FormatPath(document.FilePath),
                        fileName = Path.GetFileName(document.FilePath),
                        projectName = project.Name,
                        error = ex.Message
                    });
                }
            }
        }

        // Apply through the unified path — workspace, in-memory solution, caches,
        // and disk stay consistent; no sync_documents needed afterward.
        IReadOnlyList<string> introducedErrors = Array.Empty<string>();
        if (!preview && filesWithChanges > 0)
        {
            var (ok, applyError, _, applyErrors) = await ApplyAndReportAsync(changedSolution, cancellationToken);
            if (!ok) return applyError!;
            introducedErrors = applyErrors;
        }

        return CreateSuccessResponse(
            data: new
            {
                totalFilesScanned = totalFiles,
                filesWithChanges,
                preview,
                files = processedFiles,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: preview
                ? new[] { "organize_usings_batch with preview=false to apply changes" }
                : new[] { "get_diagnostics to verify the changes compile" },
            totalCount: totalFiles,
            returnedCount: processedFiles.Count
        );
    }

    public async Task<object> FormatDocumentBatchAsync(
        string? projectName, bool includeTests = true, bool preview = true, string? filePattern = null,
        string? mode = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "whitespace" : mode!.ToLowerInvariant();
        if (normalizedMode != "whitespace" && normalizedMode != "canonical")
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"mode must be 'whitespace' or 'canonical' (got '{mode}')",
                hint: "'whitespace' (default) fixes indentation/spacing and preserves your style; 'canonical' fully re-prints the syntax tree — it expands minified code but rewrites stylistic whitespace.",
                context: new { mode }
            );
        }
        var canonical = normalizedMode == "canonical";

        var projectsToProcess = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name == projectName);

        var processedFiles = new List<object>();
        var totalFiles = 0;
        var filesFormatted = 0;
        var changedSolution = _solution!;

        foreach (var project in projectsToProcess)
        {
            if (!includeTests && project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                continue;

            var documents = project.Documents;
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
                    var originalText = (await document.GetTextAsync(cancellationToken)).ToString();

                    string formattedText;
                    if (canonical)
                    {
                        // Canonical re-print of the syntax tree: the only engine that
                        // expands minified one-liner code, at the cost of rewriting
                        // stylistic whitespace (suppression-operator spacing becomes
                        // "null !", blank lines between members are dropped). Opt-in.
                        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                        if (syntaxTree == null) continue;
                        var root = await syntaxTree.GetRootAsync(cancellationToken);
                        formattedText = root.NormalizeWhitespace().ToFullString();
                    }
                    else
                    {
                        // Whitespace-only formatting per the workspace's formatting
                        // rules — fixes indentation and token spacing, preserves the
                        // author's structural choices (single-line auto-properties,
                        // blank lines, "null!"). Same contract as `dotnet format`.
                        var formattedDocument = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
                        formattedText = (await formattedDocument.GetTextAsync(cancellationToken)).ToString();
                    }

                    var hasChanges = originalText != formattedText;

                    if (!hasChanges) continue;

                    filesFormatted++;

                    processedFiles.Add(new
                    {
                        filePath = FormatPath(document.FilePath),
                        fileName = Path.GetFileName(document.FilePath),
                        projectName = project.Name,
                        preview
                    });

                    if (!preview)
                    {
                        changedSolution = changedSolution.WithDocumentText(
                            document.Id, SourceText.From(formattedText, Encoding.UTF8));
                    }
                }
                catch (OperationCanceledException)
                {
                    // A per-call timeout must surface as cancellation (→ TIMEOUT), not be
                    // folded into a per-file error entry and returned as partial success.
                    throw;
                }
                catch (Exception ex)
                {
                    processedFiles.Add(new
                    {
                        filePath = FormatPath(document.FilePath),
                        fileName = Path.GetFileName(document.FilePath),
                        projectName = project.Name,
                        error = ex.Message
                    });
                }
            }
        }

        // Apply through the unified path — workspace, in-memory solution, caches,
        // and disk stay consistent; no sync_documents needed afterward.
        IReadOnlyList<string> introducedErrors = Array.Empty<string>();
        if (!preview && filesFormatted > 0)
        {
            var (ok, applyError, _, applyErrors) = await ApplyAndReportAsync(changedSolution, cancellationToken);
            if (!ok) return applyError!;
            introducedErrors = applyErrors;
        }

        return CreateSuccessResponse(
            data: new
            {
                totalFilesScanned = totalFiles,
                filesFormatted,
                mode = normalizedMode,
                preview,
                files = processedFiles,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: preview
                ? new[] { "format_document_batch with preview=false to apply changes" }
                : new[] { "get_diagnostics to verify the formatting compiles" },
            totalCount: totalFiles,
            returnedCount: processedFiles.Count
        );
    }


}
