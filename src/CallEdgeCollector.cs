using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SharpLensMcp;

// "What does this method call": the members a method, accessor, operator or constructor
// invokes or touches, each with how it gets there (CallEdgeKind), the syntax that makes
// the call, the static type of the receiver it goes through, and resolved to the member
// that carries the body that runs; plus the throw sites of the bodies walked. Tool-
// agnostic: the input is the symbol plus the compilation to bind it with, the output is
// CallEdges and ThrowSites, and nothing is cached. Every call tool (get_outgoing_calls,
// find_callers, get_call_graph, find_path_between, get_exception_flow, analyze_method)
// and the coverage walk read their edges from here.
//
// It works on the bound operation tree rather than on syntax kinds, so it sees every call
// the compiler emits for the code as written: explicit and implicit constructor calls
// (target-typed new, : base(...)/: this(...), the implicit base() call, a primary
// constructor's base-type arguments), collection-initializer Add calls, user-defined
// operators and conversions, and the accessor a property, indexer or event use actually
// runs. The constructs the operation tree exposes only through the semantic model
// (foreach's enumerator, using's Dispose, deconstruction, await's awaiter) are resolved
// through it. A constructor also runs its type's field, property and event initializers
// unless it chains to : this(...); a compiler-synthesized constructor, and a primary
// constructor with no base-type arguments, runs those and, for a class, the base type's
// parameterless constructor. Lambda and local-function bodies are part of the enclosing
// body's tree, so their calls and throws belong to it; nameof(...) names a member without
// running it and is skipped.
//
// The compilation must be the one the caller resolves symbols against (for SharpLens, the
// generator-aware GetProjectCompilationAsync compilation, never the workspace's default
// semantic model): edges are bound in it, so their targets are its symbol instances.
internal static class CallEdgeCollector
{
    public static CallEdgeResult Collect(IMethodSymbol method, Compilation compilation, CancellationToken cancellationToken)
    {
        var edges = new List<CallEdge>();
        var throws = new List<ThrowSite>();
        var bound = false;
        var body = (IMethodSymbol)ResolveBodyCarrier(DeclaredIn(method, compilation));
        var isConstructor = body.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor;

        foreach (var declRef in body.DeclaringSyntaxReferences)
        {
            if (!compilation.ContainsSyntaxTree(declRef.SyntaxTree)) continue;
            var model = compilation.GetSemanticModel(declRef.SyntaxTree);
            var syntax = declRef.GetSyntax(cancellationToken);

            // A primary constructor's declaring syntax is its type declaration, whose
            // operation is the constructor body (the base-type arguments' constructor
            // call) when the type passes base-type arguments, and null otherwise. Any other
            // member declared by the type declaration is one the compiler synthesizes (a
            // record's copy constructor, ToString, Equals, ...) and has no body in source.
            var isPrimaryConstructor = syntax is TypeDeclarationSyntax
                                       && body.MethodKind == MethodKind.Constructor && !body.IsImplicitlyDeclared;
            if (syntax is TypeDeclarationSyntax && !isPrimaryConstructor) continue;
            bound = true;

            var operation = model.GetOperation(syntax, cancellationToken);
            if (operation != null) Walk(operation, model, edges, throws, inMemberInitializer: false);
            else if (isPrimaryConstructor) AddImplicitBaseConstructorCall(body, syntax, edges);

            if (isConstructor && syntax is not ConstructorDeclarationSyntax
                    { Initializer.ThisOrBaseKeyword.RawKind: (int)SyntaxKind.ThisKeyword })
            {
                CollectInitializers(body.ContainingType, body.IsStatic, compilation, edges, throws, cancellationToken);
            }
        }

        if (body.DeclaringSyntaxReferences.IsEmpty && isConstructor && body.IsImplicitlyDeclared
            && body.ContainingType is { } type && type.Locations.Any(l => l.IsInSource))
        {
            bound = CollectInitializers(type, body.IsStatic, compilation, edges, throws, cancellationToken);
            if (type.DeclaringSyntaxReferences.FirstOrDefault(r => compilation.ContainsSyntaxTree(r.SyntaxTree)) is { } typeReference)
            {
                AddImplicitBaseConstructorCall(body, typeReference.GetSyntax(cancellationToken), edges);
            }
        }

        return new CallEdgeResult(edges, throws, bound);
    }

