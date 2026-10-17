using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace SharpLensMcp;

// The call graph one tool call walks, built lazily over CallEdgeCollector and memoized for
// that call only (tools keep no state across calls). Every call tool reads both directions
// from here, so they agree on what a call is:
//   - BodyAsync: what a member's body calls. The body is bound in every target framework
//     of its project (each compiles its own #if branches) and each call site is kept once.
//     A dispatched call also reaches the in-solution implementations and overrides its
//     receiver's static type can run (SolutionTypeIndex.DispatchTargets), flagged
//     ViaDispatch. Lambdas and local functions are part of their containing member.
//   - CallersAsync: the inverse. SymbolFinder supplies candidate members (generated
//     documents included) for every reference to the method, to a member it overrides or
//     implements (when the reference's receiver type can dispatch to the method), and,
//     for a constructor, the constructors of direct subclasses that call it implicitly; a
//     candidate is a caller exactly when its own BodyAsync has an edge to the method, so
//     the caller set is the callee relation read backwards. A reference inside a lambda or
//     local function belongs to the containing member, one inside a field, property or
//     event initializer to the constructors that run it.
// Members are keyed by the solution's cross-compilation identity
// (CrossCompilationSymbolComparer), so a member reached through several compilations is
// one node.
internal sealed class CallGraphSession
{
    private readonly Solution _solution;
    private readonly Func<Project, CancellationToken, Task<Compilation?>> _getCompilation;
    private readonly LogicalProjectModel _projects;
    private readonly Dictionary<ISymbol, WalkedBody> _bodies = new(CrossCompilationSymbolComparer.Instance);
    private readonly Dictionary<ISymbol, IReadOnlyList<CallerSite>> _callers = new(CrossCompilationSymbolComparer.Instance);
    private readonly Dictionary<SyntaxTree, Compilation?> _treeCompilations = new();
    private SolutionTypeIndex? _types;

    public CallGraphSession(
        Solution solution, Func<Project, CancellationToken, Task<Compilation?>> getCompilation, LogicalProjectModel projects)
    {
        _solution = solution;
        _getCompilation = getCompilation;
        _projects = projects;
    }

