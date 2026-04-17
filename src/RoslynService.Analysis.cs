using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

public partial class RoslynService
{
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
                var compilation = await GetProjectCompilationAsync(project);
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
                var compilation = await GetProjectCompilationAsync(project);
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
                filePath = FormatPath(lineSpan.Path),
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
                    filePath = FormatPath(lineSpan.Path),
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

        if (changedSolution == null || changedSolution == _solution)
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
                    filePath = FormatPath(addedDoc.FilePath) ?? $"NewFile_{addedDoc.Name}",
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
                    filePath = FormatPath(newDoc.FilePath),
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
                    filePath = FormatPath(removedDoc.FilePath),
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
                targetFramework = project.CompilationOptions?.Platform.ToString(),
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
                        filePath = FormatPath(document.FilePath),
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
                        filePath = FormatPath(document.FilePath),
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
                        filePath = FormatPath(document.FilePath),
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
                        filePath = FormatPath(document.FilePath),
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


}