    // An instance constructor of a class with no constructor initializer in source (the
    // compiler-synthesized parameterless constructor, or a primary constructor without
    // base-type arguments) calls the base type's parameterless constructor; the call site
    // is the type declaration. (An explicit constructor's implicit base() call is in its
    // own operation tree.)
    private static void AddImplicitBaseConstructorCall(IMethodSymbol constructor, SyntaxNode typeDeclaration, List<CallEdge> edges)
    {
        if (constructor.IsStatic || constructor.ContainingType is not { TypeKind: TypeKind.Class } type) return;
        if (constructor.IsImplicitlyDeclared && !constructor.Parameters.IsEmpty) return;
        if (type.BaseType?.InstanceConstructors.FirstOrDefault(c => c.Parameters.IsEmpty) is not { } baseConstructor) return;
        edges.Add(new CallEdge(ResolveBodyCarrier(baseConstructor), CallEdgeKind.ConstructorInitializer,
            ViaDispatch: false, InMemberInitializer: false, typeDeclaration, ReceiverType: null));
    }

    // The location a tool reports for an edge: the name of the invoked or accessed member
    // where the call is written (`Leaf` in Target.Leaf(x), `Worker` in new Worker(), `Area`
    // in s.Area()), the operator token of an operator, the keyword of a constructor
    // initializer, foreach, using or await, and the identifier of the constructor or type
    // declaration whose constructor makes an implicit base() call.
    public static Location SiteLocation(CallEdge edge) => SiteOf(edge.Syntax) switch
    {
        { IsNode: true } node => node.AsNode()!.GetLocation(),
        var token => token.AsToken().GetLocation()
    };

    private static SyntaxNodeOrToken SiteOf(SyntaxNode syntax) => syntax switch
    {
        InvocationExpressionSyntax invocation => NameOf(invocation.Expression),
        ObjectCreationExpressionSyntax creation => TypeNameOf(creation.Type),
        ImplicitObjectCreationExpressionSyntax creation => creation.NewKeyword,
        ConstructorInitializerSyntax initializer => initializer.ThisOrBaseKeyword,
        PrimaryConstructorBaseTypeSyntax baseType => TypeNameOf(baseType.Type),
        BaseTypeDeclarationSyntax declaration => declaration.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        MemberAccessExpressionSyntax or MemberBindingExpressionSyntax or SimpleNameSyntax => NameOf((ExpressionSyntax)syntax),
        AssignmentExpressionSyntax assignment => assignment.OperatorToken,
        BinaryExpressionSyntax binary => binary.OperatorToken,
        PrefixUnaryExpressionSyntax prefix => prefix.OperatorToken,
        PostfixUnaryExpressionSyntax postfix => postfix.OperatorToken,
        CommonForEachStatementSyntax forEach => forEach.ForEachKeyword,
        UsingStatementSyntax usingStatement => usingStatement.UsingKeyword,
        LocalDeclarationStatementSyntax { UsingKeyword.RawKind: (int)SyntaxKind.UsingKeyword } usingDeclaration =>
            usingDeclaration.UsingKeyword,
        AwaitExpressionSyntax awaitExpression => awaitExpression.AwaitKeyword,
        _ => syntax
    };

    private static SyntaxNodeOrToken NameOf(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
        MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
        _ => expression
    };

    private static SyntaxNodeOrToken TypeNameOf(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right,
        AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name,
        _ => type
    };

