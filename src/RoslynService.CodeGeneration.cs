using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SharpLensMcp;

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

        // Any parameterized, block-bodied member: method, constructor, operator, or
        // local function.
        var target = GetNullCheckTarget(pos.Token.Parent);
        if (target == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method, constructor, operator, or local function found at this position",
                hint: "Position cursor on a method/constructor/operator/local-function declaration",
                context: new { filePath = address.FilePath, line = address.Line, column = address.Column }
            );
        }

        var (declarationNode, parameterList, body, methodName) = target.Value;
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

        // Guard candidates: non-nullable reference-type parameters. Parameters
        // annotated nullable (string?) declare that null is a VALID argument —
        // throwing on them would contradict their own contract. Parameters the
        // body already guards are skipped so a second run doesn't duplicate.
        var nullableParams = new List<string>();
        foreach (var p in parameterList.Parameters)
        {
            var paramSymbol = semanticModel.GetDeclaredSymbol(p, cancellationToken);
            if (paramSymbol == null) continue;

            var type = paramSymbol.Type;
            var isNullableValueType = type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            if (!type.IsReferenceType && !isNullableValueType) continue;

            if (paramSymbol.NullableAnnotation == NullableAnnotation.Annotated || isNullableValueType)
            {
                skippedNullable.Add(p.Identifier.Text);
                continue;
            }

            if (alreadyGuarded.Contains(p.Identifier.Text))
            {
                skippedAlreadyGuarded.Add(p.Identifier.Text);
                continue;
            }

            nullableParams.Add(p.Identifier.Text);
        }

        if (nullableParams.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    message = "No nullable parameters found that need null checks",
                    methodName,
                    skippedNullable,
                    skippedAlreadyGuarded
                }
            );
        }

        // Generate null check code, indented one level deeper than the method
        // declaration (derived from its column, not a hardcoded 8 spaces).
        var methodColumn = declarationNode.GetLocation().GetLineSpan().StartLinePosition.Character;
        var indent = new string(' ', methodColumn + 4);
        var nullChecks = new StringBuilder();
        foreach (var param in nullableParams)
        {
            nullChecks.AppendLine($"{indent}global::System.ArgumentNullException.ThrowIfNull({param});");
        }

        var openBrace = body.OpenBraceToken;
        var insertPosition = openBrace.Span.End;

        // Get the text and create the modified version
        var sourceText = await document.GetTextAsync(cancellationToken);
        var newText = sourceText.Replace(new TextSpan(insertPosition, 0),
            WithDocumentLineEndings("\n" + nullChecks.ToString(), sourceText));

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    methodName,
                    parametersWithNullChecks = nullableParams,
                    skippedNullable,
                    skippedAlreadyGuarded,
                    generatedCode = nullChecks.ToString().Trim(),
                    changes = new[]
                    {
                        new
                        {
                            filePath = address.FilePath,
                            insertAfterLine = openBrace.GetLocation().GetLineSpan().StartLinePosition.Line,
                            newCode = nullChecks.ToString().Trim()
                        }
                    }
                },
                suggestedNextTools: new[] { "add_null_checks with preview=false to apply" }
            );
        }

        // Apply through the unified path — workspace, in-memory solution, caches,
        // and disk stay consistent; no sync_documents needed afterward.
        var newSolution = _solution!.WithDocumentText(document.Id, newText);
        var (ok, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName,
                parametersWithNullChecks = nullableParams,
                skippedNullable,
                skippedAlreadyGuarded,
                generatedCode = nullChecks.ToString().Trim(),
                changedFiles = FormatAppliedChanges(applyReport),
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[] { "get_diagnostics to verify the guards compile" }
        );
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

    /// <summary>
    /// Generate Equals, GetHashCode, and operators for a type.
    /// </summary>
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
                context: new { filePath = address.FilePath, line = address.Line, column = address.Column }
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
        // not valid C# and the auto-property already covers that storage) and
        // computed get-only properties (they derive from other compared state).
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
            .Where(p => !p.IsStatic && p.GetMethod != null && p.SetMethod != null
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
                (m is IPropertySymbol prop && !prop.IsStatic && prop.GetMethod != null
                    && (prop.SetMethod != null || autoPropertyTargets.Contains(prop))))
            .Select(m => m.Name)
            .ToList();

        if (members.Count == 0 && !chainBase)
        {
            return CreateSuccessResponse(
                data: new
                {
                    message = "No instance fields or properties found to compare",
                    typeName = targetTypeName
                }
            );
        }

        // Build the members as syntax, not string concatenation (the bug class
        // behind emitting <Prop>k__BackingField names): identifiers flow through
        // the generator, and NormalizeWhitespace is correct for fresh generated
        // syntax. GetHashCode uses the HashCode builder so EVERY compared member
        // feeds the hash — HashCode.Combine caps at eight arguments, and hashing
        // a subset of what Equals compares breaks the Equals/GetHashCode contract.
        var generator = Microsoft.CodeAnalysis.Editing.SyntaxGenerator.GetGenerator(document);
        var generatedMembers = BuildEqualityMemberDeclarations(
            generator, declaredTypeName, members, includeOperators, chainBase, chainBaseHash, typeSymbol.IsValueType);
        var generatedText = string.Join("\n\n",
            generatedMembers.Select(m => m.NormalizeWhitespace().ToFullString()));

        // Find where to insert (before closing brace of type)
        var closeBrace = typeNode.CloseBraceToken;
        var insertPosition = closeBrace.SpanStart;

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    typeName = targetTypeName,
                    membersCompared = members,
                    includeOperators,
                    generatedCode = generatedText
                },
                suggestedNextTools: new[] { "generate_equality_members with preview=false to apply" }
            );
        }

        // Apply through the unified path — workspace, in-memory solution, caches,
        // and disk stay consistent; no sync_documents needed afterward.
        var sourceText = await document.GetTextAsync(cancellationToken);
        var newText = sourceText.Replace(new TextSpan(insertPosition, 0),
            WithDocumentLineEndings("\n" + generatedText + "\n", sourceText));
        var newSolution = _solution!.WithDocumentText(document.Id, newText);
        var (ok, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                typeName = targetTypeName,
                membersCompared = members,
                includeOperators,
                changedFiles = FormatAppliedChanges(applyReport),
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[] { "get_diagnostics to verify the generated members compile" }
        );
    }

    // The equality members as syntax declarations. Member and type names enter
    // as identifier nodes (keyword escaping handled by the generator), never
    // interpolated strings.
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

    private static List<SyntaxNode> BuildEqualityMemberDeclarations(
        Microsoft.CodeAnalysis.Editing.SyntaxGenerator generator,
        string typeName,
        List<string> memberNames,
        bool includeOperators,
        bool chainBase,
        bool chainBaseHash,
        bool isValueType)
    {
        var boolType = generator.TypeExpression(SpecialType.System_Boolean);
        var typeSyntax = SyntaxFactory.ParseTypeName(typeName);
        var nullableTypeSyntax = SyntaxFactory.ParseTypeName(typeName + "?");
        // A struct's strongly-typed Equals takes the value non-nullable (`T`, not
        // `T?` which would be Nullable<T>); the `is null` and ReferenceEquals
        // short-circuits are class-only — both are meaningless on a value type
        // (a struct can't be null, and ReferenceEquals boxes and is always false).
        var typedEqualsParamType = isValueType ? typeSyntax : nullableTypeSyntax;
        var operatorParamType = isValueType ? typeSyntax : nullableTypeSyntax;

        var objectEquals = generator.MethodDeclaration(
            "Equals",
            parameters: new[] { generator.ParameterDeclaration("obj", SyntaxFactory.ParseTypeName("object?")) },
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
        // intended shape is `A == other.A && B == other.B`.
        ExpressionSyntax? memberComparison = memberNames.Count > 0
            ? memberNames
                .Select(ExpressionSyntax (m) => SyntaxFactory.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    (ExpressionSyntax)generator.IdentifierName(m),
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("other"),
                        (SimpleNameSyntax)generator.IdentifierName(m))))
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

        var hashStatements = new List<SyntaxNode>
        {
            generator.LocalDeclarationStatement("hash",
                generator.ObjectCreationExpression(SyntaxFactory.ParseTypeName("global::System.HashCode")))
        };
        if (chainBaseHash)
        {
            hashStatements.Add(generator.ExpressionStatement(
                generator.InvocationExpression(
                    generator.MemberAccessExpression(generator.IdentifierName("hash"), "Add"),
                    generator.InvocationExpression(
                        generator.MemberAccessExpression(SyntaxFactory.BaseExpression(), "GetHashCode")))));
        }
        hashStatements.AddRange(memberNames.Select(m => generator.ExpressionStatement(
            generator.InvocationExpression(
                generator.MemberAccessExpression(generator.IdentifierName("hash"), "Add"),
                generator.IdentifierName(m)))));
        hashStatements.Add(generator.ReturnStatement(
            generator.InvocationExpression(
                generator.MemberAccessExpression(generator.IdentifierName("hash"), "ToHashCode"))));
        var getHashCode = generator.MethodDeclaration(
            "GetHashCode",
            returnType: generator.TypeExpression(SpecialType.System_Int32),
            accessibility: Accessibility.Public,
            modifiers: Microsoft.CodeAnalysis.Editing.DeclarationModifiers.Override,
            statements: hashStatements);

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

    // The document's line ending (from its first line break), defaulting to "\n".
    internal static string DetectLineEnding(SourceText text)
    {
        if (text.Lines.Count > 0)
        {
            var first = text.Lines[0];
            if (first.EndIncludingLineBreak > first.End)
                return text.ToString(TextSpan.FromBounds(first.End, first.EndIncludingLineBreak));
        }
        return "\n";
    }

    // Rewrites generated code's newlines to the document's line ending, so generated
    // members/guards spliced into a CRLF file don't introduce lone LFs.
    internal static string WithDocumentLineEndings(string generated, SourceText document)
    {
        var eol = DetectLineEnding(document);
        // Normalize in both directions: collapse any CRLF in the generated text to LF first,
        // then expand to the document's ending — so CRLF-bearing generated text spliced into
        // an LF file (and the reverse) never leaves mixed endings.
        return generated.Replace("\r\n", "\n").Replace("\n", eol);
    }

    // Renders a type for GENERATED code: fully qualified with a global:: prefix so it
    // resolves with no using directive, whatever the generated file's imports are. Every
    // codegen type reference routes through this so a nested type (Outer.Inner) or a
    // foreign-namespace type can't slip out as an unresolvable simple name (CS0246).
    private static string TypeRef(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
