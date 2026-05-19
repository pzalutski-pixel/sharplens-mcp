using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SharpLensMcp;

// Read-only analysis/inspection tools. Previously co-located with the actual
// refactoring tools in RoslynService.Refactoring.cs; the split mirrors the
// v1.5.0 partial-class organization.
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
        var data = await FindUnusedCodeDataAsyncCore(projectName, includePrivate, includeInternal, symbolKindFilter, maxResults ?? 50);

        var countByKind = data.Symbols
            .GroupBy(s => s.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        return CreateSuccessResponse(
            data: new
            {
                projectName = projectName ?? "All projects",
                countByKind,
                unusedSymbols = data.Symbols.Select(s => new
                {
                    name = s.Name,
                    fullyQualifiedName = s.FullName,
                    kind = s.Kind,
                    accessibility = s.Accessibility,
                    containingType = s.ContainingType,
                    filePath = s.FilePath,
                    line = s.Line,
                    column = s.Column
                }).ToList()
            },
            suggestedNextTools: data.Symbols.Count > 0
                ? new[] { "find_references to verify symbol is truly unused", "rename_symbol or delete unused code" }
                : new[] { "No unused code found - codebase is clean" },
            totalCount: data.TotalCount,
            returnedCount: data.Symbols.Count
        );
    }

    // Internal typed-data variant used by GetProjectHealthAsync.
    internal Task<UnusedCodeData> FindUnusedCodeDataAsync(
        string? projectName,
        bool includePrivate,
        bool includeInternal,
        string? symbolKindFilter,
        int maxResults) => FindUnusedCodeDataAsyncCore(projectName, includePrivate, includeInternal, symbolKindFilter, maxResults);

    private async Task<UnusedCodeData> FindUnusedCodeDataAsyncCore(
        string? projectName,
        bool includePrivate,
        bool includeInternal,
        string? symbolKindFilter,
        int maxResults)
    {
        var unusedSymbols = new List<UnusedSymbolEntry>();
        var maxResultsToReturn = maxResults;

        var projectsToAnalyze = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name == projectName);

        foreach (var project in projectsToAnalyze)
        {
            if (unusedSymbols.Count >= maxResultsToReturn)
                break;

            var compilation = await GetProjectCompilationAsync(project);
            if (compilation == null) continue;

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

                    if (referenceCount <= 1 && !hasReferencedMembers)
                    {
                        var location = typeSymbol.Locations.FirstOrDefault(loc => loc.IsInSource);
                        if (location != null)
                        {
                            var lineSpan = location.GetLineSpan();
                            unusedSymbols.Add(new UnusedSymbolEntry(
                                Name: typeSymbol.Name,
                                FullName: typeSymbol.ToDisplayString(),
                                Kind: GetTypeKindString(typeSymbol),
                                Accessibility: typeSymbol.DeclaredAccessibility.ToString(),
                                ContainingType: null,
                                FilePath: FormatPath(lineSpan.Path),
                                Line: lineSpan.StartLinePosition.Line,
                                Column: lineSpan.StartLinePosition.Character));
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
                            unusedSymbols.Add(new UnusedSymbolEntry(
                                Name: member.Name,
                                FullName: member.ToDisplayString(),
                                Kind: member.Kind.ToString(),
                                Accessibility: member.DeclaredAccessibility.ToString(),
                                ContainingType: member.ContainingType?.ToDisplayString(),
                                FilePath: FormatPath(lineSpan.Path),
                                Line: lineSpan.StartLinePosition.Line,
                                Column: lineSpan.StartLinePosition.Character));
                        }
                    }
                }
            }
        }

        return new UnusedCodeData(TotalCount: unusedSymbols.Count, Symbols: unusedSymbols);
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

        // Find factory methods in other types that return this type. Re-resolve
        // `type` in each project's augmented compilation: FindTypeByNameAsync
        // returns the type from the BASE compilation, but allTypes (and their
        // return types) come from the AUGMENTED compilation. When a project has
        // source generators (implicit .NET SDK generators count), base != augmented
        // and SymbolEqualityComparer.Default returns false for the "same" source
        // type across compilations — silently breaking external-factory detection.
        var typeMetadataName = type.ToDisplayString();
        var externalFactories = new List<object>();
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project);
            if (compilation == null) continue;

            var typeInCompilation = compilation.GetTypeByMetadataName(typeMetadataName);
            if (typeInCompilation == null) continue;

            var allTypes = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>();
            foreach (var t in allTypes)
            {
                if (SymbolEqualityComparer.Default.Equals(t, typeInCompilation)) continue;

                var factories = t.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => m.IsStatic &&
                               m.DeclaredAccessibility == Accessibility.Public &&
                               (m.Name.StartsWith("Create") || m.Name.StartsWith("Build") || m.Name.StartsWith("New")) &&
                               SymbolEqualityComparer.Default.Equals(m.ReturnType, typeInCompilation))
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
                typeKind = GetTypeKindString(type),
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

    // Helpers for member signatures used by GetMissingMembersAsync and friends.
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

            if (IsSuccessResponse(result))
            {
                results.Add(new
                {
                    typeName,
                    methodName,
                    success = true,
                    data = GetResponseData(result)
                });
            }
            else
            {
                errors.Add(new
                {
                    typeName,
                    methodName,
                    success = false,
                    error = GetResponseError(result)
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

    // Transitive call-graph: BFS up to maxDepth, capped at maxNodes, with cycle detection.
    // Replaces the N-round-trip pattern of agents calling find_callers / get_outgoing_calls
    // iteratively to trace an impact chain. Returns nodes + edges (streamable) rather than
    // a nested tree (avoids exponential blowup on diamond-shaped graphs).
    public async Task<object> GetCallGraphAsync(
        string filePath,
        int line,
        int column,
        string direction = "callees",
        int maxDepth = 3,
        int maxNodes = 100)
    {
        EnsureSolutionLoaded();

        if (maxDepth <= 0 || maxDepth > 10)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxDepth must be between 1 and 10 (got {maxDepth})",
                hint: "Depths above 10 explode for highly-connected codebases. Drill iteratively with smaller depths.",
                context: new { maxDepth });
        }

        var dir = direction?.ToLowerInvariant() ?? "callees";
        if (dir != "callees" && dir != "callers" && dir != "both")
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"direction must be 'callees', 'callers', or 'both' (got '{direction}')",
                context: new { direction });
        }

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
                context: new { filePath });
        }

        var semanticModel = await document.GetSemanticModelAsync();
        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(ErrorCodes.AnalysisFailed, "Could not get semantic model/syntax tree", context: new { filePath });
        }

        var position = GetPosition(syntaxTree, line, column);
        var token = syntaxTree.GetRoot().FindToken(position);
        var node = token.Parent;
        if (node == null)
        {
            return CreateErrorResponse(ErrorCodes.SymbolNotFound, "No symbol found at position", context: new { filePath, line, column });
        }

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var rootSymbol = (symbolInfo.Symbol ?? semanticModel.GetDeclaredSymbol(node)) as IMethodSymbol;
        if (rootSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a method symbol",
                hint: "Position the cursor on a method declaration or call site.",
                context: new { filePath, line, column });
        }

        // Node table keyed by symbol; assign sequential ids for the wire format.
        var nodeIds = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var nodes = new List<object>();
        var edges = new List<object>();
        // Typed adjacency for cycle-detection BFS — avoids dynamic dispatch through
        // anonymous-typed edge records (which was both slow and fragile).
        var adjacency = new Dictionary<int, List<int>>();
        var cyclesDetected = new HashSet<string>(StringComparer.Ordinal);
        var truncatedByDepth = false;
        var truncatedByNodes = false;

        int RegisterNode(IMethodSymbol sym, int depth)
        {
            if (nodeIds.TryGetValue(sym, out var existing)) return existing;
            var id = nodes.Count;
            nodeIds[sym] = id;
            nodes.Add(new
            {
                id,
                fullName = sym.ToDisplayString(),
                kind = sym.MethodKind.ToString(),
                location = GetSymbolLocation(sym),
                depth
            });
            return id;
        }

        int rootId = RegisterNode(rootSymbol, 0);

        var queue = new Queue<(IMethodSymbol sym, int depth)>();
        queue.Enqueue((rootSymbol, 0));
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { rootSymbol };

        while (queue.Count > 0)
        {
            if (nodes.Count >= maxNodes)
            {
                truncatedByNodes = true;
                break;
            }

            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                truncatedByDepth = true;
                continue;
            }

            var currentId = nodeIds[current];

            // Callees: walk the method body for invocations.
            if (dir == "callees" || dir == "both")
            {
                var calleeSymbols = await CollectCalleesAsync(current);
                foreach (var callee in calleeSymbols)
                {
                    if (visited.Contains(callee))
                    {
                        // Cycle: an edge that points back into something we've already processed.
                        if (nodeIds.TryGetValue(callee, out var existingId))
                        {
                            AddEdge(edges, adjacency, currentId, existingId);
                            if (SymbolEqualityComparer.Default.Equals(callee, current) ||
                                IsAncestor(existingId, currentId, adjacency))
                            {
                                cyclesDetected.Add($"{current.ToDisplayString()} -> {callee.ToDisplayString()}");
                            }
                        }
                        continue;
                    }
                    visited.Add(callee);
                    if (nodes.Count >= maxNodes) { truncatedByNodes = true; break; }
                    var calleeId = RegisterNode(callee, depth + 1);
                    AddEdge(edges, adjacency, currentId, calleeId);
                    queue.Enqueue((callee, depth + 1));
                }
            }

            // Callers: SymbolFinder gives us call sites; map each to the calling method.
            if (dir == "callers" || dir == "both")
            {
                var callers = await SymbolFinder.FindCallersAsync(current, _solution!);
                foreach (var callerInfo in callers)
                {
                    if (callerInfo.CallingSymbol is not IMethodSymbol callerMethod) continue;
                    if (visited.Contains(callerMethod))
                    {
                        if (nodeIds.TryGetValue(callerMethod, out var existingId))
                        {
                            AddEdge(edges, adjacency, existingId, currentId);
                            if (SymbolEqualityComparer.Default.Equals(callerMethod, current))
                            {
                                cyclesDetected.Add($"{callerMethod.ToDisplayString()} -> {current.ToDisplayString()}");
                            }
                        }
                        continue;
                    }
                    visited.Add(callerMethod);
                    if (nodes.Count >= maxNodes) { truncatedByNodes = true; break; }
                    var callerId = RegisterNode(callerMethod, depth + 1);
                    AddEdge(edges, adjacency, callerId, currentId);
                    queue.Enqueue((callerMethod, depth + 1));
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                root = new
                {
                    id = rootId,
                    fullName = rootSymbol.ToDisplayString(),
                    kind = rootSymbol.MethodKind.ToString(),
                    location = GetSymbolLocation(rootSymbol)
                },
                direction = dir,
                maxDepth,
                nodes,
                edges,
                truncatedByDepth,
                truncatedByNodes,
                cyclesDetected = cyclesDetected.ToList()
            },
            suggestedNextTools: new[]
            {
                "get_outgoing_calls or find_callers for single-hop detail at a specific node",
                "analyze_change_impact for the impact of changing this method"
            },
            totalCount: nodes.Count,
            returnedCount: nodes.Count);
    }

    // Walks the method's body collecting distinct method symbols it invokes.
    // Excludes operator/conversion/property-accessor calls to keep the graph readable;
    // callers can drill into properties via get_outgoing_calls if they need that.
    private async Task<List<IMethodSymbol>> CollectCalleesAsync(IMethodSymbol method)
    {
        var result = new List<IMethodSymbol>();
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            var syntax = await declRef.GetSyntaxAsync();
            var document = _solution!.GetDocument(declRef.SyntaxTree);
            if (document == null) continue;
            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null) continue;

            var invocations = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>();
            foreach (var inv in invocations)
            {
                var calledSymbol = semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                if (calledSymbol == null) continue;
                if (calledSymbol.MethodKind != MethodKind.Ordinary
                    && calledSymbol.MethodKind != MethodKind.LocalFunction) continue;
                if (!result.Any(m => SymbolEqualityComparer.Default.Equals(m, calledSymbol)))
                    result.Add(calledSymbol);
            }
        }
        return result;
    }

    // Records the edge in both the wire-format list (for output) and the typed adjacency
    // map (for in-flight cycle detection). Kept in sync at every edge addition.
    private static void AddEdge(List<object> edges, Dictionary<int, List<int>> adjacency, int from, int to)
    {
        edges.Add(new { from, to, kind = "calls" });
        if (!adjacency.TryGetValue(from, out var neighbors))
        {
            neighbors = new List<int>();
            adjacency[from] = neighbors;
        }
        neighbors.Add(to);
    }

    // Detects whether `candidateId` is reachable from `targetId` via existing edges
    // (in-flight graph). Used to record true cycles (A->B->A) rather than back-edges
    // to siblings. Typed adjacency: O(V+E) BFS, no boxing.
    private static bool IsAncestor(int candidateId, int targetId, Dictionary<int, List<int>> adjacency)
    {
        if (candidateId == targetId) return true;
        var seen = new HashSet<int> { targetId };
        var queue = new Queue<int>();
        queue.Enqueue(targetId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adjacency.TryGetValue(cur, out var neighbors)) continue;
            foreach (var to in neighbors)
            {
                if (to == candidateId) return true;
                if (seen.Add(to)) queue.Enqueue(to);
            }
        }
        return false;
    }
}