    // Whether a member has a body in the solution the session can walk: a declaration in
    // source, or a compiler-synthesized constructor of a source type.
    public static bool HasSourceBody(IMethodSymbol method)
    {
        var carrier = (IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(method);
        if (carrier.IsAbstract || carrier.IsExtern) return false;
        if (!carrier.DeclaringSyntaxReferences.IsEmpty) return true;
        return carrier.IsImplicitlyDeclared
               && carrier.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
               && carrier.ContainingType?.Locations.Any(l => l.IsInSource) == true;
    }

    public async Task<WalkedBody> BodyAsync(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var carrier = (IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(method);
        if (_bodies.TryGetValue(carrier, out var cached)) return cached;

        var callees = new List<ResolvedCallEdge>();
        var throws = new List<ThrowSite>();
        var seenEdges = new HashSet<(string target, CallEdgeKind kind, bool viaDispatch, SourceSpanKey? site)>();
        var seenThrows = new HashSet<SourceSpanKey?>();
        var bound = false;

        void AddEdge(ResolvedCallEdge edge)
        {
            if (seenEdges.Add((IdentityOf(edge.Target), edge.Kind, edge.ViaDispatch, SourceSpanKey.From(edge.Site)))) callees.Add(edge);
        }

        foreach (var compilation in await CompilationsDeclaringAsync(carrier, cancellationToken))
        {
            var result = CallEdgeCollector.Collect(carrier, compilation, cancellationToken);
            bound |= result.BodyBound;
            foreach (var edge in result.Edges)
            {
                if (edge.Target is not IMethodSymbol target) continue;
                _treeCompilations.TryAdd(edge.Syntax.SyntaxTree, compilation);
                var site = CallEdgeCollector.SiteLocation(edge);
                AddEdge(new ResolvedCallEdge(target, edge.Kind, false, edge.Syntax, site));
                if (!edge.ViaDispatch) continue;
                foreach (var implementation in (await TypesAsync(cancellationToken)).DispatchTargets(target, edge.ReceiverType))
                {
                    AddEdge(new ResolvedCallEdge(implementation, edge.Kind, true, edge.Syntax, site));
                }
            }
            foreach (var thrown in result.Throws)
            {
                _treeCompilations.TryAdd(thrown.Syntax.SyntaxTree, compilation);
                if (seenThrows.Add(SourceSpanKey.From(thrown.Syntax.GetLocation()))) throws.Add(thrown);
            }
        }

        var body = new WalkedBody(carrier, callees, throws, bound);
        _bodies[carrier] = body;
        return body;
    }

    // An identity string for de-duplicating edges: the solution key, or for a member
    // without one (an anonymous type's) its display string.
    private static string IdentityOf(ISymbol symbol) =>
        CrossCompilationSymbol.SolutionKey(symbol) ?? symbol.ToDisplayString();

    public async Task<IReadOnlyList<CallerSite>> CallersAsync(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var carrier = (IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(method);
        if (_callers.TryGetValue(carrier, out var cached)) return cached;

        var types = await TypesAsync(cancellationToken);
        var searched = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        var candidates = new List<IMethodSymbol>();
        var candidateSet = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        void AddCandidate(IMethodSymbol candidate)
        {
            if (candidateSet.Add(candidate)) candidates.Add(candidate);
        }

        foreach (var reached in new[] { carrier }.Concat(types.DispatchedThrough(carrier)))
        {
            // An accessor is referenced through its property or event.
            ISymbol searchSymbol = reached.AssociatedSymbol ?? reached;
            if (!searched.Add(searchSymbol)) continue;
            var dispatchedThrough = !CrossCompilationSymbolComparer.Instance.Equals(reached, carrier);
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(searchSymbol, _solution, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!location.Location.IsInSource) continue;
                    // A reference to a member the method overrides or implements reaches the
                    // method only through a receiver that can be an instance of its type;
                    // the rest are skipped before their bodies are bound.
                    if (dispatchedThrough && !await MayDispatchToAsync(location.Location, carrier, types, cancellationToken)) continue;
                    foreach (var member in await ContainingBodiesAsync(location.Location, cancellationToken)) AddCandidate(member);
                }
            }
        }
        if (carrier.MethodKind == MethodKind.Constructor)
        {
            foreach (var subclass in types.DirectSubclasses(carrier.ContainingType))
            {
                foreach (var constructor in subclass.InstanceConstructors) AddCandidate(constructor);
            }
        }

        var sites = new List<CallerSite>();
        foreach (var candidate in candidates)
        {
            var body = await BodyAsync(candidate, cancellationToken);
            foreach (var edge in body.Callees)
            {
                if (CrossCompilationSymbolComparer.Instance.Equals(edge.Target, carrier)) sites.Add(new CallerSite(body.Carrier, edge));
            }
        }

        _callers[carrier] = sites;
        return sites;
    }

    // Whether the call or member access at a reference location can dispatch to `carrier`:
    // the operation enclosing the reference is bound and its receiver's static type checked
    // against SolutionTypeIndex.DispatchTargets. A reference whose operation is not a call
    // or member access, or that has no receiver, is kept (its body's edges decide).
    private async Task<bool> MayDispatchToAsync(
        Location location, IMethodSymbol carrier, SolutionTypeIndex types, CancellationToken cancellationToken)
    {
        if (location.SourceTree is not { } tree || await CompilationContainingAsync(tree, cancellationToken) is not { } compilation)
        {
            return true;
        }
        var model = compilation.GetSemanticModel(tree);
        var node = (await tree.GetRootAsync(cancellationToken)).FindNode(location.SourceSpan, getInnermostNodeForTie: true);

        bool Reaches(IOperation? instance, params IMethodSymbol?[] boundMembers) =>
            instance == null
            || boundMembers.Any(bound => bound != null
                                         && types.DispatchTargets(bound, ReceiverTypeOf(instance))
                                             .Contains(carrier, CrossCompilationSymbolComparer.Instance));

        for (var current = node; current != null; current = current.Parent)
        {
            switch (model.GetOperation(current, cancellationToken))
            {
                case null:
                    continue;
                case IInvocationOperation invocation:
                    return Reaches(invocation.Instance, invocation.TargetMethod);
                case IMethodReferenceOperation methodReference:
                    return Reaches(methodReference.Instance, methodReference.Method);
                case IPropertyReferenceOperation propertyReference:
                    return Reaches(propertyReference.Instance, propertyReference.Property.GetMethod, propertyReference.Property.SetMethod);
                case IEventReferenceOperation eventReference:
                    return Reaches(eventReference.Instance, eventReference.Event.AddMethod, eventReference.Event.RemoveMethod);
                default:
                    return true;
            }
        }
        return true;
    }

    private static ITypeSymbol? ReceiverTypeOf(IOperation instance) =>
        instance is IConversionOperation { IsImplicit: true } conversion
            ? conversion.Operand.Type
            : instance.Type;

    // The members whose bodies contain a source location: the enclosing member, lifted out
    // of lambdas and local functions; for an initializer, the constructors of its
    // staticness; for a type-level position (a primary constructor's base-type arguments),
    // the type's instance constructors.
    private async Task<IReadOnlyList<IMethodSymbol>> ContainingBodiesAsync(Location location, CancellationToken cancellationToken)
    {
        if (location.SourceTree is not { } tree || await CompilationContainingAsync(tree, cancellationToken) is not { } compilation)
        {
            return Array.Empty<IMethodSymbol>();
        }
        var enclosing = compilation.GetSemanticModel(tree).GetEnclosingSymbol(location.SourceSpan.Start, cancellationToken);
        while (enclosing is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction })
        {
            enclosing = enclosing.ContainingSymbol;
        }
        return enclosing switch
        {
            IMethodSymbol member => new[] { member },
            IFieldSymbol or IPropertySymbol or IEventSymbol when enclosing.ContainingType is { } type =>
                (enclosing.IsStatic ? type.StaticConstructors : type.InstanceConstructors).ToList(),
            INamedTypeSymbol type => type.InstanceConstructors.ToList(),
            _ => Array.Empty<IMethodSymbol>()
        };
    }

