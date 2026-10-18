using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SharpLensMcp;

public partial class RoslynService
{
    private static readonly IReadOnlyList<string> DiagnosticSeverityNames = Enum.GetNames<DiagnosticSeverity>();

    public async Task<object> GetDiagnosticsAsync(
        string? filePath,
        string? projectPath,
        string? severity,
        bool includeHidden,
        bool runAnalyzers = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Choice("severity", severity, DiagnosticSeverityNames, out var canonicalSeverity) is { } invalid)
            return CreateErrorResponse(invalid);

        var (scope, scopeError) = ResolveDiagnosticsScope(filePath, projectPath);
        if (scope == null) return scopeError!;

        var data = await ComputeDiagnosticsDataAsync(scope, canonicalSeverity, includeHidden, runAnalyzers, cancellationToken);
        var returned = data.Diagnostics.Take(_maxDiagnostics).ToList();

        return CreateSuccessResponse(
            data: new
            {
                errorCount = data.ErrorCount,
                warningCount = data.WarningCount,
                analyzersRan = data.AnalyzersRan,
                analyzerCount = data.AnalyzerCount,
                // These are Roslyn's live-compilation diagnostics, not a `dotnet build`. Counts
                // can differ from a real build (MSBuild target/generator graph, a partial load
                // inflating unresolved-type errors); use health_check's `partial` to rule that out.
                analysisMode = "live-compilation",
                diagnostics = returned.Select(d => new
                {
                    id = d.Id,
                    severity = d.Severity,
                    message = d.Message,
                    filePath = d.FilePath,
                    line = d.Line,
                    column = d.Column,
                    targetFrameworks = d.TargetFrameworks
                }).ToList()
            },
            ResultCounts.Capped(data.Diagnostics.Count, returned.Count),
            suggestedNextTools: data.ErrorCount > 0 || data.WarningCount > 0
                ? new[]
                {
                    "get_code_fixes for a diagnostic to see available fixes",
                    "apply_code_fix to apply a fix automatically"
                }
                : data.Diagnostics.Count > 0
                    ? new[]
                    {
                        "No errors or warnings - the remaining diagnostics are informational",
                        "get_code_fixes for a diagnostic to see available fixes"
                    }
                    : new[] { "No diagnostics found in the requested scope" });
    }

    // What a get_diagnostics call analyzes: each unit is a project, and for a file-scoped
    // call the document of that file in the project. A file is analyzed in every project
    // (target framework) that compiles it; a project path or name in every target framework
    // of the logical project it resolves to (one framework for a framework-qualified name);
    // no scope means every project. Errors are the canonical not-found envelopes.
    internal (IReadOnlyList<(Project Project, Document? Document)>? Units, object? Error) ResolveDiagnosticsScope(
        string? filePath, string? projectPath)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            var document = TryFindDocument(filePath);
            if (document == null)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.FileNotInSolution,
                    $"File not found in solution: {filePath}",
                    hint: "Check the file path or reload the solution",
                    context: new { filePath }));
            }
            return (DocumentsSharingPath(document).Select(d => (d.Project, (Document?)d)).ToList(), null);
        }

        if (!string.IsNullOrEmpty(projectPath))
        {
            var (projects, error) = ResolveProjectPath(projectPath);
            return projects == null ? (null, error) : (projects.Select(p => (p, (Document?)null)).ToList(), null);
        }

        return (GetLogicalProjects().Projects.SelectMany(p => p.Projects).Select(p => (p, (Document?)null)).ToList(), null);
    }

    // Every document of the solution with the same file path as the given one — the file
    // as each target framework (or linking project) compiles it — in solution order.
    private List<Document> DocumentsSharingPath(Document document) =>
        document.FilePath == null
            ? new List<Document> { document }
            : _solution!.GetDocumentIdsWithFilePath(document.FilePath)
                .Select(id => _solution.GetDocument(id))
                .OfType<Document>()
                .ToList();

    // A projectPath argument: the path of a project file — absolute, or relative to the
    // solution directory, with either separator — or a project name as ResolveProjectScope
    // accepts it ("Lib", "Multi", "Multi(net8.0)"). Anything else is the canonical
    // project-not-found error.
    private (IReadOnlyList<Project>? Projects, object? Error) ResolveProjectPath(string projectPath)
    {
        var model = GetLogicalProjects();
        if (TryFullPath(projectPath) is { } fullPath)
        {
            var byPath = model.Projects.FirstOrDefault(p =>
                p.FilePath != null && LoadFidelityClassifier.PathComparer.Equals(Path.GetFullPath(p.FilePath), fullPath));
            if (byPath != null) return (byPath.Projects, null);
        }
        return ResolveProjectScope(projectPath);
    }

    // The full path a path-like argument denotes (relative ones against the solution
    // directory); null when it cannot be a path.
    private string? TryFullPath(string path)
    {
        try
        {
            path = WithNativeSeparators(path);
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            var solutionDirectory = _solution?.FilePath == null ? null : Path.GetDirectoryName(_solution.FilePath);
            return solutionDirectory == null ? null : Path.GetFullPath(Path.Combine(solutionDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // The diagnostics of the scope: each unit's suppression-aware diagnostics (the whole
    // project, or one file), the per-target-framework duplicates merged into one entry that
    // lists the frameworks it occurred in, hidden ones dropped unless includeHidden or the
    // severity filter asks for Hidden. Error/warning counts are taken before the severity
    // filter, which narrows only the list. Ordered errors first, then by location.
    internal async Task<DiagnosticsData> ComputeDiagnosticsDataAsync(
        IReadOnlyList<(Project Project, Document? Document)> units,
        string? severity,
        bool includeHidden,
        bool runAnalyzers,
        CancellationToken cancellationToken)
    {
        var raw = new List<(Diagnostic Diagnostic, Project Project)>();
        var analyzerTypes = new HashSet<Type>();
        foreach (var (project, document) in units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComputedDiagnostics computed;
            if (document != null)
            {
                var tree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (tree == null) continue;
                computed = await ComputeTreeDiagnosticsAsync(project, tree, runAnalyzers, cancellationToken);
            }
            else
            {
                computed = await ComputeProjectDiagnosticsAsync(project, runAnalyzers, cancellationToken);
            }
            raw.AddRange(computed.Diagnostics.Select(d => (d, project)));
            if (runAnalyzers)
            {
                foreach (var analyzer in computed.Ran) analyzerTypes.Add(analyzer.GetType());
            }
        }

        var severityFilter = severity == null ? (DiagnosticSeverity?)null : Enum.Parse<DiagnosticSeverity>(severity);
        var keepHidden = includeHidden || severityFilter == DiagnosticSeverity.Hidden;
        var model = GetLogicalProjects();

        var merged = TargetFrameworkDeduplication.By(
                raw,
                item => (item.Diagnostic.Id, SourceSpanKey.From(item.Diagnostic.Location),
                    item.Diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)),
                EqualityComparer<(string, SourceSpanKey?, string)>.Default,
                item => LogicalProject.TargetFrameworkOf(item.Project))
            .Where(m => keepHidden || m.Item.Diagnostic.Severity != DiagnosticSeverity.Hidden)
            .ToList();

        var errorCount = merged.Count(m => m.Item.Diagnostic.Severity == DiagnosticSeverity.Error);
        var warningCount = merged.Count(m => m.Item.Diagnostic.Severity == DiagnosticSeverity.Warning);

        var entries = merged
            .Where(m => severityFilter == null || m.Item.Diagnostic.Severity == severityFilter)
            .Select(m =>
            {
                var diagnostic = m.Item.Diagnostic;
                var span = diagnostic.Location.GetLineSpan();
                return (Severity: diagnostic.Severity, Entry: new DiagnosticEntry(
                    diagnostic.Id,
                    diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(),
                    FormatPath(span.Path),
                    span.StartLinePosition.Line,
                    span.StartLinePosition.Character,
                    model.Of(m.Item.Project).IsMultiTargeted ? m.TargetFrameworks : null));
            })
            .OrderByDescending(e => e.Severity)
            .ThenBy(e => new ResultLocation(e.Entry.FilePath, e.Entry.Line, e.Entry.Column), ResultLocationComparer.Instance)
            .ThenBy(e => e.Entry.Id, StringComparer.Ordinal)
            .ThenBy(e => e.Entry.Message, StringComparer.Ordinal)
            .Select(e => e.Entry)
            .ToList();

        return new DiagnosticsData(
            ErrorCount: errorCount,
            WarningCount: warningCount,
            AnalyzerCount: analyzerTypes.Count,
            AnalyzersRan: analyzerTypes.Count > 0,
            Diagnostics: entries);
    }

    // The diagnostic with the given id AT the position: its span contains the position (its
    // end inclusive, so a zero-width diagnostic matches at its offset), the smallest such
    // span first. Diagnostics are the document's suppression-aware compiler and analyzer
    // diagnostics, each once. Nearby lists the diagnostics on the requested line or at the
    // position, for the not-found hint.
    private async Task<(Diagnostic? Diagnostic, List<Diagnostic> Nearby)> FindDiagnosticAtAsync(
        ResolvedPosition pos, string diagnosticId, int line, CancellationToken cancellationToken)
    {
        var computed = await ComputeTreeDiagnosticsAsync(pos.Document.Project, pos.Tree, runAnalyzers: true, cancellationToken);
        var inTree = computed.Diagnostics
            .Where(d => d.Location.IsInSource && d.Location.SourceTree == pos.Tree)
            .DistinctBy(d => (d.Id, d.Location.SourceSpan, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

        bool At(Diagnostic d) => d.Location.SourceSpan.Start <= pos.Position && pos.Position <= d.Location.SourceSpan.End;

        var diagnostic = inTree
            .Where(d => d.Id == diagnosticId && At(d))
            .OrderBy(d => d.Location.SourceSpan.Length)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .FirstOrDefault();
        var nearby = inTree
            .Where(d => At(d) || d.Location.GetLineSpan().StartLinePosition.Line == line)
            .OrderBy(d => d.Location.SourceSpan.Start)
            .Take(10)
            .ToList();
        return (diagnostic, nearby);
    }

    private object DiagnosticNotFoundError(string filePath, string diagnosticId, int line, int column, List<Diagnostic> nearby) =>
        CreateErrorResponse(
            ErrorCodes.SymbolNotFound,
            $"No diagnostic with ID '{diagnosticId}' found at line {line}, column {column}",
            hint: nearby.Count > 0
                ? $"Found {nearby.Count} other diagnostic(s) on or at this position. Use one of their IDs, or the position get_diagnostics reports."
                : "No diagnostics at this position. Run get_diagnostics to see each diagnostic's line and column.",
            context: new
            {
                filePath,
                diagnosticId,
                line,
                column,
                diagnosticsNearby = nearby.Select(d => new
                {
                    id = d.Id,
                    message = d.GetMessage(),
                    severity = d.Severity.ToString(),
                    line = d.Location.GetLineSpan().StartLinePosition.Line,
                    column = d.Location.GetLineSpan().StartLinePosition.Character,
                    span = new
                    {
                        start = d.Location.SourceSpan.Start,
                        end = d.Location.SourceSpan.End,
                        length = d.Location.SourceSpan.Length
                    }
                }).ToList()
            });

    public async Task<object> GetCodeFixesAsync(
        string filePath, string diagnosticId, int line, int column,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        var (diagnostic, nearby) = await FindDiagnosticAtAsync(pos, diagnosticId, line, cancellationToken);
        if (diagnostic == null) return DiagnosticNotFoundError(filePath, diagnosticId, line, column, nearby);

        var lineSpan = diagnostic.Location.GetLineSpan();

        // The real fixes, from the same providers apply_code_fix uses — index/title/
        // equivalenceKey match apply_code_fix's own listing, so the documented workflow
        // (get_code_fixes → apply_code_fix with fixIndex) shows one consistent reality.
        // Canned guidance strings remain only as a fallback for diagnostics no provider
        // can fix.
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
            ResultCounts.Complete(fixes.Count),
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
                });
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

        var (diagnostic, nearby) = await FindDiagnosticAtAsync(pos, diagnosticId, line, cancellationToken);
        if (diagnostic == null) return DiagnosticNotFoundError(filePath, diagnosticId, line, column, nearby);

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
                ResultCounts.Complete(availableFixes.Count),
                suggestedNextTools: new[]
                {
                    $"apply_code_fix with fixIndex=0 and preview=true to preview the first fix",
                    $"apply_code_fix with fixIndex=0 and preview=false to apply the first fix"
                });
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
                ResultCounts.Complete(changedDocuments.Count),
                suggestedNextTools: new[] { "apply_code_fix with preview=false to apply changes to disk" });
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
            ResultCounts.Complete(applyReport.Count),
            suggestedNextTools: new[] { "get_diagnostics to verify fix resolved the issue" });
    }

    // The code actions every fix provider of the document's project offers for the
    // diagnostic: the built-in providers first, then the analyzer assemblies' own.
    private async Task<List<CodeAction>> GetCodeActionsForDiagnosticAsync(
        Document document, Diagnostic diagnostic, CancellationToken cancellationToken = default)
    {
        var codeActions = new List<CodeAction>();

        foreach (var provider in CodeFixProvidersFor(document.Project))
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

    // The code fixes and refactorings at a position or selection. Fixes are offered for the
    // document's suppression-aware compiler and analyzer diagnostics that intersect the
    // span (each diagnostic once), from the same providers get_code_fixes uses.
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

        if (includeCodeFixes)
        {
            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (syntaxTree != null)
            {
                var computed = await ComputeTreeDiagnosticsAsync(document.Project, syntaxTree, runAnalyzers: true, cancellationToken);
                var diagnostics = computed.Diagnostics
                    .Where(d => d.Location.IsInSource && d.Location.SourceTree == syntaxTree)
                    .Where(d => d.Location.SourceSpan.IntersectsWith(span) || d.Location.SourceSpan.Contains(position))
                    .DistinctBy(d => (d.Id, d.Location.SourceSpan, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))
                    .ToList();

                var codeFixProviders = CodeFixProvidersFor(document.Project);

                foreach (var diagnostic in diagnostics)
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

    // The solution's projects as logical projects (a multi-targeted project once, with its
    // frameworks). targetFramework is the evaluated TargetFramework of a single-targeted
    // project — set in the project file, an imported Directory.Build.props, or through a
    // property — and null for a multi-targeted one, whose frameworks are targetFrameworks.
    // A pattern (* and ? wildcards, case-insensitive) filters by the logical name or a
    // framework-qualified member name; totalCount counts the projects the pattern matched,
    // so only maxProjects truncates.
    public Task<object> GetProjectStructureAsync(
        bool includeReferences,
        bool includeDocuments,
        string? projectNamePattern = null,
        int? maxProjects = null,
        bool summaryOnly = false,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxProjects", maxProjects, MaxProjectsLimit) is { } invalid)
            return Task.FromResult(CreateErrorResponse(invalid));

        var matched = GetLogicalProjects().Projects
            .Where(p => string.IsNullOrEmpty(projectNamePattern)
                || MatchesGlobPattern(p.Name, projectNamePattern)
                || p.Projects.Any(member => MatchesGlobPattern(member.Name, projectNamePattern)))
            .ToList();
        var listed = matched.Take(maxProjects ?? int.MaxValue).ToList();
        var counts = ResultCounts.Capped(matched.Count, listed.Count);
        var solutionPath = FormatPath(_solution!.FilePath);

        if (summaryOnly)
        {
            var summary = listed.Select(p => new
            {
                name = p.Name,
                targetFrameworks = p.IsMultiTargeted ? p.TargetFrameworks : null,
                documentCount = DocumentPaths(p).Count,
                projectReferenceCount = ProjectReferenceNames(p).Count,
                language = p.Projects[0].Language
            }).ToList();

            return Task.FromResult(CreateSuccessResponse(
                data: new { solutionPath, projects = summary },
                counts,
                suggestedNextTools: new[]
                {
                    "get_project_structure with summaryOnly=false for full details",
                    "get_project_structure with projectNamePattern to filter projects"
                }));
        }

        using var evaluator = new MSBuildProjectEvaluator();
        var projects = new List<object>();
        foreach (var logical in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var referenceDisplays = logical.Projects
                .SelectMany(p => p.MetadataReferences)
                .Select(r => r.Display ?? "Unknown")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var documents = logical.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath != null)
                .DistinctBy(d => d.FilePath!, LoadFidelityClassifier.PathComparer)
                .ToList();

            var references = includeReferences
                ? referenceDisplays.Take(100)
                    .Concat(referenceDisplays.Count > 100 ? new[] { $"... and {referenceDisplays.Count - 100} more" } : Array.Empty<string>())
                    .ToList()
                : null;
            var documentList = includeDocuments
                ? documents.Take(500)
                    .Select(d => new { name = d.Name, filePath = FormatPath(d.FilePath), folders = d.Folders.ToList() })
                    .ToList()
                : null;
            if (documentList != null && documents.Count > 500)
            {
                documentList.Add(new { name = $"... and {documents.Count - 500} more documents", filePath = string.Empty, folders = new List<string>() });
            }

            projects.Add(new
            {
                name = logical.Name,
                filePath = logical.FilePath == null ? null : FormatPath(logical.FilePath),
                language = logical.Projects[0].Language,
                outputPath = logical.IsMultiTargeted || logical.Projects[0].OutputFilePath == null
                    ? null
                    : FormatPath(logical.Projects[0].OutputFilePath),
                outputPaths = logical.IsMultiTargeted
                    ? logical.Projects
                        .Where(p => p.OutputFilePath != null)
                        .Select(p => new { targetFramework = LogicalProject.TargetFrameworkOf(p), path = FormatPath(p.OutputFilePath) })
                        .ToList()
                    : null,
                targetFramework = logical.IsMultiTargeted ? null : EvaluatedTargetFramework(evaluator, logical),
                targetFrameworks = logical.IsMultiTargeted ? logical.TargetFrameworks : null,
                documentCount = documents.Count,
                referenceCount = referenceDisplays.Count,
                references,
                projectReferences = ProjectReferenceNames(logical),
                documents = documentList
            });
        }

        return Task.FromResult(CreateSuccessResponse(
            data: new { solutionPath, projects },
            counts,
            suggestedNextTools: new[]
            {
                "get_diagnostics for a project to check for issues",
                "dependency_graph to visualize project dependencies"
            }));
    }

    // The largest maxProjects get_project_structure accepts.
    private const int MaxProjectsLimit = 10000;

    // The distinct document paths of a logical project's members.
    private static List<string> DocumentPaths(LogicalProject project) =>
        project.Projects.SelectMany(p => p.Documents)
            .Select(d => d.FilePath ?? d.Name)
            .Distinct(LoadFidelityClassifier.PathComparer)
            .ToList();

    // The logical names of the projects a logical project references, distinct, in
    // reference order.
    private List<string> ProjectReferenceNames(LogicalProject project)
    {
        var model = GetLogicalProjects();
        return project.Projects
            .SelectMany(p => p.ProjectReferences)
            .Select(r => _solution!.GetProject(r.ProjectId))
            .Select(p => p == null ? "Unknown" : model.Of(p).Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // The TargetFramework MSBuild evaluates for a single-targeted project file (the legacy
    // TargetFrameworkVersion for a non-SDK project); the framework of the member's name when
    // the project loaded with one; null when there is no project file to evaluate or it
    // cannot be evaluated.
    private static string? EvaluatedTargetFramework(MSBuildProjectEvaluator evaluator, LogicalProject project)
    {
        if (project.TargetFrameworks.Count == 1) return project.TargetFrameworks[0];
        if (project.FilePath == null || !File.Exists(project.FilePath)) return null;
        try
        {
            return evaluator.Property(project.FilePath, null, "TargetFramework")
                ?? evaluator.Property(project.FilePath, null, "TargetFrameworkVersion");
        }
        catch (Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            return null;
        }
    }

    // A file's organized usings, over EVERY document that compiles it (one per target
    // framework): a using is removed only when the compiler marks it unnecessary (CS8019)
    // in all of them, keeping the preprocessor directives in its trivia; the rest are
    // grouped global → plain (System root namespaces first) → static → alias,
    // alphabetical within each group — unless preprocessor directives run through the
    // using block, which Roslyn's own Remove-and-Sort also leaves in order (moving usings
    // across a #region/#if would break the directive structure). The file header (the
    // first using's leading trivia) stays at the top. A document whose compilation cannot
    // resolve System.Object (an unrestored project: no references) is refused — every
    // using would read as unnecessary there.
    private static async Task<OrganizedUsings?> ComputeOrganizedUsingsAsync(
        IReadOnlyList<Document> documents, CancellationToken cancellationToken = default)
    {
        var root = await documents[0].GetSyntaxRootAsync(cancellationToken);
        if (root is not CompilationUnitSyntax unit) return null;

        var usings = unit.Usings;
        if (usings.Count == 0) return new OrganizedUsings(unit, 0, 0, false, false, null);

        HashSet<int>? removable = null;
        foreach (var document in documents)
        {
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null) return null;
            if (model.Compilation.GetSpecialType(SpecialType.System_Object).TypeKind == TypeKind.Error)
            {
                return new OrganizedUsings(null, usings.Count, 0, false, false,
                    $"Cannot organize usings: project '{LogicalProject.SplitName(document.Project.Name).Name}' does not resolve System.Object, so every using would look unnecessary (are its references restored?)");
            }
            var unusedSpans = model.GetDiagnostics(cancellationToken: cancellationToken)
                .Where(d => d.Id == "CS8019")
                .Select(d => d.Location.SourceSpan)
                .ToList();
            var unusedHere = Enumerable.Range(0, usings.Count)
                .Where(i => unusedSpans.Any(span => span.Contains(usings[i].Span) || usings[i].Span.Contains(span)))
                .ToHashSet();
            if (removable == null) removable = unusedHere;
            else removable.IntersectWith(unusedHere);
        }

        // The header leaves the first using before anything moves, and returns on top. A
        // removed using hands its preprocessor directives (from its first directive on,
        // with any disabled text between them) to the next using that stays, or to the
        // declaration after the block.
        var header = usings[0].GetLeadingTrivia();
        var kept = new List<UsingDirectiveSyntax>();
        var carried = new List<SyntaxTrivia>();
        for (var i = 0; i < usings.Count; i++)
        {
            var leading = i == 0 ? SyntaxFactory.TriviaList() : usings[i].GetLeadingTrivia();
            if (removable!.Contains(i))
            {
                carried.AddRange(leading.SkipWhile(t => !t.IsDirective));
                continue;
            }
            kept.Add(usings[i].WithLeadingTrivia(carried.Concat(leading)));
            carried.Clear();
        }

        var directivesInBlock = kept.Any(u =>
            u.GetLeadingTrivia().Any(t => t.IsDirective) || u.GetTrailingTrivia().Any(t => t.IsDirective));
        var ordered = directivesInBlock ? kept : kept.OrderBy(UsingSortKey).ToList();
        var organized = unit.WithUsings(SyntaxFactory.List(ordered));
        if (carried.Count > 0) organized = PrependToFirstDeclaration(organized, SyntaxFactory.TriviaList(carried));
        var newRoot = WithLeadingHeader(organized, header);

        return new OrganizedUsings(newRoot, usings.Count, removable!.Count,
            newRoot.ToFullString() != unit.ToFullString(), directivesInBlock, null);
    }

    // Puts the file header back on top: onto the first using, else the first declaration
    // after the usings.
    private static CompilationUnitSyntax WithLeadingHeader(CompilationUnitSyntax unit, SyntaxTriviaList header)
    {
        if (unit.Usings.Count == 0) return PrependToFirstDeclaration(unit, header);
        var first = unit.Usings[0];
        return unit.ReplaceNode(first, first.WithLeadingTrivia(header.AddRange(first.GetLeadingTrivia())));
    }

    // Prepends trivia to what follows the using block: the first member, else the
    // end-of-file token.
    private static CompilationUnitSyntax PrependToFirstDeclaration(CompilationUnitSyntax unit, SyntaxTriviaList trivia)
    {
        if (unit.Members.Count > 0)
        {
            var member = unit.Members[0];
            return unit.ReplaceNode(member, member.WithLeadingTrivia(trivia.AddRange(member.GetLeadingTrivia())));
        }
        var eof = unit.EndOfFileToken;
        return unit.WithEndOfFileToken(eof.WithLeadingTrivia(trivia.AddRange(eof.LeadingTrivia)));
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
    // directives are reordered or a new one is inserted before them (add_missing_imports).
    // The header is the leading trivia of the directive that was syntactically first;
    // rather than let it travel to the middle of the file with that directive, it is moved
    // onto whichever directive ends up first — the same contract as Roslyn's "Remove and
    // Sort Usings" / dotnet format. No-op when the original first directive is still first
    // (or the block is empty).
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

    // Rebuilds the compilation unit with a reduced using list while keeping the file header
    // (the original first using's leading trivia) pinned to the top: onto the new first
    // using when one remains, or onto the first member / EOF token when the block empties —
    // so a header on the sole using is not deleted with it.
    private static CompilationUnitSyntax WithUsingsPreservingHeader(
        CompilationUnitSyntax unit, UsingDirectiveSyntax originalFirst, IReadOnlyList<UsingDirectiveSyntax> newUsings)
    {
        if (newUsings.Count > 0)
            return unit.WithUsings(PinHeaderTrivia(originalFirst, newUsings));

        return WithLeadingHeader(unit.WithUsings(default), originalFirst.GetLeadingTrivia());
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

        var linked = DocumentsSharingPath(document);
        var organized = await ComputeOrganizedUsingsAsync(linked, cancellationToken);
        if (organized == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Not a valid C# file",
                hint: "This tool only works with C# source files",
                context: new { filePath }
            );
        }
        if (organized.Error != null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                organized.Error,
                hint: "Restore the project (dotnet restore) and reload the solution, then retry.",
                context: new { filePath });
        }

        var organizedText = organized.NewRoot!.ToFullString();
        if (preview || !organized.Changed)
        {
            return CreateSuccessResponse(
                data: new
                {
                    organizedText,
                    totalUsings = organized.TotalUsings,
                    removedUsings = organized.RemovedUsings,
                    changed = organized.Changed,
                    sortSkippedForDirectives = organized.SortSkippedForDirectives,
                    preview = true,
                    applied = false
                },
                suggestedNextTools: organized.Changed
                    ? new[] { "organize_usings with preview=false to apply", "organize_usings_batch to process multiple files" }
                    : new[] { "Usings are already organized - nothing to apply" }
            );
        }

        var newSolution = WithTextForEvery(_solution!, linked, organizedText);
        var (ok, applyError, _, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                organizedText,
                totalUsings = organized.TotalUsings,
                removedUsings = organized.RemovedUsings,
                changed = organized.Changed,
                sortSkippedForDirectives = organized.SortSkippedForDirectives,
                preview = false,
                applied = true,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[] { "get_diagnostics to verify the file still compiles" }
        );
    }

    // The same text for every document of a file (each target framework's copy), so the
    // solution never holds two versions of one file.
    private static Solution WithTextForEvery(Solution solution, IEnumerable<Document> documents, string text)
    {
        var sourceText = SourceText.From(text, Encoding.UTF8);
        foreach (var document in documents) solution = solution.WithDocumentText(document.Id, sourceText);
        return solution;
    }

    // The files of a batch's scope, each once: every distinct document path of the scope's
    // projects (filtered by file-name pattern), with the logical project it belongs to and
    // every document of the solution that compiles it.
    private List<(string FilePath, LogicalProject Project, List<Document> Documents)> BatchFiles(
        IReadOnlyList<Project> scope, string? filePattern)
    {
        var model = GetLogicalProjects();
        var seen = new HashSet<string>(LoadFidelityClassifier.PathComparer);
        var files = new List<(string, LogicalProject, List<Document>)>();
        foreach (var project in scope)
        {
            foreach (var document in project.Documents)
            {
                if (document.FilePath == null) continue;
                if (!string.IsNullOrEmpty(filePattern) && !MatchesGlobPattern(Path.GetFileName(document.FilePath), filePattern)) continue;
                if (!seen.Add(document.FilePath)) continue;
                files.Add((document.FilePath, model.Of(project), DocumentsSharingPath(document)));
            }
        }
        return files;
    }

    // Whether a document is generated code, by the convention analyzers and the IDE use:
    // generated_code = true in its analyzer config; a file name ending in .g.cs, .g.i.cs,
    // .designer.cs or .generated.cs, or starting with TemporaryGeneratedFile_; or a leading
    // <auto-generated> / <autogenerated> comment. The batch tools leave such files alone.
    private static async Task<bool> IsGeneratedCodeAsync(Document document, CancellationToken cancellationToken)
    {
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (tree == null) return false;
        if (document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree)
                .TryGetValue("generated_code", out var configured)
            && bool.TryParse(configured, out var isGenerated))
        {
            return isGenerated;
        }

        var fileName = Path.GetFileName(document.FilePath ?? document.Name);
        if (fileName.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase)) return true;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (new[] { ".designer", ".generated", ".g", ".g.i" }.Any(suffix => stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return true;

        var root = await tree.GetRootAsync(cancellationToken);
        return root.GetLeadingTrivia().Any(t =>
            (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
            && (t.ToString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
                || t.ToString().Contains("<autogenerated", StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<object> OrganizeUsingsBatchAsync(
        string? projectName, string? filePattern, bool preview = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (scope, scopeError) = ResolveProjectScope(projectName);
        if (scope == null) return scopeError!;

        var processedFiles = new List<object>();
        var totalFiles = 0;
        var filesWithChanges = 0;
        var changedSolution = _solution!;

        foreach (var (path, project, documents) in BatchFiles(scope, filePattern))
        {
            if (await IsGeneratedCodeAsync(documents[0], cancellationToken)) continue;
            totalFiles++;
            try
            {
                var organized = await ComputeOrganizedUsingsAsync(documents, cancellationToken);
                if (organized == null) continue;
                if (organized.Error != null)
                {
                    processedFiles.Add(new
                    {
                        filePath = FormatPath(path),
                        fileName = Path.GetFileName(path),
                        projectName = project.Name,
                        error = organized.Error
                    });
                    continue;
                }
                if (organized.TotalUsings == 0 || !organized.Changed) continue;

                filesWithChanges++;
                processedFiles.Add(new
                {
                    filePath = FormatPath(path),
                    fileName = Path.GetFileName(path),
                    projectName = project.Name,
                    totalUsings = organized.TotalUsings,
                    removedUsings = organized.RemovedUsings,
                    sortSkippedForDirectives = organized.SortSkippedForDirectives,
                    preview
                });

                if (!preview)
                {
                    changedSolution = WithTextForEvery(changedSolution, documents, organized.NewRoot!.ToFullString());
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
                    filePath = FormatPath(path),
                    fileName = Path.GetFileName(path),
                    projectName = project.Name,
                    error = ex.Message
                });
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
            ResultCounts.Complete(processedFiles.Count),
            suggestedNextTools: preview
                ? new[] { "organize_usings_batch with preview=false to apply changes" }
                : new[] { "get_diagnostics to verify the changes compile" });
    }

    public async Task<object> FormatDocumentBatchAsync(
        string? projectName, bool includeTests = true, bool preview = true, string? filePattern = null,
        string? mode = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // An absent or blank mode is the default, whitespace.
        if (ParameterValidation.Choice("mode", string.IsNullOrWhiteSpace(mode) ? null : mode, FormatModes, out var canonicalMode) is { } invalidMode)
            return CreateErrorResponse(invalidMode);
        var normalizedMode = canonicalMode ?? "whitespace";
        var canonical = normalizedMode == "canonical";

        var (scope, scopeError) = ResolveProjectScope(projectName);
        if (scope == null) return scopeError!;

        var processedFiles = new List<object>();
        var totalFiles = 0;
        var filesFormatted = 0;
        var changedSolution = _solution!;

        foreach (var (path, project, documents) in BatchFiles(scope, filePattern))
        {
            if (!includeTests && project.Name.Contains("Test", StringComparison.OrdinalIgnoreCase)) continue;
            if (await IsGeneratedCodeAsync(documents[0], cancellationToken)) continue;
            totalFiles++;

            try
            {
                var originalText = (await documents[0].GetTextAsync(cancellationToken)).ToString();
                var formattedText = originalText;
                if (canonical)
                {
                    // Canonical re-print of the syntax tree: the only engine that
                    // expands minified one-liner code, at the cost of rewriting
                    // stylistic whitespace (suppression-operator spacing becomes
                    // "null !", blank lines between members are dropped). Opt-in.
                    var root = await documents[0].GetSyntaxRootAsync(cancellationToken);
                    if (root == null) continue;
                    formattedText = root.NormalizeWhitespace().ToFullString();
                }
                else
                {
                    // Whitespace-only formatting per the workspace's formatting rules —
                    // fixes indentation and token spacing, preserves the author's
                    // structural choices (single-line auto-properties, blank lines,
                    // "null!"). Same contract as `dotnet format`. Each target framework's
                    // copy formats in turn, so code active under any framework's
                    // preprocessor symbols is formatted.
                    foreach (var document in documents)
                    {
                        var current = document.WithText(SourceText.From(formattedText));
                        var formattedDocument = await Formatter.FormatAsync(current, cancellationToken: cancellationToken);
                        formattedText = (await formattedDocument.GetTextAsync(cancellationToken)).ToString();
                    }
                }

                if (originalText == formattedText) continue;

                filesFormatted++;
                processedFiles.Add(new
                {
                    filePath = FormatPath(path),
                    fileName = Path.GetFileName(path),
                    projectName = project.Name,
                    preview
                });

                if (!preview)
                {
                    changedSolution = WithTextForEvery(changedSolution, documents, formattedText);
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
                    filePath = FormatPath(path),
                    fileName = Path.GetFileName(path),
                    projectName = project.Name,
                    error = ex.Message
                });
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
            ResultCounts.Complete(processedFiles.Count),
            suggestedNextTools: preview
                ? new[] { "format_document_batch with preview=false to apply changes" }
                : new[] { "get_diagnostics to verify the formatting compiles" });
    }

    private static readonly IReadOnlyList<string> FormatModes = new[] { "whitespace", "canonical" };
}
