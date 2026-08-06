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
        string? filePath,
        int? line,
        int? column,
        string newName,
        bool preview,
        int? maxFiles = null,
        string? verbosity = null,
        string? typeName = null,
        string? memberName = null,
        int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var maxFilesToShow = maxFiles ?? 20; // Default to 20 files to prevent huge outputs
        var verbosityLevel = verbosity?.ToLower() ?? "summary"; // Default to summary to prevent token explosions

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName, overloadIndex,
            cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var (symbol, _, error) = await ResolveSymbolAtAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (symbol == null) return error!;

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
            newName,
            cancellationToken);

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

                var oldText = await oldDocument.GetTextAsync(cancellationToken);
                var newText = await newDocument.GetTextAsync(cancellationToken);

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

        // Apply atomically through the unified apply path: on success the
        // workspace, in-memory solution, caches, and disk all agree; on failure
        // nothing changed.
        var (ok, applyError, _, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                newName,
                changes,
                preview = false,
                applied = true,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[] { "get_diagnostics to verify no issues after rename" },
            totalCount: totalFiles,
            returnedCount: filesShown
        );
    }

    public async Task<object> ExtractInterfaceAsync(
        string? filePath, int? line, int? column, string interfaceName, List<string>? includeMemberNames,
        string? typeName = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName: null, overloadIndex: null,
            cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var (symbol, _, error) = await ResolveSymbolAtAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not INamedTypeSymbol typeSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "Not a type symbol",
                hint: "Place cursor on a class or struct to extract an interface",
                context: new { actualKind = symbol.Kind.ToString() }
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
        var interfaceCode = GenerateInterfaceCode(interfaceName, members, typeSymbol);

        // A generic source type extracts to a generic interface; the "add :" hint
        // and the implements clause must name it with its type parameters.
        var interfaceNameWithParams = typeSymbol.TypeParameters.Length > 0
            ? $"{interfaceName}<{string.Join(", ", typeSymbol.TypeParameters.Select(tp => tp.Name))}>"
            : interfaceName;

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
                $"Add : {interfaceNameWithParams} to {typeSymbol.Name} class declaration"
            },
            totalCount: members.Count,
            returnedCount: members.Count
        );
    }

    /// <summary>
    /// Generates a constructor from fields and/or properties of a type.
    /// </summary>
    public async Task<object> GenerateConstructorAsync(
        string? filePath,
        int? line,
        int? column,
        bool includeProperties = false,
        bool initializeToDefault = false,
        string? typeName = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName: null, overloadIndex: null,
            cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var (pos, posError) = await ResolvePositionAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (pos == null) return posError!;

        // Find the type declaration
        var typeDecl = pos.Token.Parent?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (typeDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "No type declaration found at position",
                hint: "Position cursor on a class or struct declaration",
                context: new { line = address.Line, column = address.Column }
            );
        }

        var typeSymbol = pos.Model.GetDeclaredSymbol(typeDecl, cancellationToken) as INamedTypeSymbol;
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

        // Distinct members can camel-case to the same parameter name (a `_value`
        // field and a `Value` property both → `value`); suffix collisions so every
        // parameter name is unique.
        var allMembers = DeduplicateParameterNames(fields.Concat(properties).ToList());

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

        // Build the constructor as syntax, not string concatenation: identifiers
        // and types flow through the generator (keyword escaping included) and
        // NormalizeWhitespace is correct here — fresh generated syntax has no
        // user formatting to preserve.
        var generator = Microsoft.CodeAnalysis.Editing.SyntaxGenerator.GetGenerator(pos.Document);
        var parameterDeclarations = allMembers
            .Select(m => generator.ParameterDeclaration(m.ParamName, SyntaxFactory.ParseTypeName(m.Type)))
            .ToList();
        // The coalesce assignment is built with raw SyntaxFactory: the
        // generator's AssignmentStatement parenthesizes its right side and
        // DefaultExpression spells the type out — `Name = param ?? default;`
        // is the intended shape.
        var assignments = allMembers
            .Select(SyntaxNode (m) => initializeToDefault && m.IsNullable
                ? SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        (ExpressionSyntax)generator.IdentifierName(m.Name),
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.CoalesceExpression,
                            (ExpressionSyntax)generator.IdentifierName(m.ParamName),
                            SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))))
                : generator.AssignmentStatement(
                    generator.IdentifierName(m.Name),
                    generator.IdentifierName(m.ParamName)))
            .ToList();
        var constructorDeclaration = generator.ConstructorDeclaration(
            typeSymbol.Name,
            parameterDeclarations,
            typeSymbol.DeclaredAccessibility == Accessibility.Public
                ? Accessibility.Public
                : Accessibility.Internal,
            statements: assignments);
        var constructorCode = constructorDeclaration.NormalizeWhitespace().ToFullString();

        return CreateSuccessResponse(
            data: new
            {
                // generate_constructor is generation-only by design — it returns the
                // constructor source for the caller to insert via Edit/Write. Flagged
                // here so consumers don't assume the workspace was mutated.
                appliesEditsAutomatically = false,
                typeName = typeSymbol.ToDisplayString(),
                constructorCode,
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

    // Makes every constructor parameter name unique by suffixing an index on
    // collisions (value, value2, value3, …), preserving member order.
    private static List<ConstructorMember> DeduplicateParameterNames(List<ConstructorMember> members)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ConstructorMember>(members.Count);
        foreach (var m in members)
        {
            var name = m.ParamName;
            if (used.Contains(name))
            {
                var i = 2;
                while (used.Contains($"{m.ParamName}{i}")) i++;
                name = $"{m.ParamName}{i}";
            }
            used.Add(name);
            result.Add(name == m.ParamName ? m : m with { ParamName = name });
        }
        return result;
    }

    /// <summary>
    /// Changes a method/constructor/local-function signature and updates all call sites.
    /// Supports add, remove, rename, and reorder parameter operations.
    /// Preview returns the planned diff; preview=false applies edits via SolutionEditor.
    /// </summary>
    public async Task<object> ChangeSignatureAsync(
        string? filePath,
        int? line,
        int? column,
        List<SignatureChange> changes,
        bool preview = true,
        string? typeName = null,
        string? memberName = null,
        int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName, overloadIndex,
            memberNameRequired: true, cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var (resolved, posError) = await ResolvePositionAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (resolved == null) return posError!;
        var document = resolved.Document;

        // Accept any of the three declaration kinds: instance/static method, constructor, local function.
        var declNode = resolved.Token.Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => n is MethodDeclarationSyntax
                              || n is ConstructorDeclarationSyntax
                              || n is LocalFunctionStatementSyntax);

        if (declNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method, constructor, or local function found at position",
                hint: "Position cursor on a method/constructor/local-function declaration",
                context: new { line = address.Line, column = address.Column }
            );
        }

        var methodSymbol = resolved.Model.GetDeclaredSymbol(declNode, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not resolve declaration to an IMethodSymbol",
                context: new { declarationKind = declNode.GetType().Name }
            );
        }

        var currentParams = methodSymbol.Parameters.Select((p, i) => new
        {
            name = p.Name,
            type = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            hasDefault = p.HasExplicitDefaultValue,
            defaultValue = p.HasExplicitDefaultValue ? FormatConstantLiteral(p.Type, p.ExplicitDefaultValue) : null,
            // Modifiers must survive the rewrite or the declaration becomes uncompilable.
            refKind = p.RefKind,
            isParams = p.IsParams,
            isThis = methodSymbol.IsExtensionMethod && i == 0
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

        if (newOrder != null)
        {
            var keptNames = currentParams.Where(p => !removedParams.Contains(p.name)).Select(p => p.name).ToList();
            var orderSet = new HashSet<string>(newOrder);
            var missing = keptNames.Where(n => !orderSet.Contains(n)).ToList();
            if (missing.Count > 0)
            {
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"reorder must list every kept parameter; missing: {string.Join(", ", missing)}",
                    hint: "List all parameters that are not being removed in the order array.",
                    context: new { order = newOrder, missing });
            }
            var unknown = newOrder.Where(n => !currentParams.Any(p => p.name == n)).ToList();
            if (unknown.Count > 0)
            {
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"reorder references unknown parameters: {string.Join(", ", unknown)}",
                    hint: "The order array may only name existing parameters.",
                    context: new { order = newOrder, unknown });
            }
        }

        var newParamProv = new List<(string name, string type, string? defaultValue, int oldIndex, RefKind refKind, bool isParams, bool isThis)>();

        if (newOrder != null)
        {
            foreach (var paramName in newOrder)
            {
                var oldIndex = currentParams.FindIndex(p => p.name == paramName);
                if (oldIndex >= 0 && !removedParams.Contains(paramName))
                {
                    var finalName = renamedParams.TryGetValue(paramName, out var renamed) ? renamed : paramName;
                    var p = currentParams[oldIndex];
                    newParamProv.Add((finalName, p.type, p.defaultValue, oldIndex, p.refKind, p.isParams, p.isThis));
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
                newParamProv.Add((finalName, p.type, p.defaultValue, i, p.refKind, p.isParams, p.isThis));
            }
        }

        foreach (var change in changes.Where(c => c.Action == "add"))
        {
            if (change.Name == null || change.Type == null) continue;
            var newParam = (change.Name, change.Type, change.DefaultValue, -1, RefKind.None, false, false);
            if (change.Position is { } requestedPos)
            {
                if (requestedPos < 0 || requestedPos > newParamProv.Count)
                {
                    return CreateErrorResponse(
                        ErrorCodes.InvalidParameter,
                        $"add Position {requestedPos} is out of range (valid: 0 to {newParamProv.Count})",
                        hint: "Omit Position to append the parameter at the end.",
                        context: new { position = requestedPos, parameterCount = newParamProv.Count });
                }
                newParamProv.Insert(requestedPos, newParam);
            }
            else
            {
                newParamProv.Add(newParam);
            }
        }

        // A required (no-default) parameter after an optional (defaulted) one does
        // not compile (CS1737). A `params` tail is exempt: it has no default yet legally
        // follows optional parameters. Reject any other such order rather than emit it.
        var seenOptional = false;
        foreach (var p in newParamProv)
        {
            if (p.defaultValue != null) { seenOptional = true; continue; }
            if (seenOptional && !p.isParams)
            {
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"Parameter '{p.name}' has no default value but would follow a parameter that does; required parameters must precede optional ones (CS1737).",
                    hint: "Order the change so every defaulted parameter comes last, or give this parameter a default.",
                    context: new { parameter = p.name });
            }
        }

        // Position constraints C# enforces on parameter order: a `params` array must be
        // the last parameter (CS0231) and an extension-method `this` receiver must be the
        // first (CS1100). A reorder/insert that violates either would emit uncompilable
        // code, so reject it rather than apply it.
        for (var i = 0; i < newParamProv.Count; i++)
        {
            var p = newParamProv[i];
            if (p.isParams && i != newParamProv.Count - 1)
            {
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"Parameter '{p.name}' is a params array but is not last; params must be the final parameter (CS0231).",
                    hint: "Keep the params parameter at the end, or don't place a parameter after it.",
                    context: new { parameter = p.name, position = i });
            }
            if (p.isThis && i != 0)
            {
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"Parameter '{p.name}' has the extension 'this' modifier but is not first; 'this' must be the first parameter (CS1100).",
                    hint: "Keep the extension receiver as the first parameter.",
                    context: new { parameter = p.name, position = i });
            }
        }

        var newParams = newParamProv
            .Select(p => (p.name, p.type, p.defaultValue, p.refKind, p.isParams, p.isThis)).ToList();

        var oldSignature = $"{methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {methodSymbol.Name}({string.Join(", ", currentParams.Select(p => RenderParameter(p.refKind, p.isParams, p.isThis, p.type, p.name, p.defaultValue)))})";
        var newSignature = $"{methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {methodSymbol.Name}({string.Join(", ", newParams.Select(p => RenderParameter(p.refKind, p.isParams, p.isThis, p.type, p.name, p.defaultValue)))})";

        // Find every reference (declaration and call sites).
        var references = await SymbolFinder.FindReferencesAsync(methodSymbol, _solution!, cancellationToken);
        var callSites = new List<object>();
        var filesAffected = new HashSet<string> { address.FilePath };

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
            // Compute (without writing) how each call site's arguments would be
            // rewritten, so the caller can review the exact change before applying.
            var transformedCallSites = new List<object>();
            foreach (var refGroup in references)
            {
                foreach (var location in refGroup.Locations)
                {
                    if (transformedCallSites.Count >= 20) break;
                    var refDoc = _solution!.GetDocument(location.Document.Id);
                    if (refDoc == null) continue;
                    var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                    var argList = refRoot == null
                        ? null
                        : ResolveCallSiteArgumentList(refRoot.FindNode(location.Location.SourceSpan));
                    if (argList == null) continue;
                    var reduced = await IsReducedExtensionCallAsync(methodSymbol, refDoc, argList, cancellationToken);
                    var newArgList = TransformArgumentList(argList, methodSymbol.Parameters, newParamProv, renamedParams, reduced);
                    transformedCallSites.Add(new
                    {
                        before = argList.ToString(),
                        after = newArgList.ToString()
                    });
                }
                if (transformedCallSites.Count >= 20) break;
            }

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
                    transformedCallSites,
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
        var declEditor = await editor.GetDocumentEditorAsync(document.Id, cancellationToken);
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

                var refRoot = await refDoc.GetSyntaxRootAsync(cancellationToken);
                if (refRoot == null) continue;

                var refNode = refRoot.FindNode(location.Location.SourceSpan);
                var argList = ResolveCallSiteArgumentList(refNode);
                if (argList == null) continue; // nameof / cref / unsupported shape

                var reduced = await IsReducedExtensionCallAsync(methodSymbol, refDoc, argList, cancellationToken);
                var newArgList = TransformArgumentList(
                    argList,
                    oldParameterSymbols,
                    newParamProv,
                    renamedByOldName,
                    reduced);

                var docEditor = await editor.GetDocumentEditorAsync(refDoc.Id, cancellationToken);
                docEditor.ReplaceNode(argList, newArgList);
                callSitesUpdated++;
            }
        }

        var newSolution = editor.GetChangedSolution();
        var (ok, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName = methodSymbol.Name,
                containingType = methodSymbol.ContainingType?.ToDisplayString(),
                oldSignature,
                newSignature,
                callSitesUpdated,
                changedFiles = FormatAppliedChanges(applyReport),
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics to verify no errors after the rewrite",
                "find_references to verify all call sites were updated"
            }
        );
    }

    // Format a constant value the way it would appear as a default-value expression in source.
    private static ParameterListSyntax BuildParameterList(
        List<(string name, string type, string? defaultValue, RefKind refKind, bool isParams, bool isThis)> newParams)
    {
        var parameters = newParams.Select(p =>
        {
            var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.name))
                .WithType(SyntaxFactory.ParseTypeName(p.type).WithTrailingTrivia(SyntaxFactory.Space));
            var modifiers = ParameterModifierTokens(p.refKind, p.isParams, p.isThis);
            if (modifiers.Count > 0) param = param.WithModifiers(SyntaxFactory.TokenList(modifiers));
            if (!string.IsNullOrEmpty(p.defaultValue))
            {
                param = param.WithDefault(
                    SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(p.defaultValue)));
            }
            return param;
        });
        return SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters));
    }

    // Parameter-modifier tokens in valid C# order: `this`, then ref/out/in, then
    // `params`. Each carries a trailing space so it renders before the type. Dropping
    // these would make the rewritten declaration uncompilable.
    private static List<SyntaxToken> ParameterModifierTokens(RefKind refKind, bool isParams, bool isThis)
    {
        var tokens = new List<SyntaxToken>();
        void Add(SyntaxKind kind) => tokens.Add(SyntaxFactory.Token(kind).WithTrailingTrivia(SyntaxFactory.Space));
        if (isThis) Add(SyntaxKind.ThisKeyword);
        switch (refKind)
        {
            case RefKind.Ref: Add(SyntaxKind.RefKeyword); break;
            case RefKind.Out: Add(SyntaxKind.OutKeyword); break;
            case RefKind.In: Add(SyntaxKind.InKeyword); break;
            case RefKind.RefReadOnlyParameter: Add(SyntaxKind.RefKeyword); Add(SyntaxKind.ReadOnlyKeyword); break;
        }
        if (isParams) Add(SyntaxKind.ParamsKeyword);
        return tokens;
    }

    // Renders a parameter (with its modifiers) for the human-readable signature preview.
    private static string RenderParameter(
        RefKind refKind, bool isParams, bool isThis, string type, string name, string? defaultValue)
    {
        var prefix = "";
        if (isThis) prefix += "this ";
        prefix += refKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            RefKind.RefReadOnlyParameter => "ref readonly ",
            _ => ""
        };
        if (isParams) prefix += "params ";
        var suffix = defaultValue != null ? $" = {defaultValue}" : "";
        return $"{prefix}{type} {name}{suffix}";
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
    private static ArgumentListSyntax? ResolveCallSiteArgumentList(SyntaxNode? refNode)
    {
        if (refNode == null) return null;

        // The reference span lands on the method identifier or `new` keyword; walk up to
        // find whichever call-site shape contains it.
        for (var current = refNode; current != null; current = current.Parent)
        {
            switch (current)
            {
                case InvocationExpressionSyntax inv:
                    // Skip nameof() — there are no arguments to remap.
                    if (inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof")
                        return null;
                    return inv.ArgumentList;
                case ObjectCreationExpressionSyntax oc:
                    return oc.ArgumentList;
                case ImplicitObjectCreationExpressionSyntax ioc:
                    return ioc.ArgumentList;
                case ConstructorInitializerSyntax ci:
                    return ci.ArgumentList;
                case AttributeSyntax:
                    // Attribute arguments have a different syntax (AttributeArgumentListSyntax)
                    // and aren't supported by this tool yet.
                    return null;
                case CrefSyntax:
                case XmlCrefAttributeSyntax:
                    return null;
            }
        }
        return null;
    }

    // Build a new ArgumentList from the old one based on the new parameter provenance.
    // For each new slot:
    //   - oldIndex >= 0  → take the matching old argument (by name or position).
    //   - oldIndex == -1 → insert the default value as a new positional argument.
    private static ArgumentListSyntax TransformArgumentList(
        ArgumentListSyntax oldArgList,
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> oldParams,
        List<(string name, string type, string? defaultValue, int oldIndex, RefKind refKind, bool isParams, bool isThis)> newParamProv,
        Dictionary<string, string> renamedByOldName,
        bool isReducedExtensionCall)
    {
        // A reduced-form extension call (x.Ext(a)) has no argument for the `this`
        // receiver — it is the call receiver — so the call's argument list is offset by
        // one relative to the parameter list. Skip the `this` slot and shift positional
        // argument lookups back by one. A static-form call (Ext(x, a)) carries the
        // receiver as its first argument, so no offset.
        var receiverOffset = isReducedExtensionCall ? 1 : 0;
        var newArgs = new List<ArgumentSyntax>();

        foreach (var slot in newParamProv)
        {
            if (slot.oldIndex == -1)
            {
                var literal = string.IsNullOrEmpty(slot.defaultValue) ? "default" : slot.defaultValue!;
                newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.ParseExpression(literal)));
                continue;
            }

            // The receiver of a reduced-form call is not in the argument list.
            if (isReducedExtensionCall && slot.isThis) continue;

            var oldParamName = oldParams[slot.oldIndex].Name;
            var oldArg = FindOldArgument(oldArgList, oldParams, slot.oldIndex, receiverOffset);

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

        // A params tail lets a call site pass more positional arguments than there
        // are parameters; the params slot above only pulled the first of them.
        // Append the remaining trailing positional arguments verbatim (params must
        // be the last parameter, so its slot is last and these belong at the end)
        // instead of silently dropping them.
        var paramsIndex = oldParams.Length - 1;
        if (paramsIndex >= 0 && oldParams[paramsIndex].IsParams
            && newParamProv.Any(slot => slot.oldIndex == paramsIndex))
        {
            // The params parameter sits at argument index paramsIndex - receiverOffset;
            // surplus positional arguments follow it.
            for (var i = paramsIndex - receiverOffset + 1; i < oldArgList.Arguments.Count; i++)
            {
                var surplus = oldArgList.Arguments[i];
                if (surplus.NameColon == null) newArgs.Add(surplus.WithoutTrivia());
            }
        }

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs))
            .WithTriviaFrom(oldArgList);
    }

    // Whether this call site invokes the extension method in reduced (instance) form —
    // `receiver.Ext(args)` — rather than static form — `Ext(receiver, args)`. Only then
    // is the receiver absent from the argument list.
    private static async Task<bool> IsReducedExtensionCallAsync(
        IMethodSymbol method, Document document, ArgumentListSyntax argList, CancellationToken cancellationToken)
    {
        if (!method.IsExtensionMethod) return false;
        if (argList.Parent is not InvocationExpressionSyntax invocation) return false;
        var model = await document.GetSemanticModelAsync(cancellationToken);
        return model?.GetSymbolInfo(invocation, cancellationToken).Symbol
            is IMethodSymbol { MethodKind: MethodKind.ReducedExtension };
    }

    private static ArgumentSyntax? FindOldArgument(
        ArgumentListSyntax args,
        System.Collections.Immutable.ImmutableArray<IParameterSymbol> oldParams,
        int oldIndex,
        int receiverOffset)
    {
        if (oldIndex < 0 || oldIndex >= oldParams.Length) return null;
        var oldName = oldParams[oldIndex].Name;

        // Prefer named-argument match.
        var named = args.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == oldName);
        if (named != null) return named;

        // Fall back to positional. The parameter at oldIndex maps to the argument at
        // oldIndex - receiverOffset (a reduced-form extension call drops the receiver
        // argument). The argument must NOT itself be a named arg (named args can be in
        // any position and shouldn't be remapped positionally).
        var argIndex = oldIndex - receiverOffset;
        if (argIndex >= 0 && argIndex < args.Arguments.Count)
        {
            var positional = args.Arguments[argIndex];
            if (positional.NameColon == null) return positional;
        }

        return null;
    }

    /// <summary>
    /// Extracts a code block into a new method.
    /// Uses data flow analysis to determine parameters and return value.
    /// </summary>
    /// <remarks>
    /// Intentionally span-only (filePath + startLine/endLine), not name-addressable
    /// like the symbol-targeting refactorings. The unit of work is a statement RANGE,
    /// not a declared symbol; ResolveAddressingAsync resolves a name to a single
    /// declaration position and cannot express a selection span, so name addressing
    /// does not apply here.
    /// </remarks>
    public async Task<object> ExtractMethodAsync(
        string filePath,
        int startLine,
        int endLine,
        string methodName,
        string accessibility = "private",
        bool preview = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // Validate the new method's name and accessibility up front so preview and apply
        // agree: without this, preview reports success on input that produces uncompilable
        // code the apply path then rejects.
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(methodName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{methodName}' is not a valid C# method name",
                hint: "methodName must be a valid C# identifier.",
                context: new { methodName }
            );
        }
        var validAccessibilities = new[]
        {
            "private", "public", "protected", "internal", "protected internal", "private protected"
        };
        if (!validAccessibilities.Contains(accessibility))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{accessibility}' is not a valid accessibility",
                hint: "accessibility must be one of: " + string.Join(", ", validAccessibilities) + ".",
                context: new { accessibility }
            );
        }

        var document = TryFindDocument(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Ensure the file is part of a project in the loaded solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                context: new { filePath }
            );
        }

        var text = await syntaxTree.GetTextAsync(cancellationToken);
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        // Validate the line range up front: indexing text.Lines out of range (or a
        // reversed range) would otherwise throw and surface as an InternalError.
        if (ValidateLineRange(text, startLine, endLine) is { } rangeError) return rangeError;

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

        // Keep only the TOP-LEVEL statements of the selection: a selection that
        // covers an if/foreach also matches the statements inside its body (their
        // parent is the inner block), and emitting those alongside the outer
        // statement duplicated them in the extracted method.
        var targetBlock = statements[0].Parent;
        statements = statements.Where(s => s.Parent == targetBlock).ToList();

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

        var methodSymbol = semanticModel.GetDeclaredSymbol(containingMethod, cancellationToken) as IMethodSymbol;
        // Render parameter/return types at the extraction site so their qualification
        // binds in that scope (ToMinimalDisplayString is position-aware).
        var typeRenderPosition = containingMethod.SpanStart;

        // `return` inside the selection (outside nested lambdas/local functions,
        // whose returns don't leave the extracted method) cannot be represented by
        // this extraction strategy — refuse instead of generating broken flow.
        var inSameFunction = statements
            .SelectMany(s => s.DescendantNodesAndSelf(n =>
                n is not AnonymousFunctionExpressionSyntax && n is not LocalFunctionStatementSyntax))
            .ToList();

        if (inSameFunction.OfType<ReturnStatementSyntax>().Any())
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "The selection contains a return statement",
                hint: "Extract a smaller range that does not include the method's return, or restructure so the returned value is computed by the extracted code and returned by the caller.",
                context: new { startLine, endLine }
            );
        }

        // `await` in the selection means the extracted method must itself be async.
        var containsAwait = inSameFunction.OfType<AwaitExpressionSyntax>().Any();

        // Perform data flow analysis. Roslyn's AnalyzeDataFlow(first, last) requires
        // the two statements to be direct siblings in the same statement list. The
        // targetBlock filter above already guarantees that, so the throw should not
        // occur — but if Roslyn ever rejects the span, falling back to analyzing only
        // the FIRST statement would present wrong parameters/return as a valid preview.
        // Fail honestly instead.
        var firstStatement = statements.First();
        var lastStatement = statements.Last();
        DataFlowAnalysis? dataFlow;
        try
        {
            dataFlow = semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);
        }
        catch (ArgumentException)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze data flow across the selected statements",
                hint: "Select a contiguous run of complete statements at the same block level.",
                context: new { startLine, endLine });
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
                    ILocalSymbol local => local.Type.ToMinimalDisplayString(semanticModel, typeRenderPosition),
                    IParameterSymbol param => param.Type.ToMinimalDisplayString(semanticModel, typeRenderPosition),
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
                returnType = returnSymbol.Type.ToMinimalDisplayString(semanticModel, typeRenderPosition);
                returnVariable = returnSymbol.Name;
                returnReason = $"variable '{returnVariable}' assigned inside and used after selection";
            }
        }
        else if (returnCandidates.Count > 1)
        {
            // Multiple out values - would need out parameters or tuple
            returnType = $"({string.Join(", ", returnCandidates.Cast<ILocalSymbol>().Select(s => $"{s.Type.ToMinimalDisplayString(semanticModel, typeRenderPosition)} {s.Name}"))})";
            returnReason = "multiple variables flow out - consider using tuple or out parameters";
        }

        // Method-level generic type parameters the selection references are declared
        // on the extracted sibling; the enclosing TYPE's parameters stay in scope and
        // are not re-declared.
        var neededTypeParams = CollectReferencedMethodTypeParameters(
            methodSymbol, inSameFunction, dataFlow, returnCandidates, semanticModel, cancellationToken);
        var typeParamList = neededTypeParams.Count > 0
            ? $"<{string.Join(", ", neededTypeParams.Select(tp => tp.Name))}>"
            : "";
        // Explicit type arguments at the call site so a parameter used only in the
        // return type or body (where inference can't reach it) still binds.
        var callTypeArgs = typeParamList;
        var constraintClauses = string.Concat(
            containingMethod.ConstraintClauses
                .Where(c => neededTypeParams.Any(tp => tp.Name == c.Name.Identifier.Text))
                .Select(c => " " + c.NormalizeWhitespace().ToString()));

        // An awaited selection extracts into an async method: the declared return
        // type wraps in Task/Task<T> and the call site awaits it.
        var declaredReturnType = containsAwait
            ? (returnType == "void" ? "Task" : $"Task<{returnType}>")
            : returnType;
        var asyncModifier = containsAwait ? "async " : "";
        var awaitPrefix = containsAwait ? "await " : "";

        // Build the extracted method code
        var sb = new StringBuilder();
        var paramString = string.Join(", ", parameters.Select(p => $"{p.type} {p.name}"));

        sb.AppendLine($"{accessibility} {asyncModifier}{declaredReturnType} {methodName}{typeParamList}({paramString}){constraintClauses}");
        sb.AppendLine("{");

        // Re-base the selected statements to the new method-body indent while
        // preserving their relative nesting: strip the common leading whitespace
        // shared by every non-blank line, then add a single level. A per-line
        // TrimStart would flatten an `if` block's inner statements to the same
        // column as the `if`.
        var bodyLines = string.Concat(statements.Select(s => s.ToFullString()))
            .Replace("\r\n", "\n").Split('\n');
        var commonIndent = bodyLines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Length - l.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        foreach (var line in bodyLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            sb.AppendLine("    " + line[commonIndent..]);
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
        // A flowed-out local declared BEFORE the selection is assigned at the call
        // site, not re-declared — `var x = …` on an existing `x` does not compile
        // (CS0128/CS0136). A local first declared inside the selection is new.
        bool DeclaredBeforeSelection(ISymbol s) => s.DeclaringSyntaxReferences.Any(r =>
            r.SyntaxTree == firstStatement.SyntaxTree && r.Span.Start < firstStatement.SpanStart);

        string replacementCode;

        if (returnType == "void")
        {
            replacementCode = $"{awaitPrefix}{methodName}{callTypeArgs}({callArgs});";
        }
        else if (returnCandidates.Count == 1)
        {
            var target = DeclaredBeforeSelection(returnCandidates[0])
                ? returnVariable
                : $"var {returnVariable}";
            replacementCode = $"{target} = {awaitPrefix}{methodName}{callTypeArgs}({callArgs});";
        }
        else
        {
            // Mixed deconstruction: re-declare only the genuinely new locals.
            var targets = string.Join(", ", returnCandidates.Select(s =>
                DeclaredBeforeSelection(s) ? s.Name : $"var {s.Name}"));
            replacementCode = $"({targets}) = {awaitPrefix}{methodName}{callTypeArgs}({callArgs});";
        }

        var signature = $"{accessibility} {asyncModifier}{declaredReturnType} {methodName}{typeParamList}({paramString}){constraintClauses}";

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
        var docEditor = await editor.GetDocumentEditorAsync(document.Id, cancellationToken);
        docEditor.ReplaceNode(oldBlock, newBlock);
        // InsertAfter places newMethod as a sibling immediately after containingMethod
        // within their common parent (the TypeDeclarationSyntax).
        docEditor.InsertAfter(containingMethod, newMethod);

        var newSolution = editor.GetChangedSolution();
        var (applied, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!applied) return applyError!;

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
                changedFiles = FormatAppliedChanges(applyReport),
                location = new { filePath, startLine, endLine },
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics to verify no new errors",
                $"find_references on {methodName} to confirm the extracted method is called"
            }
        );
    }

    // The containing method's generic type parameters that the selection actually
    // uses — via flowing-in/out variable types or any type mentioned in the body.
    // Returned in declaration order. The enclosing TYPE's parameters are excluded
    // (they stay in scope for the sibling method and must not be re-declared).
    private static List<ITypeParameterSymbol> CollectReferencedMethodTypeParameters(
        IMethodSymbol? methodSymbol,
        List<SyntaxNode> selectionNodes,
        DataFlowAnalysis dataFlow,
        List<ISymbol> returnCandidates,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (methodSymbol == null || methodSymbol.TypeParameters.Length == 0)
            return new List<ITypeParameterSymbol>();

        var methodTps = new HashSet<ITypeParameterSymbol>(methodSymbol.TypeParameters, SymbolEqualityComparer.Default);
        var used = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);

        void Collect(ITypeSymbol? type)
        {
            switch (type)
            {
                case ITypeParameterSymbol tp when methodTps.Contains(tp):
                    used.Add(tp);
                    break;
                case INamedTypeSymbol named:
                    foreach (var arg in named.TypeArguments) Collect(arg);
                    break;
                case IArrayTypeSymbol arr:
                    Collect(arr.ElementType);
                    break;
                case IPointerTypeSymbol ptr:
                    Collect(ptr.PointedAtType);
                    break;
            }
        }

        foreach (var s in dataFlow.DataFlowsIn)
            Collect((s as ILocalSymbol)?.Type ?? (s as IParameterSymbol)?.Type);
        foreach (var s in returnCandidates.OfType<ILocalSymbol>())
            Collect(s.Type);
        foreach (var node in selectionNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Collect(semanticModel.GetTypeInfo(node, cancellationToken).Type);
            if (semanticModel.GetSymbolInfo(node, cancellationToken).Symbol is ITypeParameterSymbol tps
                && methodTps.Contains(tps))
            {
                used.Add(tps);
            }
        }

        return methodSymbol.TypeParameters.Where(used.Contains).ToList();
    }

}
