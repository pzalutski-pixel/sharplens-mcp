using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// File-reorganization refactorings: move a type into its own correctly-named
// file (the remedy tool for find_god_objects' "too many types per file"
// findings) and split selected members of a type into a new partial. Both are
// COMPUTE-ONLY by product rule: they return the full would-be content of both
// files and never touch disk or workspace — the agent applies the result with
// its own Edit/Write tools (under the host's approval flow) and then calls
// sync_documents. New tools must not gain a write path without the user's
// explicit validation.
//
// The new file carries exactly the context its code needs: the namespace
// declarations around it and the using directives it uses — never the origin's
// other types, top-level statements, assembly or module attributes, or global
// usings. Declarations are removed with DeclarationRemoval, so the directives and
// file header around them stay balanced and in place in both files, and a using
// directive the move left unused in the origin is removed too.
public partial class RoslynService
{
    public async Task<object> MoveTypeToFileAsync(
        string typeName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (resolution, error) = await ResolveReorganizationTargetAsync(typeName, cancellationToken);
        if (resolution == null) return error!;
        var (document, root, typeDecl, typeSymbol, typeResolution) = resolution;
        var notice = typeResolution.ShadowNotice;

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
                    message = "The type is already the only type in its file",
                    notice
                });
        }

        var newFileName = $"{typeSymbol.Name}.cs";
        var (newFilePath, collisionError) = ResolveNewFilePath(document, newFileName, typeName);
        if (newFilePath == null) return collisionError!;

        // The new file is the origin minus everything that is not the moved type's
        // context; the origin keeps everything except the moved declaration. A
        // block-scoped namespace that loses all its types is removed rather than left
        // behind as an empty `namespace X { }`.
        var newFileRoot = TrimLeadingBlankLines(RemoveEmptiedNamespaces(
            DeclarationRemoval.Remove(root, siblingTypes.Concat(FileContextNotCarried(root)))));
        var originRoot = (CompilationUnitSyntax)RemoveEmptiedNamespaces(DeclarationRemoval.Remove(root, new[] { typeDecl }));

        var (finalOrigin, finalNewFile) = await WithoutUnusedUsingsAsync(
            document, originRoot, newFilePath, (CompilationUnitSyntax)newFileRoot, cancellationToken);

        return CreateSuccessResponse(
            data: new
            {
                // Compute-only by design — the caller writes both files itself.
                appliesEditsAutomatically = false,
                typeName = typeSymbol.ToDisplayString(),
                newFilePath = FormatPath(newFilePath),
                originFilePath = FormatPath(document.FilePath),
                newFileContent = await InDocumentLineBreaksAsync(document, finalNewFile.ToFullString(), cancellationToken),
                updatedOriginContent = await MatchDocumentLineEndingsAsync(document, finalOrigin.ToFullString(), cancellationToken),
                notice
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
        var (document, root, typeDecl, typeSymbol, typeResolution) = resolution;

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

        // New file: the type's context (as move_type_to_file builds it) holding a
        // partial half of the type with ONLY the moved members (NewPartialHalf). The
        // origin loses the moved members. Both halves get `partial`.
        var notCarried = siblingTypes.Concat(FileContextNotCarried(root)).ToList();
        var trackedNew = root.TrackNodes(movedMembers.Append<SyntaxNode>(typeDecl).Concat(notCarried));
        var newFileRoot = DeclarationRemoval.Remove(trackedNew, notCarried.Select(n => trackedNew.GetCurrentNode(n)!));
        var currentTypeInNew = newFileRoot.GetCurrentNode(typeDecl)!;
        var newHalf = NewPartialHalf(currentTypeInNew, movedMembers.Select(m => newFileRoot.GetCurrentNode(m)!).ToList());
        var newFileUnit = (CompilationUnitSyntax)TrimLeadingBlankLines(RemoveEmptiedNamespaces(newFileRoot.ReplaceNode(currentTypeInNew, newHalf)));

        var trackedOrigin = root.TrackNodes(movedMembers.Append<SyntaxNode>(typeDecl));
        var originRoot = DeclarationRemoval.Remove(trackedOrigin, movedMembers.Select(m => trackedOrigin.GetCurrentNode(m)!));
        var currentTypeInOrigin = originRoot.GetCurrentNode(typeDecl)!;
        originRoot = originRoot.ReplaceNode(currentTypeInOrigin, EnsurePartialModifier(currentTypeInOrigin));

        var (finalOrigin, finalNewFile) = await WithoutUnusedUsingsAsync(
            document, originRoot, newFilePath, newFileUnit, cancellationToken);

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
                newFileContent = await InDocumentLineBreaksAsync(document, finalNewFile.ToFullString(), cancellationToken),
                updatedOriginContent = await MatchDocumentLineEndingsAsync(document, finalOrigin.ToFullString(), cancellationToken),
                notice = typeResolution.ShadowNotice
            },
            suggestedNextTools: new[]
            {
                "Write newFileContent to newFilePath and replace the origin file with updatedOriginContent",
                "sync_documents on both files after writing, then get_diagnostics to verify"
            });
    }

    // The new partial half of a split type: the declaration with only the moved members,
    // no attributes (they stay on the origin half, where repeating them would apply each
    // twice), no primary-constructor parameter list and no base list (only one partial
    // declaration may have the parameter list, and the bases need not repeat). The
    // trivia before the type stays (directives, blank lines, indentation) except its doc
    // comment, which documents the type once, on the origin half.
    private static TypeDeclarationSyntax NewPartialHalf(TypeDeclarationSyntax type, IReadOnlyList<MemberDeclarationSyntax> members)
    {
        var leading = type.GetLeadingTrivia();
        var kept = new List<SyntaxTrivia>();
        for (var i = 0; i < leading.Count; i++)
        {
            var isDocumentation = leading[i].IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || leading[i].IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia);
            if (isDocumentation)
            {
                // The indentation before the doc comment goes with it.
                if (kept.Count > 0 && kept[^1].IsKind(SyntaxKind.WhitespaceTrivia)) kept.RemoveAt(kept.Count - 1);
                continue;
            }
            kept.Add(leading[i]);
        }
        var newLeading = SyntaxFactory.TriviaList(kept);

        // The header's line break (before `{` on the next line) is the trailing trivia of
        // whatever token preceded the brace; it must survive the header's shortening.
        var headerTrailing = type.OpenBraceToken.GetPreviousToken().TrailingTrivia;
        var half = type.WithMembers(SyntaxFactory.List(members))
            .WithAttributeLists(default)
            .WithParameterList(null)
            .WithBaseList(null);
        var lastHeaderToken = half.OpenBraceToken.GetPreviousToken();
        half = half.ReplaceToken(lastHeaderToken, lastHeaderToken.WithTrailingTrivia(headerTrailing));
        return EnsurePartialModifier(half.WithLeadingTrivia(newLeading));
    }

    // What the origin file holds that a file moved out of it must not carry: top-level
    // statements, assembly and module attributes, and global using directives (each
    // already applies to the whole compilation).
    private static IEnumerable<SyntaxNode> FileContextNotCarried(CompilationUnitSyntax root) =>
        root.Members.OfType<GlobalStatementSyntax>().Cast<SyntaxNode>()
            .Concat(root.AttributeLists)
            .Concat(root.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)));

    // A file with no blank lines (or indentation) before its first token.
    private static SyntaxNode TrimLeadingBlankLines(SyntaxNode root)
    {
        var first = root.GetFirstToken(includeZeroWidth: true);
        var trimmed = first.LeadingTrivia.SkipWhile(t => t.IsKind(SyntaxKind.EndOfLineTrivia) || t.IsKind(SyntaxKind.WhitespaceTrivia));
        return root.ReplaceToken(first, first.WithLeadingTrivia(trimmed));
    }

    // The origin without the using directives the reorganization left unused there, and
    // the new file without any using directive it does not use - judged by compiling the
    // two together in place of the origin.
    private async Task<(CompilationUnitSyntax origin, CompilationUnitSyntax newFile)> WithoutUnusedUsingsAsync(
        Document document, CompilationUnitSyntax originRoot, string newFilePath, CompilationUnitSyntax newFileRoot,
        CancellationToken cancellationToken)
    {
        var newDocumentId = DocumentId.CreateNewId(document.Project.Id);
        var forked = document.Project.Solution
            .WithDocumentSyntaxRoot(document.Id, originRoot)
            .AddDocument(DocumentInfo.Create(newDocumentId, Path.GetFileName(newFilePath),
                folders: document.Folders,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(newFileRoot.ToFullString()), VersionStamp.Create())),
                filePath: newFilePath));

        var (origin, _) = await RemoveNewlyUnusedUsingsAsync(document, forked.GetDocument(document.Id)!, cancellationToken);

        var newDocument = forked.GetDocument(newDocumentId)!;
        var newRoot = (CompilationUnitSyntax)(await newDocument.GetSyntaxRootAsync(cancellationToken))!;
        var model = await newDocument.GetSemanticModelAsync(cancellationToken);
        if (model == null || newRoot.Usings.Count == 0) return ((CompilationUnitSyntax)origin, newRoot);
        var unused = model.GetDiagnostics(cancellationToken: cancellationToken)
            .Where(d => d.Id == "CS8019")
            .Select(d => d.Location.SourceSpan)
            .ToList();
        var survivors = newRoot.Usings.Where(u => !unused.Any(span => span.Contains(u.Span) || u.Span.Contains(span))).ToList();
        var finalNew = survivors.Count == newRoot.Usings.Count
            ? newRoot
            : (CompilationUnitSyntax)TrimLeadingBlankLines(WithUsingsPreservingHeader(newRoot, newRoot.Usings[0], survivors));
        return ((CompilationUnitSyntax)origin, finalNew);
    }

    // New content made from a document's text, with every line break in the document's
    // style.
    private static async Task<string> InDocumentLineBreaksAsync(Document document, string content, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken);
        var lineBreak = LineEndings.UniformStyle(text) ?? LineEndings.DominantStyle(text);
        return lineBreak == null ? content : LineEndings.Convert(content, lineBreak);
    }

    private sealed record ReorganizationTarget(
        Document Document,
        CompilationUnitSyntax Root,
        TypeDeclarationSyntax TypeDeclaration,
        INamedTypeSymbol TypeSymbol,
        TypeResolution TypeResolution);

    // Shared target resolution for both reorganization tools: name lookup with
    // ambiguity propagation, then the v1 shape restrictions (single-file,
    // non-nested, source-declared) that both tools require.
    private async Task<(ReorganizationTarget? target, object? error)> ResolveReorganizationTargetAsync(
        string typeName, CancellationToken cancellationToken)
    {
        var typeResolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        if (typeResolution.Error != null) return (null, typeResolution.Error);
        if (typeResolution.Type is not { } typeSymbol)
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
        return (new ReorganizationTarget(document, root, typeDecl, typeSymbol, typeResolution), null);
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
            root = DeclarationRemoval.Remove(root, empty);
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
