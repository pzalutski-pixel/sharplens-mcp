using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

public partial class RoslynService
{
    private static readonly IReadOnlyList<string> RenameVerbosities = new[] { "summary", "compact", "full" };

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

        if (ParameterValidation.Limit("maxFiles", maxFiles, 1000) is { } maxFilesError) return CreateErrorResponse(maxFilesError);
        if (ParameterValidation.Choice("verbosity", verbosity, RenameVerbosities, out var canonicalVerbosity) is { } verbosityError)
            return CreateErrorResponse(verbosityError);
        var maxFilesToShow = maxFiles ?? 20; // Default to 20 files to prevent huge outputs
        var verbosityLevel = canonicalVerbosity ?? "summary"; // Default to summary to prevent token explosions

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName, overloadIndex,
            cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var (symbol, _, error) = await ResolveSymbolAtAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (symbol == null) return error!;

        if (InvalidIdentifierError(newName, "newName") is { } nameError) return nameError;

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

        // Rename through Roslyn's rename engine, keeping its conflict report: a location
        // the new name would break is listed in the preview and refuses the apply.
        var (newSolution, renameConflicts, refusal) = await RenameConflictResolution.RenameAsync(
            _solution!, symbol, newName, new Microsoft.CodeAnalysis.Rename.SymbolRenameOptions(), cancellationToken);
        if (newSolution == null)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Cannot rename '{symbol.Name}' to '{newName}': {refusal}",
                context: new { symbolName = symbol.Name, newName });
        }
        var conflicts = await DescribeRenameConflictsAsync(_solution!, renameConflicts, cancellationToken);

        // The changes per file, exactly as the apply would write them: the minimal edited
        // spans, one entry per file even when several documents share it (one per target
        // framework of a multi-targeted project).
        var changes = new List<object>();
        var (preparedSolution, _) = await PrepareForApplyAsync(newSolution, cancellationToken);
        var fileChanges = (await ComputeFileChangesAsync(preparedSolution ?? newSolution, cancellationToken))
            .Where(f => f.ChangeType == "Modified")
            .ToList();
        var totalFiles = fileChanges.Count;

        // Only include detailed changes for first N files
        foreach (var fileChange in fileChanges.Take(maxFilesToShow))
        {
            var oldText = fileChange.OldText!;
            var textChanges = fileChange.Changes;

            if (verbosityLevel == "summary")
            {
                // Summary: Just file path and count
                changes.Add(new
                {
                    filePath = FormatPath(fileChange.FilePath),
                    changeCount = textChanges.Count
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
                    filePath = FormatPath(fileChange.FilePath),
                    changeCount = textChanges.Count,
                    changes = documentChanges,
                    truncated = textChanges.Count > 20
                });
            }
            else
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
                    filePath = FormatPath(fileChange.FilePath),
                    changeCount = textChanges.Count,
                    changes = documentChanges,
                    truncated = textChanges.Count > 20
                });
            }
        }

        var filesShown = changes.Count;
        var filesHidden = totalFiles - filesShown;
        var notice = address.TypeResolution?.ShadowNotice;

        // If preview mode, just return the changes
        if (preview)
        {
            var verbosityHint = verbosityLevel == "summary"
                ? "Using verbosity='summary' (file paths + counts only). Use verbosity='compact' for locations or verbosity='full' for detailed text changes."
                : verbosityLevel == "compact"
                    ? "Using verbosity='compact' (locations only). Use verbosity='full' to see old/new text for each change."
                    : null;

            var hints = new List<string>();
            if (conflicts.Count > 0)
                hints.Add($"The rename leaves {conflicts.Count} unresolved conflicts (see conflicts); applying it is refused. Choose another name.");
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
                    conflicts,
                    preview = true,
                    applied = false,
                    notice
                },
                suggestedNextTools: hints.ToArray(),
                totalCount: totalFiles,
                returnedCount: filesShown
            );
        }

        if (conflicts.Count > 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Renaming '{symbol.Name}' to '{newName}' leaves {conflicts.Count} unresolved conflicts; nothing was written",
                hint: "Preview the rename to see each conflict, then choose a name that does not collide.",
                context: new { symbolName = symbol.Name, newName, conflicts });
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
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null,
                notice
            },
            suggestedNextTools: new[] { "get_diagnostics to verify no issues after rename" },
            totalCount: totalFiles,
            returnedCount: filesShown
        );
    }

    // The rename engine's conflicts as { filePath, line, column, kind }, each source
    // location once (a multi-targeted file reports a conflict per target framework),
    // ordered by file and position. Spans are in the documents of `renamedFrom`, the
    // solution as it was before the rename.
    private async Task<List<object>> DescribeRenameConflictsAsync(
        Solution renamedFrom, IReadOnlyList<RenameConflict> conflicts, CancellationToken cancellationToken)
    {
        var located = new List<(string path, int line, int column, string kind)>();
        var seen = new HashSet<(string, int, int)>();
        foreach (var conflict in conflicts)
        {
            var document = renamedFrom.GetDocument(conflict.DocumentId);
            if (document?.FilePath == null) continue;
            var text = await document.GetTextAsync(cancellationToken);
            var position = text.Lines.GetLinePosition(conflict.Span.Start);
            if (!seen.Add((FormatPath(document.FilePath), conflict.Span.Start, conflict.Span.Length))) continue;
            located.Add((FormatPath(document.FilePath), position.Line, position.Character, conflict.Kind));
        }
        return located
            .OrderBy(c => c.path, StringComparer.Ordinal)
            .ThenBy(c => c.line)
            .ThenBy(c => c.column)
            .Select(c => (object)new { filePath = c.path, line = c.line, column = c.column, kind = c.kind })
            .ToList();
    }

    // The INVALID_PARAMETER error for a name that cannot be a C# identifier — not a valid
    // identifier spelling, or a reserved keyword — or null when the name is usable.
    private object? InvalidIdentifierError(string? name, string parameter)
    {
        if (name != null && SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None)
            return null;
        return CreateErrorResponse(
            ErrorCodes.InvalidParameter,
            $"'{name}' is not a valid C# identifier",
            hint: "Identifiers start with a letter or underscore, contain only letters, digits, or underscores, and are not reserved keywords.",
            context: new { parameter, value = name });
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

        var (symbol, position, error) = await ResolveSymbolAtAsync(
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

        if (InvalidIdentifierError(interfaceName, "interfaceName") is { } nameError) return nameError;

        // The public instance members an interface can declare: ordinary methods,
        // properties (indexers included) and events.
        var members = typeSymbol.GetMembers()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public)
            .Where(m => m is IMethodSymbol or IPropertySymbol or IEventSymbol)
            .Where(m => !m.IsStatic)
            .Where(m => m is not IMethodSymbol method || method.MethodKind == MethodKind.Ordinary)
            .ToList();

        // A requested name that is not one of those members is an error, never silently
        // dropped from the interface.
        if (includeMemberNames != null && includeMemberNames.Count > 0)
        {
            var unknown = includeMemberNames
                .Where(n => !members.Any(m => m.Name == n))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (unknown.Count > 0)
            {
                return CreateErrorResponse(
                    ErrorCodes.SymbolNotFound,
                    $"Not public instance members of '{typeSymbol.ToDisplayString()}': {string.Join(", ", unknown)}",
                    hint: "includeMemberNames lists public instance methods, properties and events of the type.",
                    context: new { typeName = typeSymbol.ToDisplayString(), unknown });
            }
            members = members.Where(m => includeMemberNames.Contains(m.Name)).ToList();
        }

        // Generate interface code
        var interfaceCode = GenerateInterfaceCode(interfaceName, members, typeSymbol,
            await DocumentLineBreakAsync(position!.Document, cancellationToken));

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
                suggestedFileName = $"{interfaceName}.cs",
                notice = address.TypeResolution?.ShadowNotice
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

        // Types are spelled as they bind inside the type, where the constructor goes.
        string TypeText(ITypeSymbol type) => type.ToMinimalDisplayString(pos.Model, typeDecl.OpenBraceToken.Span.End);

        var fields = typeSymbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => !f.IsStatic && !f.IsConst && !f.IsImplicitlyDeclared)
            .Select(f => new ConstructorMember(
                Name: f.Name,
                Type: TypeText(f.Type),
                IsReadOnly: f.IsReadOnly,
                IsNullable: f.NullableAnnotation == NullableAnnotation.Annotated,
                ParamName: ToCamelCase(f.Name.TrimStart('_'))))
            .ToList();

        // Every property a constructor of the type can assign: one with a setter (of any
        // accessibility, init included) or a get-only auto-property. Indexers, explicit
        // interface implementations and computed properties are not state a constructor
        // initializes by name.
        var autoProperties = typeSymbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.IsImplicitlyDeclared && f.AssociatedSymbol is IPropertySymbol)
            .Select(f => (IPropertySymbol)f.AssociatedSymbol!)
            .ToHashSet(SymbolEqualityComparer.Default);
        var properties = new List<ConstructorMember>();
        if (includeProperties)
        {
            properties = typeSymbol.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => !p.IsStatic && !p.IsIndexer && p.ExplicitInterfaceImplementations.IsEmpty
                    && (p.SetMethod != null || autoProperties.Contains(p)))
                .Select(p => new ConstructorMember(
                    Name: p.Name,
                    Type: TypeText(p.Type),
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
                    ? "The type has no instance fields or assignable properties"
                    : "The type has no instance fields. Try includeProperties: true",
                context: new { typeName = typeSymbol.Name }
            );
        }

        // Build the constructor as syntax: identifiers and types flow through the
        // generator (keyword escaping included). A member whose name a parameter also
        // has is assigned through `this.`, or the assignment would assign the
        // parameter to itself.
        var generator = Microsoft.CodeAnalysis.Editing.SyntaxGenerator.GetGenerator(pos.Document);
        var parameterNames = allMembers.Select(m => m.ParamName).ToHashSet(StringComparer.Ordinal);
        ExpressionSyntax Target(ConstructorMember m) => parameterNames.Contains(m.Name)
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ThisExpression(),
                (SimpleNameSyntax)generator.IdentifierName(m.Name))
            : (ExpressionSyntax)generator.IdentifierName(m.Name);
        var parameterDeclarations = allMembers
            .Select(m => generator.ParameterDeclaration(m.ParamName, SyntaxFactory.ParseTypeName(m.Type)))
            .ToList();
        // The coalesce assignment is built with raw SyntaxFactory: the
        // generator's AssignmentStatement parenthesizes its right side and
        // DefaultExpression spells the type out — `Name = param ?? default;`
        // is the intended shape.
        var assignments = allMembers
            .Select(SyntaxNode (m) => SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    Target(m),
                    initializeToDefault && m.IsNullable
                        ? SyntaxFactory.BinaryExpression(
                            SyntaxKind.CoalesceExpression,
                            (ExpressionSyntax)generator.IdentifierName(m.ParamName),
                            SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression))
                        : (ExpressionSyntax)generator.IdentifierName(m.ParamName))))
            .ToList();
        var constructorDeclaration = generator.ConstructorDeclaration(
            typeSymbol.Name,
            parameterDeclarations,
            typeSymbol.DeclaredAccessibility == Accessibility.Public
                ? Accessibility.Public
                : Accessibility.Internal,
            statements: assignments);
        // A snippet for the caller to paste: laid out fresh, with the document's line breaks.
        var constructorCode = constructorDeclaration
            .NormalizeWhitespace(eol: await DocumentLineBreakAsync(pos.Document, cancellationToken))
            .ToFullString();

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
                parameters = allMembers.Select(m => new { name = m.ParamName, type = m.Type }).ToList(),
                notice = address.TypeResolution?.ShadowNotice
            },
            suggestedNextTools: new[]
            {
                "Use Edit/Write to insert the generated constructorCode into the class",
                "validate_code on constructorCode to confirm it compiles",
                "sync_documents after editing, then get_diagnostics to check for errors"
            }
        );
    }

    // The line break a snippet generated for the document uses: the document's own
    // style, else "\n".
    private static async Task<string> DocumentLineBreakAsync(Document document, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken);
        return LineEndings.UniformStyle(text) ?? LineEndings.DominantStyle(text) ?? "\n";
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
}
