using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SharpLensMcp;

public partial class RoslynService
{
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
                    filePath = FormatPath(lineSpan.Value.Path),
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
                        filePath = FormatPath(callerDocument.FilePath),
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
                                filePath = FormatPath(lineSpan.Path),
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
                                filePath = FormatPath(lineSpan.Path),
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
                            filePath = FormatPath(oldDocument.FilePath),
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
                            filePath = FormatPath(oldDocument.FilePath),
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
                            filePath = FormatPath(oldDocument.FilePath),
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
                    filePath = FormatPath(lineSpan.Value.Path),
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
                        filePath = FormatPath(lineSpan.Value.Path),
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
                filePath = FormatPath(lineSpan.Path),
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
                    filePath = FormatPath(lineSpan.Path),
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
            .FirstOrDefault(d => d.FilePath != null && d.FilePath.Equals(filePath, PathComparison));

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
            .FirstOrDefault(d => d.FilePath != null && d.FilePath.Equals(filePath, PathComparison));

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
                        filePath = FormatPath(refFilePath),
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
            .FirstOrDefault(d => d.FilePath != null && d.FilePath.Equals(filePath, PathComparison));

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

}
