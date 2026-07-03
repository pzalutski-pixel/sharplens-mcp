using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// File-reorganization refactorings: move a type into its own correctly-named
// file (the remedy tool for find_god_objects' "too many types per file"
// findings) and split selected members of a type into a new partial. Both are
// COMPUTE-ONLY by product rule: they return the full would-be content of both
// files and never touch disk or workspace — the agent applies the result with
// its own Edit/Write tools (under the host's approval flow) and then calls
// sync_documents. New tools must not gain a write path without the user's
// explicit validation.
public partial class RoslynService
{
    public async Task<object> MoveTypeToFileAsync(
        string typeName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (resolution, error) = await ResolveReorganizationTargetAsync(typeName, cancellationToken);
        if (resolution == null) return error!;
        var (document, root, typeDecl, typeSymbol) = resolution;

        var siblingTypes = TopLevelTypeDeclarations(root)
            .Where(t => t != typeDecl)
            .ToList();

        if (siblingTypes.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    appliesEditsAutomatically = false,
                    alreadyAlone = true,
                    typeName = typeSymbol.ToDisplayString(),
                    filePath = FormatPath(document.FilePath),
                    message = "The type is already the only type in its file"
                });
        }

        var newFileName = $"{typeSymbol.Name}.cs";
        var (newFilePath, collisionError) = ResolveNewFilePath(document, newFileName, typeName);
        if (newFilePath == null) return collisionError!;

        // The new file is the origin minus every OTHER type: usings, namespace
        // shape (file-scoped or block), and the moved type's own text survive
        // verbatim. The origin keeps everything except the moved declaration. A
        // block-scoped namespace that loses all its types is removed rather than
        // left behind as an empty `namespace X { }`.
        var newFileRoot = RemoveEmptiedNamespaces(root.RemoveNodes(siblingTypes, SyntaxRemoveOptions.KeepNoTrivia)!);
        var originRoot = RemoveEmptiedNamespaces(root.RemoveNode(typeDecl, SyntaxRemoveOptions.KeepNoTrivia)!);

        return CreateSuccessResponse(
            data: new
            {
                // Compute-only by design — the caller writes both files itself.
                appliesEditsAutomatically = false,
                typeName = typeSymbol.ToDisplayString(),
                newFilePath = FormatPath(newFilePath),
                originFilePath = FormatPath(document.FilePath),
                newFileContent = newFileRoot.ToFullString(),
                updatedOriginContent = originRoot.ToFullString()
            },
            suggestedNextTools: new[]
            {
                "Write newFileContent to newFilePath and replace the origin file with updatedOriginContent",
                "sync_documents on both files after writing, then get_diagnostics to verify"
            });
    }

    public async Task<object> SplitTypeAsync(
        string typeName,
        List<string> memberNames,
        string? newFileName = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (memberNames == null || memberNames.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "memberNames must list at least one member to move",
                context: new { typeName });
        }

        var (resolution, error) = await ResolveReorganizationTargetAsync(typeName, cancellationToken);
        if (resolution == null) return error!;
        var (document, root, typeDecl, typeSymbol) = resolution;

        if (typeDecl is RecordDeclarationSyntax)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "Records cannot be split: positional members live in the record header",
                context: new { typeName, typeKind = "record" });
        }
        if (typeDecl is not ClassDeclarationSyntax and not StructDeclarationSyntax)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "Only classes and structs can be split",
                context: new { typeName, declarationKind = typeDecl.Kind().ToString() });
        }

        var requested = new HashSet<string>(memberNames, StringComparer.Ordinal);
        var (movedMembers, memberError) = SelectMembersToMove(typeDecl, requested, typeName);
        if (movedMembers == null) return memberError!;

        if (movedMembers.Count == typeDecl.Members.Count)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "memberNames selects every member of the type: use move_type_to_file instead",
                context: new { typeName, memberCount = typeDecl.Members.Count });
        }

        var resolvedFileName = newFileName ?? $"{typeSymbol.Name}.Partial.cs";
        if (resolvedFileName.Contains('/') || resolvedFileName.Contains('\\'))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "newFileName must be a bare file name, not a path",
                context: new { newFileName });
        }
        if (!resolvedFileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            resolvedFileName += ".cs";
        }
        var (newFilePath, collisionError) = ResolveNewFilePath(document, resolvedFileName, typeName);
        if (newFilePath == null) return collisionError!;

        var partialAlreadyPresent = typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword);
        var siblingTypes = TopLevelTypeDeclarations(root).Where(t => t != typeDecl).ToList();

        // New file: drop sibling types, then shrink the target declaration to
        // ONLY the moved members. Attributes stay on the origin half — copying
        // them would apply each attribute twice (CS0579 for non-repeatable
        // ones). The origin loses the moved members. Both halves get `partial`.
        var trackedNew = root.TrackNodes(siblingTypes.Append<SyntaxNode>(typeDecl).Concat(movedMembers));
        var newFileRoot = siblingTypes.Count > 0
            ? trackedNew.RemoveNodes(
                siblingTypes.Select(t => trackedNew.GetCurrentNode(t)!),
                SyntaxRemoveOptions.KeepNoTrivia)!
            : trackedNew;
        var currentTypeInNew = newFileRoot.GetCurrentNode(typeDecl)!;
        var currentMovedInNew = movedMembers
            .Select(m => newFileRoot.GetCurrentNode(m)!)
            .ToList();
        var newHalf = EnsurePartialModifier(
            currentTypeInNew
                .WithMembers(SyntaxFactory.List(currentMovedInNew))
                .WithAttributeLists(default));
        newFileRoot = newFileRoot.ReplaceNode(currentTypeInNew, newHalf);

        var trackedOrigin = root.TrackNodes(movedMembers.Append<SyntaxNode>(typeDecl));
        var originRoot = trackedOrigin.RemoveNodes(
            movedMembers.Select(m => trackedOrigin.GetCurrentNode(m)!),
            SyntaxRemoveOptions.KeepNoTrivia)!;
        var currentTypeInOrigin = originRoot.GetCurrentNode(typeDecl)!;
        originRoot = originRoot.ReplaceNode(currentTypeInOrigin, EnsurePartialModifier(currentTypeInOrigin));

        return CreateSuccessResponse(
            data: new
            {
                // Compute-only by design — the caller writes both files itself.
                appliesEditsAutomatically = false,
                typeName = typeSymbol.ToDisplayString(),
                newFilePath = FormatPath(newFilePath),
                originFilePath = FormatPath(document.FilePath),
                movedMembers = movedMembers.Select(DescribeMemberDeclaration).ToList(),
                partialModifierAdded = !partialAlreadyPresent,
                newFileContent = newFileRoot.ToFullString(),
                updatedOriginContent = originRoot.ToFullString()
            },
            suggestedNextTools: new[]
            {
                "Write newFileContent to newFilePath and replace the origin file with updatedOriginContent",
                "sync_documents on both files after writing, then get_diagnostics to verify"
            });
    }

    private sealed record ReorganizationTarget(
        Document Document,
        CompilationUnitSyntax Root,
        TypeDeclarationSyntax TypeDeclaration,
        INamedTypeSymbol TypeSymbol);

    // Shared target resolution for both reorganization tools: name lookup with
    // ambiguity propagation, then the v1 shape restrictions (single-file,
    // non-nested, source-declared) that both tools require.
    private async Task<(ReorganizationTarget? target, object? error)> ResolveReorganizationTargetAsync(
        string typeName, CancellationToken cancellationToken)
    {
        var (typeSymbol, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return (null, ambiguousType);
        if (typeSymbol == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName }));
        }

        if (typeSymbol.ContainingType != null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{typeSymbol.ToDisplayString()}' is a nested type",
                hint: "Nested types are not supported: move or split the containing type instead.",
                context: new { typeName }));
        }

        var declarationRefs = typeSymbol.DeclaringSyntaxReferences;
        if (declarationRefs.Length == 0)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"'{typeSymbol.ToDisplayString()}' has no source declaration (defined in metadata or generated code)",
                context: new { typeName }));
        }
        if (declarationRefs.Length > 1)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{typeSymbol.ToDisplayString()}' is a partial type declared in {declarationRefs.Length} places",
                hint: "Types with multiple partial declarations are not supported: reorganize the individual files directly.",
                context: new
                {
                    typeName,
                    declarationFiles = declarationRefs
                        .Select(r => FormatPath(r.SyntaxTree.FilePath))
                        .ToList()
                }));
        }

        if (await declarationRefs[0].GetSyntaxAsync(cancellationToken) is not TypeDeclarationSyntax typeDecl)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{typeSymbol.ToDisplayString()}' is not a class, struct, interface, or record declaration",
                context: new { typeName, declarationKind = declarationRefs[0].GetSyntax(cancellationToken).Kind().ToString() }));
        }

        var document = _solution!.GetDocument(typeDecl.SyntaxTree);
        if (document == null || document.FilePath == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"The declaring file of '{typeSymbol.ToDisplayString()}' is not a solution document",
                context: new { typeName }));
        }

        var root = (CompilationUnitSyntax)await typeDecl.SyntaxTree.GetRootAsync(cancellationToken);
        return (new ReorganizationTarget(document, root, typeDecl, typeSymbol), null);
    }

    // Every type declared at namespace or file level in the unit (classes,
    // structs, records, interfaces, enums, and delegates), at any namespace
    // nesting depth — but never types nested inside another type.
    private static List<MemberDeclarationSyntax> TopLevelTypeDeclarations(CompilationUnitSyntax root)
    {
        var result = new List<MemberDeclarationSyntax>();
        void Walk(SyntaxList<MemberDeclarationSyntax> members)
        {
            foreach (var member in members)
            {
                switch (member)
                {
                    case BaseNamespaceDeclarationSyntax ns:
                        Walk(ns.Members);
                        break;
                    case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                        result.Add(member);
                        break;
                }
            }
        }
        Walk(root.Members);
        return result;
    }

    // Removes block-scoped namespaces left with no members after a type move,
    // collapsing nested empties too. File-scoped namespaces are never emptied
    // here (the moved type or the remaining siblings always keep them populated).
    private static SyntaxNode RemoveEmptiedNamespaces(SyntaxNode root)
    {
        while (true)
        {
            var empty = root.DescendantNodes()
                .OfType<NamespaceDeclarationSyntax>()
                .Where(ns => ns.Members.Count == 0)
                .ToList();
            if (empty.Count == 0) return root;
            root = root.RemoveNodes(empty, SyntaxRemoveOptions.KeepNoTrivia) ?? root;
        }
    }

    // Validates the new file's path: same folder as the origin, name not
    // already taken by the origin itself, another solution document, or a
    // stray file on disk.
    private (string? path, object? error) ResolveNewFilePath(
        Document originDocument, string fileName, string typeName)
    {
        var directory = Path.GetDirectoryName(originDocument.FilePath!)!;
        var newFilePath = Path.Combine(directory, fileName);

        if (string.Equals(Path.GetFileName(originDocument.FilePath!), fileName, PathComparison))
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"The origin file is already named '{fileName}'",
                hint: "The file already bears this name: move the OTHER types out of it instead.",
                context: new { typeName, filePath = FormatPath(originDocument.FilePath) }));
        }

        var existingDocument = originDocument.Project.Documents
            .FirstOrDefault(d => d.FilePath != null && string.Equals(d.FilePath, newFilePath, PathComparison));
        if (existingDocument != null || File.Exists(newFilePath))
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"A file named '{fileName}' already exists in the target folder",
                hint: "Merge into the existing file manually, or (split_type) pass a different newFileName.",
                context: new { typeName, conflictingPath = FormatPath(newFilePath) }));
        }

        return (newFilePath, null);
    }

    // Maps requested member names to the declarations that will move. A name
    // matches every declaration bearing it (all overloads move together; the
    // type's simple name matches constructors). Field declarations move only
    // when ALL their declarators are requested — half a declarator list cannot
    // move. Nested types are refused.
    private (List<MemberDeclarationSyntax>? members, object? error) SelectMembersToMove(
        TypeDeclarationSyntax typeDecl, HashSet<string> requested, string typeName)
    {
        var moved = new List<MemberDeclarationSyntax>();
        var matchedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case BaseTypeDeclarationSyntax nested when requested.Contains(nested.Identifier.Text):
                    return (null, CreateErrorResponse(
                        ErrorCodes.InvalidParameter,
                        $"'{nested.Identifier.Text}' is a nested type",
                        hint: "Nested types are not supported: split methods, properties, fields, or events instead.",
                        context: new { typeName, memberName = nested.Identifier.Text }));

                case MethodDeclarationSyntax method when requested.Contains(method.Identifier.Text):
                    moved.Add(member);
                    matchedNames.Add(method.Identifier.Text);
                    break;

                case PropertyDeclarationSyntax property when requested.Contains(property.Identifier.Text):
                    moved.Add(member);
                    matchedNames.Add(property.Identifier.Text);
                    break;

                case EventDeclarationSyntax eventDecl when requested.Contains(eventDecl.Identifier.Text):
                    moved.Add(member);
                    matchedNames.Add(eventDecl.Identifier.Text);
                    break;

                case ConstructorDeclarationSyntax ctor when requested.Contains(ctor.Identifier.Text):
                    moved.Add(member);
                    matchedNames.Add(ctor.Identifier.Text);
                    break;

                case BaseFieldDeclarationSyntax field:
                {
                    var declaratorNames = field.Declaration.Variables
                        .Select(v => v.Identifier.Text)
                        .ToList();
                    var requestedHere = declaratorNames.Where(requested.Contains).ToList();
                    if (requestedHere.Count == 0) break;
                    if (requestedHere.Count < declaratorNames.Count)
                    {
                        return (null, CreateErrorResponse(
                            ErrorCodes.InvalidParameter,
                            $"The declaration of '{requestedHere[0]}' declares multiple variables ({string.Join(", ", declaratorNames)})",
                            hint: "List every variable of the declaration in memberNames, or split the declaration first.",
                            context: new { typeName, declaredTogether = declaratorNames }));
                    }
                    moved.Add(member);
                    foreach (var name in requestedHere) matchedNames.Add(name);
                    break;
                }
            }
        }

        var unmatched = requested.Where(n => !matchedNames.Contains(n)).ToList();
        if (unmatched.Count > 0)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Member(s) not found in '{typeName}': {string.Join(", ", unmatched)}",
                hint: "Use get_type_members to see available members. Operators, indexers, and finalizers cannot be addressed by name.",
                context: new { typeName, unmatched }));
        }

        return (moved, null);
    }

    private static TypeDeclarationSyntax EnsurePartialModifier(TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword)) return typeDecl;

        var partialToken = SyntaxFactory.Token(SyntaxKind.PartialKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        if (typeDecl.Modifiers.Count > 0)
        {
            return typeDecl.WithModifiers(typeDecl.Modifiers.Add(partialToken));
        }

        // No modifiers: the keyword token owns the leading indentation — move
        // it onto the inserted partial token so "partial class" stays aligned.
        var keyword = typeDecl.Keyword;
        return typeDecl
            .WithKeyword(keyword.WithLeadingTrivia())
            .WithModifiers(SyntaxFactory.TokenList(partialToken.WithLeadingTrivia(keyword.LeadingTrivia)));
    }

    private static object DescribeMemberDeclaration(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => new { name = m.Identifier.Text, kind = "Method" },
        PropertyDeclarationSyntax p => new { name = p.Identifier.Text, kind = "Property" },
        EventDeclarationSyntax e => new { name = e.Identifier.Text, kind = "Event" },
        ConstructorDeclarationSyntax c => new { name = c.Identifier.Text, kind = "Constructor" },
        EventFieldDeclarationSyntax ef => new
        {
            name = string.Join(", ", ef.Declaration.Variables.Select(v => v.Identifier.Text)),
            kind = "Event"
        },
        BaseFieldDeclarationSyntax f => new
        {
            name = string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text)),
            kind = "Field"
        },
        _ => new { name = member.Kind().ToString(), kind = "Other" }
    };
}
