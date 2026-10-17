using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// add_null_checks and generate_equality_members. Both insert generated syntax marked
// with Formatter.Annotation (the apply formats it with the document's options and line
// breaks) and use only APIs that exist in every target framework of the project the
// document belongs to: ArgumentNullException.ThrowIfNull and System.HashCode where every
// framework has them, an equivalent built from older APIs otherwise.
public partial class RoslynService
{
    /// <summary>
    /// Add null check guard clauses to method parameters.
    /// </summary>
    public async Task<object> AddNullChecksAsync(
        string? filePath,
        int? line,
        int? column,
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

        var (pos, posError) = await ResolvePositionAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (pos == null) return posError!;
        var document = pos.Document;
        var semanticModel = pos.Model;
        var notice = address.TypeResolution?.ShadowNotice;

        // Any parameterized, block-bodied member: method, constructor, operator, or
        // local function.
        var target = GetNullCheckTarget(pos.Token.Parent);
        if (target == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method, constructor, operator, or local function found at this position",
                hint: "Position cursor on a method/constructor/operator/local-function declaration",
                context: new { filePath = FormatPath(document.FilePath), line = address.Line, column = address.Column }
            );
        }

        var (_, parameterList, body, methodName) = target.Value;
        if (body == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Member has no body (possibly expression-bodied)",
                hint: "This tool works with block-bodied members",
                context: new { methodName }
            );
        }

        var alreadyGuarded = GetAlreadyGuardedParameterNames(body);
        var skippedNullable = new List<string>();
        var skippedAlreadyGuarded = new List<string>();
        var skippedOut = new List<string>();

        // Guard candidates: reference-type parameters a caller can pass null to whose
        // contract does not allow it. Skipped: out parameters (assigned by the method,
        // never read on entry); parameters whose contract permits null — annotated
        // nullable (string?), nullable value types (int?), or a default of null; and
        // parameters the body already guards, so a second run doesn't duplicate.
        var guarded = new List<string>();
        foreach (var p in parameterList.Parameters)
        {
            var paramSymbol = semanticModel.GetDeclaredSymbol(p, cancellationToken);
            if (paramSymbol == null) continue;

            if (paramSymbol.RefKind == RefKind.Out)
            {
                skippedOut.Add(paramSymbol.Name);
                continue;
            }

            var type = paramSymbol.Type;
            var isNullableValueType = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            if (!type.IsReferenceType && !isNullableValueType) continue;

            if (paramSymbol.NullableAnnotation == NullableAnnotation.Annotated || isNullableValueType
                || (paramSymbol.HasExplicitDefaultValue && paramSymbol.ExplicitDefaultValue == null))
            {
                skippedNullable.Add(paramSymbol.Name);
                continue;
            }

            if (alreadyGuarded.Contains(p.Identifier.Text))
            {
                skippedAlreadyGuarded.Add(paramSymbol.Name);
                continue;
            }

            guarded.Add(paramSymbol.Name);
        }

        if (guarded.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    message = "No reference-type parameter needs a null check",
                    methodName,
                    skippedNullable,
                    skippedAlreadyGuarded,
                    skippedOut,
                    notice
                }
            );
        }

        // ThrowIfNull where every target framework has it (.NET 6+), an if-null-throw
        // guard otherwise. Each guard is a statement of its own line; the last is
        // followed by a blank line when the body has statements.
        var throwIfNull = await ApiInEveryTargetFrameworkAsync(document, HasThrowIfNull, cancellationToken);
        var guardMarker = new SyntaxAnnotation("SharpLens.NullCheck");
        var guards = guarded.Select((name, i) =>
        {
            var identifier = EscapedName(name).Identifier.Text;
            var statement = SyntaxFactory.ParseStatement(throwIfNull
                ? $"global::System.ArgumentNullException.ThrowIfNull({identifier});"
                : $"if ({identifier} is null) throw new global::System.ArgumentNullException(nameof({identifier}));");
            var trailing = i == guarded.Count - 1 && body.Statements.Count > 0
                ? SyntaxFactory.TriviaList(SyntaxFactory.LineFeed, SyntaxFactory.LineFeed)
                : SyntaxFactory.TriviaList(SyntaxFactory.LineFeed);
            return statement.WithLeadingTrivia().WithTrailingTrivia(trailing)
                .WithAdditionalAnnotations(Formatter.Annotation, guardMarker);
        }).ToList();

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var newRoot = root.ReplaceNode(body, body.WithStatements(body.Statements.InsertRange(0, guards)));
        var newSolution = _solution!.WithDocumentSyntaxRoot(document.Id, newRoot);

        if (preview)
        {
            var generatedCode = await AnnotatedTextAsync(newSolution.GetDocument(document.Id)!, guardMarker, dedent: false, cancellationToken);
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    methodName,
                    parametersWithNullChecks = guarded,
                    skippedNullable,
                    skippedAlreadyGuarded,
                    skippedOut,
                    generatedCode,
                    changes = new[]
                    {
                        new
                        {
                            filePath = FormatPath(document.FilePath),
                            insertAfterLine = body.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition.Line,
                            newCode = generatedCode
                        }
                    },
                    notice
                },
                suggestedNextTools: new[] { "add_null_checks with preview=false to apply" }
            );
        }

        // Apply through the unified path — workspace, in-memory solution, caches,
        // and disk stay consistent; no sync_documents needed afterward.
        var (ok, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName,
                parametersWithNullChecks = guarded,
                skippedNullable,
                skippedAlreadyGuarded,
                skippedOut,
                generatedCode = await AnnotatedTextAsync(newSolution.GetDocument(document.Id)!, guardMarker, dedent: false, cancellationToken),
                changedFiles = FormatAppliedChanges(applyReport),
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null,
                notice
            },
            suggestedNextTools: new[] { "get_diagnostics to verify the guards compile" }
        );
    }

    // Whether every target framework the document compiles for (each project holding a
    // document of its file) passes the probe — an API generated code may use only when
    // it exists in all of them.
    private async Task<bool> ApiInEveryTargetFrameworkAsync(
        Document document, Func<Compilation, bool> probe, CancellationToken cancellationToken)
    {
        var documentIds = document.FilePath == null
            ? (IReadOnlyList<DocumentId>)new[] { document.Id }
            : _solution!.GetDocumentIdsWithFilePath(document.FilePath);
        foreach (var documentId in documentIds)
        {
            var compilation = await _solution!.GetDocument(documentId)!.Project.GetCompilationAsync(cancellationToken);
            if (compilation == null || !probe(compilation)) return false;
        }
        return true;
    }

    private static bool HasThrowIfNull(Compilation compilation) =>
        compilation.GetTypeByMetadataName("System.ArgumentNullException")?.GetMembers("ThrowIfNull")
            .OfType<IMethodSymbol>()
            .Any(m => m.IsStatic && m.DeclaredAccessibility == Accessibility.Public
                      && m.Parameters.Length > 0 && m.Parameters[0].Type.SpecialType == SpecialType.System_Object) == true;

    private static bool HasHashCode(Compilation compilation) =>
        compilation.GetTypeByMetadataName("System.HashCode") is { DeclaredAccessibility: Accessibility.Public };

    // The text of the nodes carrying the annotation, as the apply's formatting will lay
    // them out: from the first node's start to the last node's end, in the document's
    // line-break style; dedented to the first node's column when asked.
    private static async Task<string> AnnotatedTextAsync(
        Document document, SyntaxAnnotation annotation, bool dedent, CancellationToken cancellationToken)
    {
        var original = await document.GetTextAsync(cancellationToken);
        var formatted = await Formatter.FormatAsync(document, Formatter.Annotation, cancellationToken: cancellationToken);
        var root = (await formatted.GetSyntaxRootAsync(cancellationToken))!;
        var text = await formatted.GetTextAsync(cancellationToken);
        var nodes = root.GetAnnotatedNodes(annotation).ToList();
        var span = TextSpan.FromBounds(nodes[0].SpanStart, nodes[^1].Span.End);
        var lineBreak = LineEndings.UniformStyle(original) ?? LineEndings.DominantStyle(original) ?? "\n";
        return dedent
            ? Dedent(text, span, lineBreak)
            : LineEndings.Convert(text.ToString(span), lineBreak);
    }

    // The nearest parameterized member at a position whose parameters can be
    // null-guarded: a method, constructor, operator/conversion, or local function.
    // Returns null for anything else. Body may be null (expression-bodied), which
    // the caller reports separately.
    private static (SyntaxNode node, ParameterListSyntax parameters, BlockSyntax? body, string name)?
        GetNullCheckTarget(SyntaxNode? start)
    {
        for (var node = start; node != null; node = node.Parent)
        {
            switch (node)
            {
                case MethodDeclarationSyntax m: return (m, m.ParameterList, m.Body, m.Identifier.Text);
                case ConstructorDeclarationSyntax c: return (c, c.ParameterList, c.Body, c.Identifier.Text);
                case LocalFunctionStatementSyntax lf: return (lf, lf.ParameterList, lf.Body, lf.Identifier.Text);
                case OperatorDeclarationSyntax op: return (op, op.ParameterList, op.Body, $"operator {op.OperatorToken.Text}");
                case ConversionOperatorDeclarationSyntax cv: return (cv, cv.ParameterList, cv.Body, "conversion operator");
            }
        }
        return null;
    }

    // Parameter names the method body already null-guards, via either pattern:
    //   ArgumentNullException.ThrowIfNull(name);
    //   if (name == null) throw ...;   /  if (name is null) throw ...;
    private static HashSet<string> GetAlreadyGuardedParameterNames(BlockSyntax body)
    {
        var guarded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var statement in body.Statements)
        {
            // Throw-helpers: ArgumentNullException.ThrowIfNull and
            // ArgumentException.ThrowIfNullOrEmpty/ThrowIfNullOrWhiteSpace.
            if (statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation }
                && InvokedSimpleName(invocation) is "ThrowIfNull" or "ThrowIfNullOrEmpty" or "ThrowIfNullOrWhiteSpace"
                && invocation.ArgumentList.Arguments.Count > 0)
            {
                guarded.Add(invocation.ArgumentList.Arguments[0].Expression.ToString());
                continue;
            }

            if (statement is IfStatementSyntax ifStatement && ContainsThrow(ifStatement.Statement))
            {
                var name = NullComparedIdentifier(ifStatement.Condition);
                if (name != null) guarded.Add(name);
            }
        }

        return guarded;
    }

    // The simple (rightmost) name of an invoked method, e.g. "ThrowIfNull" from
    // ArgumentNullException.ThrowIfNull or "IsNullOrEmpty" from string.IsNullOrEmpty.
    private static string? InvokedSimpleName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => null
    };

    private static bool ContainsThrow(StatementSyntax statement) => statement switch
    {
        ThrowStatementSyntax => true,
        BlockSyntax block => block.Statements.Any(s => s is ThrowStatementSyntax),
        _ => false
    };

    // Matches the null-guard conditions whose throwing body protects an argument:
    // `x == null` / `null == x`, `x is null`, `x is not { }`, `IsNullOrEmpty(x)` /
    // `IsNullOrWhiteSpace(x)`, and either operand of a compound `||` (the throw fires
    // whenever that operand is null). A compound `&&` does NOT count: `x == null && cond`
    // only throws when the OTHER operand also holds, so x is not actually guarded.
    private static string? NullComparedIdentifier(ExpressionSyntax condition) => condition switch
    {
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression) =>
            binary.Left is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }
                ? GuardedName(binary.Right)
                : binary.Right is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }
                    ? GuardedName(binary.Left)
                    : null,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression) =>
            NullComparedIdentifier(binary.Left) ?? NullComparedIdentifier(binary.Right),
        InvocationExpressionSyntax invocation when
            InvokedSimpleName(invocation) is "IsNullOrEmpty" or "IsNullOrWhiteSpace"
            && invocation.ArgumentList.Arguments.Count > 0 =>
            GuardedName(invocation.ArgumentList.Arguments[0].Expression),
        // x is null
        IsPatternExpressionSyntax isPattern when
            isPattern.Pattern is ConstantPatternSyntax { Expression: LiteralExpressionSyntax nullLiteral }
            && nullLiteral.IsKind(SyntaxKind.NullLiteralExpression) =>
            GuardedName(isPattern.Expression),
        // x is not { } — i.e. x is null (UnaryPatternSyntax is always a `not` pattern)
        IsPatternExpressionSyntax { Pattern: UnaryPatternSyntax { Pattern: RecursivePatternSyntax } } isPattern =>
            GuardedName(isPattern.Expression),
        _ => null
    };

    // The guarded name from a null-check operand. Only a bare parameter reference
    // counts: a `this.x`-qualified guard checks the FIELD x, not the parameter, so
    // treating it as a parameter guard would wrongly skip guarding the parameter.
    private static string? GuardedName(ExpressionSyntax? expr) =>
        (expr as IdentifierNameSyntax)?.Identifier.Text;

    // The user-declared instance field a manual property's accessors read/write, or
    // null when the accessors don't reference one (computed property). Heuristic: the
    // first non-implicit instance field of the same type referenced in get/set.
    private async Task<IFieldSymbol?> BackingFieldOfAsync(
        IPropertySymbol property, CancellationToken cancellationToken)
    {
        foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
        {
            if (accessor == null) continue;
            foreach (var declRef in accessor.DeclaringSyntaxReferences)
            {
                var syntax = await declRef.GetSyntaxAsync(cancellationToken);
                var document = _solution!.GetDocument(syntax.SyntaxTree);
                if (document == null) continue;
                var model = await document.GetSemanticModelAsync(cancellationToken);
                if (model == null) continue;
                foreach (var identifier in syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is IFieldSymbol field
                        && !field.IsImplicitlyDeclared && !field.IsStatic
                        && SymbolEqualityComparer.Default.Equals(field.ContainingType, property.ContainingType))
                    {
                        return field;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Generate Equals, GetHashCode, and operators for a type.
    /// </summary>
    public async Task<object> GenerateEqualityMembersAsync(
        string? filePath,
        int? line,
        int? column,
        bool includeOperators = true,
        bool preview = true,
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
        var document = pos.Document;
        var notice = address.TypeResolution?.ShadowNotice;

        // Find the type declaration
        var typeNode = pos.Token.Parent?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (typeNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "No type declaration found at this position",
                hint: "Position cursor on a class or struct declaration",
                context: new { filePath = FormatPath(document.FilePath), line = address.Line, column = address.Column }
            );
        }

        var typeSymbol = pos.Model.GetDeclaredSymbol(typeNode, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get type symbol",
                context: new { typeName = typeNode.Identifier.Text }
            );
        }

        if (typeSymbol.IsRecord)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{typeSymbol.Name}' is a record — records already implement value equality (Equals, GetHashCode, ==, !=); generating them duplicates compiler-synthesized members.",
                hint: "Rely on the record's built-in equality, or drop the record keyword to hand-write it.",
                context: new { typeName = typeSymbol.Name });
        }

        // Refuse if the type already hand-declares value-equality members —
        // regenerating Equals/GetHashCode/operator== would duplicate them (CS0111).
        var hasUserEquality = typeSymbol.GetMembers().Any(m =>
            m.Locations.Any(l => l.IsInSource) && m switch
            {
                IMethodSymbol { Name: "Equals", IsOverride: true, Parameters.Length: 1 } em
                    => em.Parameters[0].Type.SpecialType == SpecialType.System_Object,
                // A strongly-typed Equals(T) where T is the containing type — an
                // IEquatable<T> implementation, a new-hidden Equals, or an own overload.
                // The generator emits one too, so regenerating would duplicate it (CS0111).
                IMethodSymbol { Name: "Equals", Parameters.Length: 1 } te
                    when SymbolEqualityComparer.Default.Equals(te.Parameters[0].Type, typeSymbol) => true,
                IMethodSymbol { Name: "GetHashCode", IsOverride: true, Parameters.IsEmpty: true } => true,
                IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator, Name: "op_Equality" } => true,
                _ => false
            });
        if (hasUserEquality)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"'{typeSymbol.Name}' already declares value-equality members (Equals/GetHashCode/operator==); generating them would duplicate the existing ones.",
                hint: "Remove the existing equality members first, or edit them by hand.",
                context: new { typeName = typeSymbol.Name });
        }

        var targetTypeName = typeSymbol.Name;
        // Include type parameters so a generic type's Equals/`is` pattern names the
        // constructed type (Box<T>, not Box, which would not compile).
        var declaredTypeName = typeSymbol.TypeParameters.Length > 0
            ? $"{typeSymbol.Name}<{string.Join(", ", typeSymbol.TypeParameters.Select(tp => tp.Name))}>"
            : typeSymbol.Name;

        // Chain base.Equals only when a non-object/non-ValueType base actually
        // overrides Equals (has value semantics); chaining a reference-equality base
        // would break the generated value equality.
        var valueBase = typeSymbol.BaseType is { } bt
            && bt.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType)
            ? typeSymbol.BaseType
            : null;
        var chainBase = valueBase != null && DeclaresEqualsOverride(valueBase);
        // Fold base.GetHashCode() into the hash ONLY when the base also overrides
        // GetHashCode. A base that overrides Equals but not GetHashCode has an
        // identity-based hash inconsistent with its own value equality; mixing that
        // into our hash would break the Equals/GetHashCode contract (two base-equal
        // instances could hash differently). Hashing only our members is always safe.
        var chainBaseHash = chainBase && DeclaresGetHashCodeOverride(valueBase!);

        // Equality compares STORED state: explicit instance fields plus properties
        // with a backing store (auto-properties, settable properties). Excluded:
        // compiler-generated backing fields (their <Prop>k__BackingField names are
        // not valid C# and the auto-property already covers that storage), computed
        // get-only properties (they derive from other compared state), indexers (not
        // a single value) and explicit interface implementations (not reachable by
        // name).
        static bool IsNamedProperty(IPropertySymbol p) =>
            !p.IsStatic && !p.IsIndexer && p.ExplicitInterfaceImplementations.IsEmpty;
        var autoPropertyTargets = typeSymbol.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.IsImplicitlyDeclared && f.AssociatedSymbol is IPropertySymbol)
            .Select(f => (IPropertySymbol)f.AssociatedSymbol!)
            .ToHashSet(SymbolEqualityComparer.Default);

        // A manual settable property (X { get => _x; set => _x = value; }) and the user
        // field _x it wraps are the SAME stored state; comparing both double-counts it.
        // The property is the compared member, so exclude the field it reads/writes.
        var backingFields = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var prop in typeSymbol.GetMembers().OfType<IPropertySymbol>()
            .Where(p => IsNamedProperty(p) && p.GetMethod != null && p.SetMethod != null
                && !autoPropertyTargets.Contains(p)))
        {
            if (await BackingFieldOfAsync(prop, cancellationToken) is { } backing)
                backingFields.Add(backing);
        }

        var members = typeSymbol.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m =>
                (m is IFieldSymbol field && !field.IsStatic && !field.IsConst
                    && !backingFields.Contains(field)) ||
                (m is IPropertySymbol prop && IsNamedProperty(prop) && prop.GetMethod != null
                    && (prop.SetMethod != null || autoPropertyTargets.Contains(prop))))
            .Select(m => (name: m.Name, type: m is IFieldSymbol f ? f.Type : ((IPropertySymbol)m).Type))
            .ToList();

        if (members.Count == 0 && !chainBase)
        {
            return CreateSuccessResponse(
                data: new
                {
                    message = "No instance fields or properties found to compare",
                    typeName = targetTypeName,
                    notice
                }
            );
        }

        // Nullable annotations (object?, T?) only where the type's nullable context
        // enables them; System.HashCode only where every target framework has it.
        var nullableAnnotations = pos.Model.GetNullableContext(typeNode.SpanStart).AnnotationsEnabled();
        var hashCodeAvailable = await ApiInEveryTargetFrameworkAsync(document, HasHashCode, cancellationToken);
        var generator = Microsoft.CodeAnalysis.Editing.SyntaxGenerator.GetGenerator(document);
        var memberMarker = new SyntaxAnnotation("SharpLens.EqualityMember");
        var generatedMembers = BuildEqualityMemberDeclarations(
                generator, declaredTypeName, members, includeOperators, chainBase, chainBaseHash,
                typeSymbol.IsValueType, nullableAnnotations, hashCodeAvailable)
            .Select(m => (MemberDeclarationSyntax)m.WithAdditionalAnnotations(Formatter.Annotation, memberMarker))
            .ToArray();

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var newRoot = root.ReplaceNode(typeNode, typeNode.AddMembers(generatedMembers));
        var newSolution = _solution!.WithDocumentSyntaxRoot(document.Id, newRoot);
        var generatedText = await AnnotatedTextAsync(newSolution.GetDocument(document.Id)!, memberMarker, dedent: true, cancellationToken);
        var membersCompared = members.Select(m => m.name).ToList();

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    typeName = targetTypeName,
                    membersCompared,
                    includeOperators,
                    generatedCode = generatedText,
                    notice
                },
                suggestedNextTools: new[] { "generate_equality_members with preview=false to apply" }
            );
        }

        // Apply through the unified path — workspace, in-memory solution, caches,
        // and disk stay consistent; no sync_documents needed afterward.
        var (ok, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                typeName = targetTypeName,
                membersCompared,
                includeOperators,
                changedFiles = FormatAppliedChanges(applyReport),
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null,
                notice
            },
            suggestedNextTools: new[] { "get_diagnostics to verify the generated members compile" }
        );
    }

    // Whether the type (or one of its bases, excluding object) overrides
    // object.Equals — i.e. participates in value equality.
    private static bool DeclaresEqualsOverride(INamedTypeSymbol type)
    {
        for (var current = type; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (current.GetMembers("Equals").OfType<IMethodSymbol>().Any(m =>
                    m.IsOverride && m.Parameters.Length == 1
                    && m.Parameters[0].Type.SpecialType == SpecialType.System_Object))
            {
                return true;
            }
        }
        return false;
    }

    // Whether the type (or one of its bases, excluding object) overrides
    // object.GetHashCode — used to decide whether base.GetHashCode() is safe to
    // fold into the generated hash (an un-overridden base hash is identity-based).
    private static bool DeclaresGetHashCodeOverride(INamedTypeSymbol type)
    {
        for (var current = type; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            if (current.GetMembers("GetHashCode").OfType<IMethodSymbol>().Any(m =>
                    m.IsOverride && m.Parameters.Length == 0))
            {
                return true;
            }
        }
        return false;
    }

    // Whether == compares two values of the type by value and reflexively: the built-in
    // operators of the integral types, bool, char, decimal and string, and of enums and
    // nullable forms of them. Every other type (a struct without ==, a type parameter,
    // float and double whose NaN is not equal to itself, a reference type whose == is
    // reference identity) is compared with EqualityComparer<T>.Default.
    private static bool ComparesWithEqualityOperator(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type.TypeKind == TypeKind.Enum || type.SpecialType is
            SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_SByte or SpecialType.System_Byte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Decimal or SpecialType.System_String
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr;
    }

    // EqualityComparer<T>.Default for the member's type, fully qualified.
    private static ExpressionSyntax DefaultComparer(ITypeSymbol type) =>
        SyntaxFactory.ParseExpression($"global::System.Collections.Generic.EqualityComparer<{TypeRef(type)}>.Default");

    // The equality members as syntax declarations. Member and type names enter
    // as identifier nodes (keyword escaping handled by the generator), never
    // interpolated strings.
    private static List<SyntaxNode> BuildEqualityMemberDeclarations(
        Microsoft.CodeAnalysis.Editing.SyntaxGenerator generator,
        string typeName,
        List<(string name, ITypeSymbol type)> members,
        bool includeOperators,
        bool chainBase,
        bool chainBaseHash,
        bool isValueType,
        bool nullableAnnotations,
        bool hashCodeAvailable)
    {
        var boolType = generator.TypeExpression(SpecialType.System_Boolean);
        var typeSyntax = SyntaxFactory.ParseTypeName(typeName);
        var nullableTypeSyntax = nullableAnnotations ? SyntaxFactory.ParseTypeName(typeName + "?") : typeSyntax;
        // A struct's strongly-typed Equals takes the value non-nullable (`T`, not
        // `T?` which would be Nullable<T>); the `is null` and ReferenceEquals
        // short-circuits are class-only — both are meaningless on a value type
        // (a struct can't be null, and ReferenceEquals boxes and is always false).
        var typedEqualsParamType = isValueType ? typeSyntax : nullableTypeSyntax;
        var operatorParamType = isValueType ? typeSyntax : nullableTypeSyntax;

        var objectEquals = generator.MethodDeclaration(
            "Equals",
            parameters: new[] { generator.ParameterDeclaration("obj", SyntaxFactory.ParseTypeName(nullableAnnotations ? "object?" : "object")) },
            returnType: boolType,
            accessibility: Accessibility.Public,
            modifiers: Microsoft.CodeAnalysis.Editing.DeclarationModifiers.Override,
            statements: new[]
            {
                // Raw SyntaxFactory: the generator's LogicalAndExpression
                // parenthesizes both operands; the intended shape is
                // `obj is T other && Equals(other)`.
                generator.ReturnStatement(
                    SyntaxFactory.BinaryExpression(
                        SyntaxKind.LogicalAndExpression,
                        SyntaxFactory.IsPatternExpression(
                            SyntaxFactory.IdentifierName("obj"),
                            SyntaxFactory.DeclarationPattern(
                                typeSyntax,
                                SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("other")))),
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.IdentifierName("Equals"),
                            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(SyntaxFactory.IdentifierName("other")))))))
            });

        // Raw SyntaxFactory for the comparison chain: the generator's
        // ValueEquals/LogicalAnd wrap every operand in parentheses; the
        // intended shape is `A == other.A && EqualityComparer<T>.Default.Equals(B, other.B)`.
        ExpressionSyntax Compare(string name, ITypeSymbol type)
        {
            var mine = (ExpressionSyntax)generator.IdentifierName(name);
            var theirs = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("other"),
                (SimpleNameSyntax)generator.IdentifierName(name));
            return ComparesWithEqualityOperator(type)
                ? SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, mine, theirs)
                : SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        DefaultComparer(type), SyntaxFactory.IdentifierName("Equals")),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                    {
                        SyntaxFactory.Argument(mine), SyntaxFactory.Argument(theirs)
                    })));
        }
        ExpressionSyntax? memberComparison = members.Count > 0
            ? members
                .Select(m => Compare(m.name, m.type))
                .Aggregate((left, right) => SyntaxFactory.BinaryExpression(
                    SyntaxKind.LogicalAndExpression, left, right))
            : null;
        // A base with value semantics must also be compared. With no own members
        // (a derived type whose only state is the base), Equals IS base.Equals(other).
        if (chainBase)
        {
            var baseEquals = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.BaseExpression(),
                    SyntaxFactory.IdentifierName("Equals")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("other")))));
            memberComparison = memberComparison != null
                ? SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, memberComparison, baseEquals)
                : baseEquals;
        }
        // Reachable only via the caller's members.Count>0 || chainBase guard.
        var comparison = memberComparison
            ?? (ExpressionSyntax)SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);
        var typedEqualsStatements = isValueType
            ? new[] { generator.ReturnStatement(comparison) }
            : new[]
            {
                generator.IfStatement(
                    SyntaxFactory.IsPatternExpression(
                        SyntaxFactory.IdentifierName("other"),
                        SyntaxFactory.ConstantPattern(
                            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                    new[] { generator.ReturnStatement(generator.FalseLiteralExpression()) }),
                generator.IfStatement(
                    generator.InvocationExpression(
                        generator.IdentifierName("ReferenceEquals"),
                        generator.ThisExpression(), generator.IdentifierName("other")),
                    new[] { generator.ReturnStatement(generator.TrueLiteralExpression()) }),
                generator.ReturnStatement(comparison)
            };
        var typedEquals = generator.MethodDeclaration(
            "Equals",
            parameters: new[] { generator.ParameterDeclaration("other", typedEqualsParamType) },
            returnType: boolType,
            accessibility: Accessibility.Public,
            statements: typedEqualsStatements);

        var getHashCode = generator.MethodDeclaration(
            "GetHashCode",
            returnType: generator.TypeExpression(SpecialType.System_Int32),
            accessibility: Accessibility.Public,
            modifiers: Microsoft.CodeAnalysis.Editing.DeclarationModifiers.Override,
            statements: hashCodeAvailable
                ? HashCodeBuilderStatements(generator, members, chainBaseHash)
                : MultiplicativeHashStatements(members, chainBaseHash));

        var declarations = new List<SyntaxNode> { objectEquals, typedEquals, getHashCode };
        if (!includeOperators) return declarations;

        // Value-type operators compare via the strongly-typed instance Equals
        // (`left.Equals(right)`) — the static object.Equals(left, right) used for
        // classes (null-safe) would box both struct operands.
        ExpressionSyntax EqualsCall() => isValueType
            ? SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("left"),
                    SyntaxFactory.IdentifierName("Equals")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("right")))))
            : SyntaxFactory.InvocationExpression(
                SyntaxFactory.IdentifierName("Equals"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("left")),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("right"))
                })));
        SyntaxNode OperatorDeclaration(Microsoft.CodeAnalysis.Editing.OperatorKind kind, SyntaxNode body) =>
            generator.OperatorDeclaration(
                kind,
                parameters: new[]
                {
                    generator.ParameterDeclaration("left", operatorParamType),
                    generator.ParameterDeclaration("right", operatorParamType)
                },
                returnType: boolType,
                accessibility: Accessibility.Public,
                modifiers: Microsoft.CodeAnalysis.Editing.DeclarationModifiers.Static,
                statements: new[] { generator.ReturnStatement(body) });

        declarations.Add(OperatorDeclaration(
            Microsoft.CodeAnalysis.Editing.OperatorKind.Equality, EqualsCall()));
        declarations.Add(OperatorDeclaration(
            Microsoft.CodeAnalysis.Editing.OperatorKind.Inequality,
            SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, EqualsCall())));
        return declarations;
    }

    // GetHashCode through System.HashCode: every compared member feeds the hash
    // (HashCode.Combine caps at eight arguments, and hashing a subset of what Equals
    // compares breaks the Equals/GetHashCode contract).
    private static List<SyntaxNode> HashCodeBuilderStatements(
        Microsoft.CodeAnalysis.Editing.SyntaxGenerator generator, List<(string name, ITypeSymbol type)> members, bool chainBaseHash)
    {
        var statements = new List<SyntaxNode>
        {
            generator.LocalDeclarationStatement("hash",
                generator.ObjectCreationExpression(SyntaxFactory.ParseTypeName("global::System.HashCode")))
        };
        if (chainBaseHash)
        {
            statements.Add(generator.ExpressionStatement(
                generator.InvocationExpression(
                    generator.MemberAccessExpression(generator.IdentifierName("hash"), "Add"),
                    generator.InvocationExpression(
                        generator.MemberAccessExpression(SyntaxFactory.BaseExpression(), "GetHashCode")))));
        }
        statements.AddRange(members.Select(m => generator.ExpressionStatement(
            generator.InvocationExpression(
                generator.MemberAccessExpression(generator.IdentifierName("hash"), "Add"),
                generator.IdentifierName(m.name)))));
        statements.Add(generator.ReturnStatement(
            generator.InvocationExpression(
                generator.MemberAccessExpression(generator.IdentifierName("hash"), "ToHashCode"))));
        return statements;
    }

    // GetHashCode for frameworks without System.HashCode: the multiplicative combination
    // of EqualityComparer<T>.Default hashes (null-safe), in an unchecked block so it
    // cannot overflow-throw under /checked.
    private static List<SyntaxNode> MultiplicativeHashStatements(List<(string name, ITypeSymbol type)> members, bool chainBaseHash)
    {
        StatementSyntax Line(string statement) =>
            SyntaxFactory.ParseStatement(statement).WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
        var statements = new List<StatementSyntax> { Line("int hash = 17;") };
        if (chainBaseHash) statements.Add(Line("hash = hash * -1521134295 + base.GetHashCode();"));
        statements.AddRange(members.Select(m => Line(
            $"hash = hash * -1521134295 + {DefaultComparer(m.type)}.GetHashCode({EscapedName(m.name).ToFullString()});")));
        statements.Add(Line("return hash;"));
        return new List<SyntaxNode>
        {
            SyntaxFactory.CheckedStatement(SyntaxKind.UncheckedStatement, SyntaxFactory.Block(statements))
        };
    }

    // Renders a type for GENERATED code: fully qualified with a global:: prefix so it
    // resolves with no using directive, whatever the generated file's imports are. Every
    // codegen type reference routes through this so a nested type (Outer.Inner) or a
    // foreign-namespace type can't slip out as an unresolvable simple name (CS0246).
    private static string TypeRef(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