    // The method as `compilation` itself declares it. A symbol seen from another compilation
    // (through a retargeting assembly) is a distinct instance that does not expose every
    // source-only detail, such as a partial member's implementation part.
    private static IMethodSymbol DeclaredIn(IMethodSymbol method, Compilation compilation)
    {
        var definition = method.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(definition.ContainingAssembly, compilation.Assembly)) return definition;
        var referenceId = CrossCompilationSymbol.ReferenceId(definition);
        if (referenceId == null) return definition;
        return DocumentationCommentId.GetSymbolsForDeclarationId(referenceId, compilation)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => SymbolEqualityComparer.Default.Equals(m.ContainingAssembly, compilation.Assembly))
            ?? definition;
    }

    // The member whose body runs for a reference to `symbol`: its OriginalDefinition, the
    // static definition of a reduced extension method, and the implementation part of a
    // partial method or property (the definition part has no body).
    public static ISymbol ResolveBodyCarrier(ISymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        return definition switch
        {
            IMethodSymbol { ReducedFrom: { } unreduced } => ResolveBodyCarrier(unreduced),
            IMethodSymbol { PartialImplementationPart: { } implementation } => implementation.OriginalDefinition,
            IPropertySymbol { PartialImplementationPart: { } implementation } => implementation.OriginalDefinition,
            _ => definition
        };
    }

    // The field, property and field-like event initializers of one staticness across all of
    // the type's declarations. Returns whether any initializer was bound.
    private static bool CollectInitializers(
        INamedTypeSymbol type, bool isStatic, Compilation compilation, List<CallEdge> edges, List<ThrowSite> throws,
        CancellationToken cancellationToken)
    {
        var bound = false;
        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            if (!compilation.ContainsSyntaxTree(declRef.SyntaxTree)) continue;
            if (declRef.GetSyntax(cancellationToken) is not TypeDeclarationSyntax declaration) continue;
            var initializers = declaration.Members.SelectMany(member => member switch
            {
                FieldDeclarationSyntax field when IsStatic(field.Modifiers) == isStatic
                                                  && !field.Modifiers.Any(SyntaxKind.ConstKeyword) =>
                    field.Declaration.Variables.Select(v => (SyntaxNode?)v.Initializer),
                EventFieldDeclarationSyntax eventField when IsStatic(eventField.Modifiers) == isStatic =>
                    eventField.Declaration.Variables.Select(v => (SyntaxNode?)v.Initializer),
                PropertyDeclarationSyntax property when IsStatic(property.Modifiers) == isStatic =>
                    new[] { (SyntaxNode?)property.Initializer },
                _ => Array.Empty<SyntaxNode?>()
            }).OfType<SyntaxNode>().ToList();
            if (initializers.Count == 0) continue;

            var model = compilation.GetSemanticModel(declRef.SyntaxTree);
            foreach (var initializer in initializers)
            {
                if (model.GetOperation(initializer, cancellationToken) is { } operation)
                {
                    Walk(operation, model, edges, throws, inMemberInitializer: true);
                    bound = true;
                }
            }
        }
        return bound;
    }

    private static bool IsStatic(SyntaxTokenList modifiers) => modifiers.Any(SyntaxKind.StaticKeyword);

    private static void Walk(
        IOperation root, SemanticModel model, List<CallEdge> edges, List<ThrowSite> throws, bool inMemberInitializer)
    {
        var currentSyntax = root.Syntax;
        void Add(ISymbol? target, CallEdgeKind kind, bool viaDispatch, ITypeSymbol? receiverType)
        {
            if (target == null) return;
            // Lambdas and local functions are walked inline as part of the enclosing body,
            // so a call to one is not a separate edge.
            if (target is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }) return;
            edges.Add(new CallEdge(ResolveBodyCarrier(target), kind, viaDispatch, inMemberInitializer, currentSyntax, receiverType));
        }

        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var operation = stack.Pop();
            if (operation is INameOfOperation) continue;
            currentSyntax = operation.Syntax;
            if (operation is IThrowOperation throwOperation)
            {
                throws.Add(new ThrowSite(Unconverted(throwOperation.Exception)?.Type, throwOperation.Syntax,
                    RethrownCatch(throwOperation)));
            }
            Visit(operation, model, Add);
            foreach (var child in operation.ChildOperations) stack.Push(child);
        }
    }

    // The catch clause whose caught exception a throw rethrows: a bare `throw;` rethrows
    // its nearest enclosing catch's exception, `throw e;` the exception of the catch that
    // declares e. A lambda or local function boundary ends the search: a throw inside one
    // runs whenever it is invoked, not in the catch.
    private static SyntaxNode? RethrownCatch(IThrowOperation throwOperation)
    {
        var thrown = Unconverted(throwOperation.Exception);
        if (thrown != null && thrown is not ILocalReferenceOperation) return null;
        for (var parent = throwOperation.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation) return null;
            if (parent is not ICatchClauseOperation catchClause) continue;
            if (thrown == null) return catchClause.Syntax;
            if (thrown is ILocalReferenceOperation { Local: var local }
                && catchClause.Locals.Contains(local, SymbolEqualityComparer.Default))
            {
                return catchClause.Syntax;
            }
        }
        return null;
    }

    // An operand as written: the operand of the implicit conversion the compiler wraps it
    // in (a thrown exception to System.Exception, a receiver to the parameter type).
    private static IOperation? Unconverted(IOperation? operation) =>
        operation is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : operation;

    private static ITypeSymbol? ReceiverTypeOf(IOperation? instance) => Unconverted(instance)?.Type;

    private static void Visit(IOperation operation, SemanticModel model, Action<ISymbol?, CallEdgeKind, bool, ITypeSymbol?> add)
    {
        switch (operation)
        {
            case IInvocationOperation invocation:
                add(invocation.TargetMethod,
                    invocation.TargetMethod.MethodKind == MethodKind.Constructor
                        ? CallEdgeKind.ConstructorInitializer
                        : CallEdgeKind.Invocation,
                    invocation.IsVirtual,
                    invocation.Instance != null
                        ? ReceiverTypeOf(invocation.Instance)
                        : invocation.TargetMethod.IsExtensionMethod
                            ? ReceiverTypeOf(invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value)
                            : null);
                break;
            case IObjectCreationOperation creation:
                add(creation.Constructor, CallEdgeKind.ObjectCreation, false, null);
                break;
            case IMethodReferenceOperation methodReference:
                add(methodReference.Method, CallEdgeKind.DelegateCreation, methodReference.IsVirtual,
                    ReceiverTypeOf(methodReference.Instance));
                break;
            case IPropertyReferenceOperation propertyReference:
                AddPropertyAccess(propertyReference, add);
                break;
            case IFieldReferenceOperation fieldReference:
                add(fieldReference.Field, CallEdgeKind.MemberAccess, false, ReceiverTypeOf(fieldReference.Instance));
                break;
            case IEventAssignmentOperation { EventReference: IEventReferenceOperation eventReference } eventAssignment:
                var eventAccessor = eventAssignment.Adds ? eventReference.Event.AddMethod : eventReference.Event.RemoveMethod;
                add(eventAccessor ?? (ISymbol)eventReference.Event,
                    eventAssignment.Adds ? CallEdgeKind.EventAdd : CallEdgeKind.EventRemove,
                    IsDispatch(eventReference.Event, eventReference.Instance), ReceiverTypeOf(eventReference.Instance));
                break;
            // The event of a += / -= is reached through its accessor (above), not as a value.
            case IEventReferenceOperation eventReference when eventReference.Parent is not IEventAssignmentOperation:
                add(eventReference.Event, CallEdgeKind.MemberAccess, false, ReceiverTypeOf(eventReference.Instance));
                break;
            case IUnaryOperation unary:
                AddMethod(unary.OperatorMethod, CallEdgeKind.Operator, add);
                break;
            case IBinaryOperation binary:
                AddMethod(binary.OperatorMethod, CallEdgeKind.Operator, add);
                AddShortCircuitTest(binary, add);
                break;
            case IIncrementOrDecrementOperation increment:
                AddMethod(increment.OperatorMethod, CallEdgeKind.Operator, add);
                break;
            case ICompoundAssignmentOperation compound:
                AddMethod(compound.OperatorMethod, CallEdgeKind.Operator, add);
                AddMethod(compound.InConversion.MethodSymbol, CallEdgeKind.Conversion, add);
                AddMethod(compound.OutConversion.MethodSymbol, CallEdgeKind.Conversion, add);
                break;
            case IConversionOperation conversion:
                AddMethod(conversion.OperatorMethod, CallEdgeKind.Conversion, add);
                break;
            case IForEachLoopOperation { Syntax: CommonForEachStatementSyntax forEach } loop:
                var forEachInfo = model.GetForEachStatementInfo(forEach);
                var enumeratorType = forEachInfo.GetEnumeratorMethod?.ReturnType;
                AddMethod(forEachInfo.GetEnumeratorMethod, CallEdgeKind.ImplicitCall, add, ReceiverTypeOf(loop.Collection));
                AddMethod(forEachInfo.MoveNextMethod, CallEdgeKind.ImplicitCall, add, enumeratorType);
                AddMethod(forEachInfo.CurrentProperty?.GetMethod, CallEdgeKind.ImplicitCall, add, enumeratorType);
                AddMethod(forEachInfo.DisposeMethod, CallEdgeKind.ImplicitCall, add, enumeratorType);
                if (forEach is ForEachVariableStatementSyntax deconstructingForEach)
                {
                    AddDeconstruction(model.GetDeconstructionInfo(deconstructingForEach), add);
                }
                break;
            case IUsingOperation usingStatement:
                AddDispose(usingStatement.Resources, usingStatement.IsAsynchronous, model, add);
                break;
            case IUsingDeclarationOperation usingDeclaration:
                AddDispose(usingDeclaration.DeclarationGroup, usingDeclaration.IsAsynchronous, model, add);
                break;
            case IDeconstructionAssignmentOperation { Syntax: AssignmentExpressionSyntax assignment }:
                AddDeconstruction(model.GetDeconstructionInfo(assignment), add);
                break;
            case IRecursivePatternOperation { DeconstructSymbol: IMethodSymbol deconstruct } recursive:
                AddMethod(deconstruct, CallEdgeKind.ImplicitCall, add, recursive.MatchedType);
                break;
            case IAwaitOperation { Syntax: AwaitExpressionSyntax awaitExpression } awaitOperation:
                var awaitInfo = model.GetAwaitExpressionInfo(awaitExpression);
                var awaiterType = awaitInfo.GetAwaiterMethod?.ReturnType;
                AddMethod(awaitInfo.GetAwaiterMethod, CallEdgeKind.ImplicitCall, add, ReceiverTypeOf(awaitOperation.Operation));
                AddMethod(awaitInfo.IsCompletedProperty?.GetMethod, CallEdgeKind.ImplicitCall, add, awaiterType);
                AddMethod(awaitInfo.GetResultMethod, CallEdgeKind.ImplicitCall, add, awaiterType);
                AddContinuation(awaiterType, model.Compilation, add);
                break;
            case IWithOperation with:
                AddMethod(with.CloneMethod, CallEdgeKind.ImplicitCall, add, ReceiverTypeOf(with.Operand));
                break;
            case ICollectionExpressionOperation collection:
                AddMethod(collection.ConstructMethod, CallEdgeKind.ImplicitCall, add);
                break;
            case IImplicitIndexerReferenceOperation implicitIndexer:
                AddImplicitMember(implicitIndexer.LengthSymbol, add, ReceiverTypeOf(implicitIndexer.Instance));
                AddImplicitMember(implicitIndexer.IndexerSymbol, add, ReceiverTypeOf(implicitIndexer.Instance));
                break;
            case IRangeOperation range:
                AddMethod(range.Method, CallEdgeKind.ImplicitCall, add);
                break;
        }
    }

    // A call with no receiver expression in source: it dispatches when the method is virtual,
    // abstract, an override, or an instance interface member. receiverType is the static
    // type the call goes through when the construct has one (a foreach collection or
    // enumerator, a using resource, an awaiter).
    private static void AddMethod(
        IMethodSymbol? method, CallEdgeKind kind, Action<ISymbol?, CallEdgeKind, bool, ITypeSymbol?> add,
        ITypeSymbol? receiverType = null)
    {
        if (method != null) add(method, kind, IsDispatchable(method), receiverType);
    }

    // A user-defined && or || first tests its left operand with the type's operator false
    // (for &&) or operator true (for ||), then applies the user-defined & or |.
    private static void AddShortCircuitTest(IBinaryOperation binary, Action<ISymbol?, CallEdgeKind, bool, ITypeSymbol?> add)
    {
        if (binary.OperatorMethod is not { ContainingType: { } operatorType }) return;
        var testName = binary.OperatorKind switch
        {
            BinaryOperatorKind.ConditionalAnd => "op_False",
            BinaryOperatorKind.ConditionalOr => "op_True",
            _ => null
        };
        if (testName == null) return;
        AddMethod(operatorType.GetMembers(testName).OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length == 1), CallEdgeKind.Operator, add);
    }

    // An index or range applied through a type's Length/Count and indexer or Slice.
    private static void AddImplicitMember(
        ISymbol? member, Action<ISymbol?, CallEdgeKind, bool, ITypeSymbol?> add, ITypeSymbol? receiverType)
    {
        switch (member)
        {
            case IPropertySymbol property:
                AddMethod(property.GetMethod, CallEdgeKind.ImplicitCall, add, receiverType);
                break;
            case IMethodSymbol method:
                AddMethod(method, CallEdgeKind.ImplicitCall, add, receiverType);
                break;
        }
    }

    // A read runs the getter, a write (assignment, object-initializer member, deconstruction
    // target) the setter, and a compound assignment, ??= or ++/-- both. A write with no setter
    // assigns a get-only auto-property's backing field in a constructor, or goes through a
    // ref-returning getter.
    private static void AddPropertyAccess(
        IPropertyReferenceOperation reference, Action<ISymbol?, CallEdgeKind, bool, ITypeSymbol?> add)
    {
        var property = reference.Property;
        var viaDispatch = IsDispatch(property, reference.Instance);
        var receiverType = ReceiverTypeOf(reference.Instance);
        var (reads, writes) = AccessOf(reference);
        if (reads)
        {
            if (property.GetMethod != null) add(property.GetMethod, CallEdgeKind.PropertyGet, viaDispatch, receiverType);
            else add(property, CallEdgeKind.MemberAccess, viaDispatch, receiverType);
        }
        if (writes)
        {
            if (property.SetMethod != null) add(property.SetMethod, CallEdgeKind.PropertySet, viaDispatch, receiverType);
            else if (property.ReturnsByRef && property.GetMethod != null) add(property.GetMethod, CallEdgeKind.PropertyGet, viaDispatch, receiverType);
            else add(property, CallEdgeKind.MemberAccess, viaDispatch, receiverType);
        }
    }

    private static (bool reads, bool writes) AccessOf(IOperation reference) => reference.Parent switch
    {
        ISimpleAssignmentOperation assignment when assignment.Target == reference => (false, true),
        ICompoundAssignmentOperation compound when compound.Target == reference => (true, true),
        ICoalesceAssignmentOperation coalesce when coalesce.Target == reference => (true, true),
        IIncrementOrDecrementOperation increment when increment.Target == reference => (true, true),
        ITupleOperation when IsDeconstructionTarget(reference) => (false, true),
        _ => (true, false)
    };

    private static bool IsDeconstructionTarget(IOperation operation)
    {
        var current = operation;
        while (current.Parent is ITupleOperation tuple) current = tuple;
        return current.Parent is IDeconstructionAssignmentOperation deconstruction && deconstruction.Target == current;
    }

    private static void AddDeconstruction(DeconstructionInfo info, Action<ISymbol?, CallEdgeKind, bool, ITypeSymbol?> add)
    {
        AddMethod(info.Method, CallEdgeKind.ImplicitCall, add);
        AddMethod(info.Conversion?.MethodSymbol, CallEdgeKind.Conversion, add);
        foreach (var nested in info.Nested) AddDeconstruction(nested, add);
    }

    // using / await using call the resource type's Dispose / DisposeAsync: the
    // IDisposable / IAsyncDisposable implementation on a class or struct, the interface
    // member itself when the resource is typed as an interface or type parameter, or the
    // pattern-based method a ref struct declares.
    private static void AddDispose(
        IOperation resources, bool isAsynchronous, SemanticModel model, Action<ISymbol?, CallEdgeKind, bool, ITypeSymbol?> add)
    {
        var compilation = model.Compilation;
        var disposable = isAsynchronous
            ? compilation.GetTypeByMetadataName("System.IAsyncDisposable")
            : compilation.GetSpecialType(SpecialType.System_IDisposable);
        var methodName = isAsynchronous ? "DisposeAsync" : "Dispose";
        var interfaceMethod = disposable?.GetMembers(methodName).OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.IsEmpty);

        IEnumerable<ITypeSymbol?> resourceTypes = resources is IVariableDeclarationGroupOperation group
            ? group.Declarations.SelectMany(d => d.Declarators).Select(d => (ITypeSymbol?)d.Symbol.Type)
            : new[] { Unconverted(resources)?.Type };

        foreach (var type in resourceTypes)
        {
            switch (type)
            {
                case null:
                    break;
                case ITypeParameterSymbol:
                case { TypeKind: TypeKind.Interface }:
                    AddMethod(interfaceMethod, CallEdgeKind.ImplicitCall, add, type);
                    break;
                default:
                    var implementation = interfaceMethod != null
                        ? type.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol
                        : null;
                    AddMethod(implementation ?? type.GetMembers(methodName).OfType<IMethodSymbol>()
                        .FirstOrDefault(m => !m.IsStatic && m.Parameters.All(p => p.IsOptional || p.IsParams)),
                        CallEdgeKind.ImplicitCall, add, type);
                    break;
            }
        }
    }

    // The state machine an await compiles to schedules its continuation through the
    // awaiter's ICriticalNotifyCompletion.UnsafeOnCompleted when the awaiter implements
    // it, otherwise through INotifyCompletion.OnCompleted.
    private static void AddContinuation(
        ITypeSymbol? awaiterType, Compilation compilation, Action<ISymbol?, CallEdgeKind, bool, ITypeSymbol?> add)
    {
        if (awaiterType == null) return;
        var critical = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ICriticalNotifyCompletion");
        var notify = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.INotifyCompletion");
        var (contract, methodName) = critical != null && Implements(awaiterType, critical)
            ? (critical, "UnsafeOnCompleted")
            : (notify, "OnCompleted");
        if (contract == null || !Implements(awaiterType, contract)) return;
        var interfaceMethod = contract.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (interfaceMethod == null) return;
        AddMethod(awaiterType.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol ?? interfaceMethod,
            CallEdgeKind.ImplicitCall, add, awaiterType);
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol contract) =>
        type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, contract));

    // A reference through a receiver dispatches when the member is dispatchable and the
    // receiver is not `base` (base.X() is a non-virtual call).
    private static bool IsDispatch(ISymbol member, IOperation? instance) =>
        IsDispatchable(member) && instance?.Syntax is not BaseExpressionSyntax;

    public static bool IsDispatchable(ISymbol member) =>
        member.IsVirtual || member.IsAbstract || member.IsOverride
        || (member.ContainingType?.TypeKind == TypeKind.Interface && !member.IsStatic);
}
