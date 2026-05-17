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

        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(newName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{newName}' is not a valid C# identifier",
                hint: "Identifiers must start with a letter or underscore and contain only letters, digits, or underscores.",
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

        var document = TryFindDocument(filePath);
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

        var fields = typeSymbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared)
            .Select(f => new ConstructorMember(
                Name: f.Name,
                Type: f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                IsReadOnly: f.IsReadOnly,
                IsNullable: f.NullableAnnotation == NullableAnnotation.Annotated,
                ParamName: ToCamelCase(f.Name.TrimStart('_'))))
            .ToList();

        var properties = new List<ConstructorMember>();
        if (includeProperties)
        {
            properties = typeSymbol.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && !p.IsReadOnly && p.SetMethod != null &&
                           p.SetMethod.DeclaredAccessibility != Accessibility.Private)
                .Select(p => new ConstructorMember(
                    Name: p.Name,
                    Type: p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    IsReadOnly: false,
                    IsNullable: p.NullableAnnotation == NullableAnnotation.Annotated,
                    ParamName: ToCamelCase(p.Name)))
                .ToList();
        }

        var allMembers = fields.Concat(properties).ToList();

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

        var parameters = allMembers
            .Select(m => $"{m.Type} {m.ParamName}")
            .ToList();

        sb.AppendLine($"{accessibility} {typeName}({string.Join(", ", parameters)})");
        sb.AppendLine("{");

        foreach (var member in allMembers)
        {
            if (initializeToDefault && member.IsNullable)
            {
                sb.AppendLine($"    {member.Name} = {member.ParamName} ?? default;");
            }
            else
            {
                sb.AppendLine($"    {member.Name} = {member.ParamName};");
            }
        }

        sb.Append("}");

        return CreateSuccessResponse(
            data: new
            {
                // generate_constructor is generation-only by design — it returns the
                // constructor source for the caller to insert via Edit/Write. Flagged
                // here so consumers don't assume the workspace was mutated.
                appliesEditsAutomatically = false,
                typeName = typeSymbol.ToDisplayString(),
                constructorCode = sb.ToString(),
                parameterCount = allMembers.Count,
                fields = fields.Select(f => f.Name).ToList(),
                properties = properties.Select(p => p.Name).ToList(),
                parameters = allMembers.Select(m => new { name = m.ParamName, type = m.Type }).ToList()
            },
            suggestedNextTools: new[]
            {
                "Use Edit/Write to insert the generated constructorCode into the class",
                "validate_code on constructorCode to confirm it compiles",
                "sync_documents after editing, then get_diagnostics to check for errors"
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
    /// Changes a method/constructor/local-function signature and updates all call sites.
    /// Supports add, remove, rename, and reorder parameter operations.
    /// Preview returns the planned diff; preview=false applies edits via SolutionEditor.
    /// </summary>
    public async Task<object> ChangeSignatureAsync(
        string filePath,
        int line,
        int column,
        List<SignatureChange> changes,
        bool preview = true)
    {
        EnsureSolutionLoaded();

        var document = TryFindDocument(filePath);
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

        // Accept any of the three declaration kinds: instance/static method, constructor, local function.
        var declNode = node?.AncestorsAndSelf()
            .FirstOrDefault(n => n is MethodDeclarationSyntax
                              || n is ConstructorDeclarationSyntax
                              || n is LocalFunctionStatementSyntax);

        if (declNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method, constructor, or local function found at position",
                hint: "Position cursor on a method/constructor/local-function declaration",
                context: new { line, column }
            );
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(declNode) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not resolve declaration to an IMethodSymbol",
                context: new { declarationKind = declNode.GetType().Name }
            );
        }

        var currentParams = methodSymbol.Parameters.Select(p => new
        {
            name = p.Name,
            type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            hasDefault = p.HasExplicitDefaultValue,
            defaultValue = p.HasExplicitDefaultValue ? FormatDefaultValueLiteral(p.ExplicitDefaultValue, p.Type) : null
        }).ToList();

        // Build a provenance list: each new parameter slot maps either to an old parameter
        // index, or is brand-new (oldIndex == -1) with a default literal.
        // This drives both the declaration rewrite and the per-call-site argument transform.
        var removedParams = new HashSet<string>();
        var renamedParams = new Dictionary<string, string>();

        foreach (var change in changes)
        {
            if (change.Action == "remove" && change.Name != null) removedParams.Add(change.Name);
            else if (change.Action == "rename" && change.Name != null && change.NewName != null)
                renamedParams[change.Name] = change.NewName;
        }

        var reorderChange = changes.FirstOrDefault(c => c.Action == "reorder");
        List<string>? newOrder = reorderChange?.Order;

        var newParamProv = new List<(string name, string type, string? defaultValue, int oldIndex)>();

        if (newOrder != null)
        {
            foreach (var paramName in newOrder)
            {
                var oldIndex = currentParams.FindIndex(p => p.name == paramName);
                if (oldIndex >= 0 && !removedParams.Contains(paramName))
                {
                    var finalName = renamedParams.TryGetValue(paramName, out var renamed) ? renamed : paramName;
                    var p = currentParams[oldIndex];
                    newParamProv.Add((finalName, p.type, p.defaultValue, oldIndex));
                }
            }
        }
        else
        {
            for (var i = 0; i < currentParams.Count; i++)
            {
                var p = currentParams[i];
                if (removedParams.Contains(p.name)) continue;
                var finalName = renamedParams.TryGetValue(p.name, out var renamed) ? renamed : p.name;
                newParamProv.Add((finalName, p.type, p.defaultValue, i));
            }
        }

        foreach (var change in changes.Where(c => c.Action == "add"))
        {
            if (change.Name == null || change.Type == null) continue;
            var newParam = (change.Name, change.Type, change.DefaultValue, -1);
            var pos = change.Position ?? -1;
            if (pos < 0 || pos >= newParamProv.Count) newParamProv.Add(newParam);
            else newParamProv.Insert(pos, newParam);
        }

        var newParams = newParamProv.Select(p => (p.name, p.type, p.defaultValue)).ToList();

        var oldSignature = $"{methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {methodSymbol.Name}({string.Join(", ", currentParams.Select(p => $"{p.type} {p.name}"))})";
        var newParamStrings = newParams.Select(p =>
            p.defaultValue != null ? $"{p.type} {p.name} = {p.defaultValue}" : $"{p.type} {p.name}");
        var newSignature = $"{methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {methodSymbol.Name}({string.Join(", ", newParamStrings)})";

        // Find every reference (declaration and call sites).
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, _solution!);
        var callSites = new List<object>();
        var filesAffected = new HashSet<string> { filePath };

        foreach (var refGroup in references)
        {
            foreach (var location in refGroup.Locations)
            {
                var refFilePath = location.Document.FilePath;
                if (refFilePath == null) continue;
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

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    methodName = methodSymbol.Name,
                    containingType = methodSymbol.ContainingType?.ToDisplayString(),
                    oldSignature,
                    newSignature,
                    oldParameters = currentParams,
                    newParameters = newParams.Select(p => new { p.name, p.type, p.defaultValue }),
                    callSitesCount = callSites.Count,
                    callSites = callSites.Take(20).ToList(),
                    hasMoreCallSites = callSites.Count > 20,
                    filesAffected = filesAffected.Select(FormatPath).ToList()
                },
                suggestedNextTools: new[]
                {
                    "Call again with preview: false to apply changes",
                    "Review the call sites before applying"
                }
            );
        }

        // ---- preview == false: actually rewrite declaration + every call site. ----

        var newParamListSyntax = BuildParameterList(newParams);

        var editor = new Microsoft.CodeAnalysis.Editing.SolutionEditor(_solution!);
        var declEditor = await editor.GetDocumentEditorAsync(document.Id);
        declEditor.ReplaceNode(declNode, ReplaceDeclarationParameterList(declNode, newParamListSyntax));

        var oldParameterSymbols = methodSymbol.Parameters;
        var renamedByOldName = renamedParams;

        var callSitesUpdated = 0;
        foreach (var refGroup in references)
        {
            foreach (var location in refGroup.Locations)
            {
                var refDoc = _solution!.GetDocument(location.Document.Id);
                if (refDoc == null) continue;

                var refRoot = await refDoc.GetSyntaxRootAsync();
                if (refRoot == null) continue;

                var refNode = refRoot.FindNode(location.Location.SourceSpan);
                var (argList, replaceWithCallSite) = ResolveCallSiteArgumentList(refNode);
                if (argList == null) continue; // nameof / cref / unsupported shape

                var newArgList = TransformArgumentList(
                    argList,
                    oldParameterSymbols,
                    newParamProv,
                    renamedByOldName);

                var docEditor = await editor.GetDocumentEditorAsync(refDoc.Id);
                docEditor.ReplaceNode(argList, newArgList);
                callSitesUpdated++;
            }
        }

        var newSolution = editor.GetChangedSolution();
        if (!_workspace!.TryApplyChanges(newSolution))
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Workspace rejected the signature change",
                hint: "Check for unsaved files or workspace state issues",
                context: new { methodName = methodSymbol.Name }
            );
        }

        _solution = _workspace.CurrentSolution;
        _documentCache.Clear();
        _compilationCache.Clear();

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName = methodSymbol.Name,
                containingType = methodSymbol.ContainingType?.ToDisplayString(),
                oldSignature,
                newSignature,
                callSitesUpdated,
                filesModified = filesAffected.Select(FormatPath).ToList()
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics to verify no errors after the rewrite",
                "find_references to verify all call sites were updated"
            }
        );
    }

    // Format a constant value the way it would appear as a default-value expression in source.
    private static string FormatDefaultValueLiteral(object? value, ITypeSymbol type)
    {
        if (value == null)
        {
            return type.IsReferenceType || type.NullableAnnotation == NullableAnnotation.Annotated ? "null" : "default";
        }
        return value switch
        {
            string s => "\"" + s.Replace("\"", "\\\"") + "\"",
            bool b => b ? "true" : "false",
            char c => "'" + c + "'",
            _ => value.ToString() ?? "default"
        };
    }

    private static ParameterListSyntax BuildParameterList(List<(string name, string type, string? defaultValue)> newParams)
    {
        var parameters = newParams.Select(p =>
        {
            var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.name))
                .WithType(SyntaxFactory.ParseTypeName(p.type).WithTrailingTrivia(SyntaxFactory.Space));
            if (!string.IsNullOrEmpty(p.defaultValue))
            {
                param = param.WithDefault(
                    SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(p.defaultValue)));
            }
            return param;
        });
        return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters));
    }

    private static SyntaxNode ReplaceDeclarationParameterList(SyntaxNode declNode, ParameterListSyntax newParams)
    {
        return declNode switch
        {
            MethodDeclarationSyntax m => m.WithParameterList(newParams),
            ConstructorDeclarationSyntax c => c.WithParameterList(newParams),
            LocalFunctionStatementSyntax lf => lf.WithParameterList(newParams),
            _ => declNode
        };
    }

    // Resolve a reference-site node to its ArgumentList. Returns null for nameof/cref-style
    // references where there is no argument list to rewrite.
    private static (ArgumentListSyntax? argList, SyntaxNode? callSite) ResolveCallSiteArgumentList(SyntaxNode? refNode)
    {
        if (refNode == null) return (null, null);

        // The reference span lands on the method identifier or `new` keyword; walk up to
        // find whichever call-site shape contains it.
        for (var current = refNode; current != null; current = current.Parent)
        {
            switch (current)
            {
                case InvocationExpressionSyntax inv:
                    // Skip nameof() — there are no arguments to remap.
                    if (inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof")
                        return (null, null);
                    return (inv.ArgumentList, inv);
                case ObjectCreationExpressionSyntax oc:
                    return (oc.ArgumentList, oc);
                case ImplicitObjectCreationExpressionSyntax ioc:
                    return (ioc.ArgumentList, ioc);
                case ConstructorInitializerSyntax ci:
                    return (ci.ArgumentList, ci);
                case AttributeSyntax attr:
                    // Attribute arguments have a different syntax (AttributeArgumentListSyntax)
                    // and aren't supported by this tool yet.
                    return (null, null);
                case CrefSyntax:
                case XmlCrefAttributeSyntax:
                    return (null, null);
            }
        }
        return (null, null);
    }

    // Build a new ArgumentList from the old one based on the new parameter provenance.
    // For each new slot:
    //   - oldIndex >= 0  → take the matching old argument (by name or position).
    //   - oldIndex == -1 → insert the default value as a new positional argument.
    private static ArgumentListSyntax TransformArgumentList(
        ArgumentListSyntax oldArgList,
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> oldParams,
        List<(string name, string type, string? defaultValue, int oldIndex)> newParamProv,
        Dictionary<string, string> renamedByOldName)
    {
        var newArgs = new List<ArgumentSyntax>();

        foreach (var slot in newParamProv)
        {
            if (slot.oldIndex == -1)
            {
                var literal = string.IsNullOrEmpty(slot.defaultValue) ? "default" : slot.defaultValue!;
                newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression(literal)));
                continue;
            }

            var oldParamName = oldParams[slot.oldIndex].Name;
            var oldArg = FindOldArgument(oldArgList, oldParams, slot.oldIndex);

            if (oldArg == null)
            {
                // Caller relied on the original default; if the parameter still has a
                // default, we can skip emitting an argument. Otherwise emit `default`.
                if (slot.defaultValue != null) continue;
                newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression("default")));
                continue;
            }

            // If we renamed this parameter and the caller used a named argument, update the name.
            if (renamedByOldName.TryGetValue(oldParamName, out var newName) && oldArg.NameColon != null)
            {
                oldArg = oldArg.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(newName)));
            }

            newArgs.Add(oldArg.WithoutTrivia());
        }

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs))
            .WithTriviaFrom(oldArgList);
    }

    private static ArgumentSyntax? FindOldArgument(
        ArgumentListSyntax args,
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> oldParams,
        int oldIndex)
    {
        if (oldIndex < 0 || oldIndex >= oldParams.Length) return null;
        var oldName = oldParams[oldIndex].Name;

        // Prefer named-argument match.
        var named = args.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == oldName);
        if (named != null) return named;

        // Fall back to positional. The argument at this index must NOT itself be a named arg
        // (named args can be in any position and shouldn't be remapped positionally).
        if (oldIndex < args.Arguments.Count)
        {
            var positional = args.Arguments[oldIndex];
            if (positional.NameColon == null) return positional;
        }

        return null;
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

        var document = TryFindDocument(filePath);
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

        // Perform data flow analysis. Roslyn's AnalyzeDataFlow(first, last) requires
        // the two statements to be direct siblings in the same statement list; on
        // selections that don't satisfy that (e.g. partial spans inside nested blocks),
        // it throws ArgumentException. Fall back to a single-statement analysis so the
        // tool still produces a useful preview rather than failing the whole call.
        var firstStatement = statements.First();
        var lastStatement = statements.Last();
        DataFlowAnalysis? dataFlow;
        try
        {
            dataFlow = semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);
        }
        catch (ArgumentException)
        {
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

        var signature = $"{accessibility} {returnType} {methodName}({paramString})";

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    methodName,
                    signature,
                    parameters,
                    returnType,
                    returnVariable,
                    returnReason,
                    statementsExtracted = statements.Count,
                    extractedCode = sb.ToString(),
                    replacementCode,
                    location = new { filePath, startLine, endLine }
                },
                suggestedNextTools: new[]
                {
                    "Call again with preview: false to apply the extraction",
                    "validate_code on extractedCode to confirm it compiles"
                }
            );
        }

        // ---- preview == false: actually apply ----
        // Parse the generated method body into a MethodDeclarationSyntax. Roslyn's
        // ParseMemberDeclaration handles the full text including signature + body.
        var newMember = SyntaxFactory.ParseMemberDeclaration(sb.ToString());
        if (newMember is not MethodDeclarationSyntax newMethod)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Generated extracted method did not parse as a MethodDeclarationSyntax",
                context: new { generatedCode = sb.ToString() });
        }

        // Parse the replacement call into a StatementSyntax.
        var replacementStmt = SyntaxFactory.ParseStatement(replacementCode);

        // Rebuild the containing block: drop the selected statements, insert the
        // replacement at the first-statement's index.
        var oldBlock = firstStatement.Parent as BlockSyntax;
        if (oldBlock == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Selected statements are not inside a block",
                context: new { startLine, endLine });
        }
        var statementSet = new HashSet<StatementSyntax>(statements);
        var firstIdx = oldBlock.Statements.IndexOf(firstStatement);
        var newStatements = oldBlock.Statements
            .Where(s => !statementSet.Contains(s))
            .ToList();
        newStatements.Insert(firstIdx, replacementStmt);
        var newBlock = oldBlock.WithStatements(SyntaxFactory.List(newStatements));

        // Apply both edits — replace the block, insert the new method after the
        // containing method — via SolutionEditor (same pattern as ChangeSignatureAsync).
        var editor = new Microsoft.CodeAnalysis.Editing.SolutionEditor(_solution!);
        var docEditor = await editor.GetDocumentEditorAsync(document.Id);
        docEditor.ReplaceNode(oldBlock, newBlock);
        // InsertAfter places newMethod as a sibling immediately after containingMethod
        // within their common parent (the TypeDeclarationSyntax).
        docEditor.InsertAfter(containingMethod, newMethod);

        var newSolution = editor.GetChangedSolution();
        if (!_workspace!.TryApplyChanges(newSolution))
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Workspace rejected the extract_method edit",
                hint: "Check for unsaved files or workspace state issues",
                context: new { filePath, methodName });
        }

        _solution = _workspace.CurrentSolution;
        _documentCache.Clear();
        _compilationCache.Clear();

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName,
                signature,
                parameters,
                returnType,
                returnVariable,
                statementsExtracted = statements.Count,
                filesModified = new[] { FormatPath(filePath) },
                location = new { filePath, startLine, endLine }
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics to verify no new errors",
                $"find_references on {methodName} to confirm the extracted method is called"
            }
        );
    }

}