    // The semantic model of a tree in the generator-aware compilation that contains it.
    public async Task<SemanticModel?> SemanticModelAsync(SyntaxTree tree, CancellationToken cancellationToken) =>
        (await CompilationContainingAsync(tree, cancellationToken))?.GetSemanticModel(tree);

    // The compilations of every target framework of the project declaring the member's
    // body: its own declaration, or for a compiler-synthesized constructor its type's.
    private async Task<IReadOnlyList<Compilation>> CompilationsDeclaringAsync(IMethodSymbol carrier, CancellationToken cancellationToken)
    {
        var tree = carrier.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree
                   ?? (carrier.IsImplicitlyDeclared ? carrier.ContainingType?.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree : null);
        if (tree == null || await ProjectContainingAsync(tree, cancellationToken) is not { } project) return Array.Empty<Compilation>();

        var compilations = new List<Compilation>();
        foreach (var member in _projects.Of(project).Projects)
        {
            if (await _getCompilation(member, cancellationToken) is { } compilation) compilations.Add(compilation);
        }
        return compilations;
    }

    private async Task<Compilation?> CompilationContainingAsync(SyntaxTree tree, CancellationToken cancellationToken)
    {
        if (_treeCompilations.TryGetValue(tree, out var cached)) return cached;
        Compilation? found = null;
        if (await ProjectContainingAsync(tree, cancellationToken) is { } project
            && await _getCompilation(project, cancellationToken) is { } compilation
            && compilation.ContainsSyntaxTree(tree))
        {
            found = compilation;
        }
        _treeCompilations[tree] = found;
        return found;
    }

    // The project whose generator-aware compilation contains a tree: the project of its
    // document (a source-generated document included), else found by compilation.
    private async Task<Project?> ProjectContainingAsync(SyntaxTree tree, CancellationToken cancellationToken)
    {
        if (_solution.GetDocument(tree)?.Project is { } documentProject) return documentProject;
        foreach (var project in _solution.Projects)
        {
            if (await _getCompilation(project, cancellationToken) is { } compilation && compilation.ContainsSyntaxTree(tree)) return project;
        }
        return null;
    }

    private async Task<SolutionTypeIndex> TypesAsync(CancellationToken cancellationToken)
    {
        if (_types != null) return _types;
        var compilations = new List<Compilation>();
        foreach (var project in _solution.Projects)
        {
            if (await _getCompilation(project, cancellationToken) is { } compilation) compilations.Add(compilation);
        }
        _types = SolutionTypeIndex.Build(compilations, cancellationToken);
        return _types;
    }
}
