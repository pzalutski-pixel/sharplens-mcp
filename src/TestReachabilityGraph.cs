using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The call graph the coverage tools (find_tests, find_untested_code) walk from test
// entry points, built lazily and memoized for the duration of ONE tool call (the tools
// keep no state across calls). Nodes are keyed by ReachabilityKey, so a declaration
// reached through symbol instances from different compilations is one node. Binding a
// body to find what it references is the expensive step; each node's callee list is
// computed once and shared by every walk in the call, so find_tests' per-test walks
// bind each body at most once between them.
internal sealed class TestReachabilityGraph
{
    private readonly Solution _solution;
    private readonly Func<Project, CancellationToken, Task<Compilation?>> _getCompilation;
    private readonly Action<IMethodSymbol>? _bodyBindObserver;
    private readonly Dictionary<ReachabilityKey, ReachableNode> _nodes = new();
    private readonly Dictionary<ReachabilityKey, IReadOnlyList<ReachableNode>> _typeInitializers = new();
    private readonly Dictionary<SyntaxTree, Compilation?> _compilations = new();

    public TestReachabilityGraph(
        Solution solution,
        Func<Project, CancellationToken, Task<Compilation?>> getCompilation,
        Action<IMethodSymbol>? bodyBindObserver)
    {
        _solution = solution;
        _getCompilation = getCompilation;
        _bodyBindObserver = bodyBindObserver;
    }

    // The node for the member whose body runs when `symbol` is referenced (see
    // CallEdgeCollector.ResolveBodyCarrier); one node per ReachabilityKey.
    public ReachableNode NodeFor(ISymbol symbol)
    {
        var definition = CallEdgeCollector.ResolveBodyCarrier(symbol);
        var key = ReachabilityKey.For(definition);
        if (_nodes.TryGetValue(key, out var node)) return node;
        node = new ReachableNode(
            definition, key,
            definition.ContainingType is { } containingType ? ReachabilityKey.For(containingType) : null);
        _nodes[key] = node;
        return node;
    }

    // Walks from the roots, marking every declaration they transitively reach. Reaching a
    // node also marks the declarations it covers (see CoveredBy) and runs its type's static
    // initialization; only nodes that are themselves reached have their bodies walked:
    // `reached` is the answer set and `walked` gates traversal, so a base method first
    // seeded through an override and later invoked via base.X() still has its own callees
    // walked. A member referenced through virtual or interface dispatch is also recorded in
    // `dispatched` (with an accessor's property or event), since any override or
    // implementation of it may be what runs. When isTarget is given, the walk stops as soon
    // as it marks a node the predicate accepts.
    public async Task<ReachabilityWalk> WalkAsync(
        IEnumerable<ISymbol> roots, Func<ReachableNode, bool>? isTarget, CancellationToken cancellationToken)
    {
        var reached = new HashSet<ReachabilityKey>();
        var dispatched = new HashSet<ReachabilityKey>();
        var walked = new HashSet<ReachabilityKey>();
        var queue = new Queue<ReachableNode>();

        bool Mark(ReachableNode node, bool viaDispatch)
        {
            if (viaDispatch && dispatched.Add(node.Key)
                && node.Symbol is IMethodSymbol { AssociatedSymbol: { } associated })
            {
                dispatched.Add(NodeFor(associated).Key);
            }
            foreach (var covered in CoveredBy(node))
            {
                if (reached.Add(covered.Key) && isTarget != null && isTarget(covered)) return true;
            }
            if (!walked.Add(node.Key)) return false;
            if (node.Symbol is IMethodSymbol) queue.Enqueue(node);
            foreach (var initializer in TypeInitializers(node))
            {
                if (Mark(initializer, viaDispatch: false)) return true;
            }
            return false;
        }

        foreach (var root in roots)
        {
            if (Mark(NodeFor(root), viaDispatch: false)) return new ReachabilityWalk(reached, dispatched, ReachedTarget: true);
        }
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var callee in await CalleesAsync(queue.Dequeue(), cancellationToken))
            {
                if (Mark(callee.Node, callee.ViaDispatch)) return new ReachabilityWalk(reached, dispatched, ReachedTarget: true);
            }
        }
        return new ReachabilityWalk(reached, dispatched, ReachedTarget: false);
    }

    // What reaching a node marks reached: the node; the property or event an accessor
    // belongs to; and the declarations it dispatches through (RelatedDeclarations), which
    // are invoked through it, with their own properties or events.
    private IReadOnlyList<ReachableNode> CoveredBy(ReachableNode node)
    {
        if (node.Covers != null) return node.Covers;
        var covers = new List<ReachableNode>();
        var seen = new HashSet<ReachabilityKey>();
        void Add(ReachableNode covered)
        {
            if (!seen.Add(covered.Key)) return;
            covers.Add(covered);
            if (covered.Symbol is IMethodSymbol { AssociatedSymbol: { } associated }) Add(NodeFor(associated));
        }
        Add(node);
        foreach (var related in RelatedDeclarations(node)) Add(related);
        node.Covers = covers;
        return covers;
    }

    // The declarations a method, property or event dispatches through: every base member
    // in its override chain and every interface member it implements, explicitly or
    // implicitly.
    public IReadOnlyList<ReachableNode> RelatedDeclarations(ReachableNode node)
    {
        if (node.RelatedDeclarations != null) return node.RelatedDeclarations;
        var member = node.Symbol;
        var related = new List<ReachableNode>();

        IEnumerable<ISymbol> overridden = member switch
        {
            IMethodSymbol method => Chain(method.OverriddenMethod, m => m.OverriddenMethod),
            IPropertySymbol property => Chain(property.OverriddenProperty, p => p.OverriddenProperty),
            IEventSymbol @event => Chain(@event.OverriddenEvent, e => e.OverriddenEvent),
            _ => Array.Empty<ISymbol>()
        };
        IEnumerable<ISymbol> explicitImplementations = member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations,
            IPropertySymbol property => property.ExplicitInterfaceImplementations,
            IEventSymbol @event => @event.ExplicitInterfaceImplementations,
            _ => Array.Empty<ISymbol>()
        };
        foreach (var declaration in overridden.Concat(explicitImplementations)) related.Add(NodeFor(declaration));

        if (member is IMethodSymbol or IPropertySymbol or IEventSymbol && member.ContainingType is { } containingType)
        {
            foreach (var iface in containingType.AllInterfaces)
            {
                foreach (var ifaceMember in iface.GetMembers())
                {
                    if (ifaceMember.Kind != member.Kind) continue;
                    if (SymbolEqualityComparer.Default.Equals(
                            containingType.FindImplementationForInterfaceMember(ifaceMember), member))
                    {
                        related.Add(NodeFor(ifaceMember));
                    }
                }
            }
        }
        node.RelatedDeclarations = related;
        return related;
    }

    private static IEnumerable<ISymbol> Chain<T>(T? first, Func<T, T?> next) where T : class, ISymbol
    {
        for (var current = first; current != null; current = next(current)) yield return current;
    }

    // Using any member of a source type (other than a constant, which is inlined) runs the
    // type's static initialization first: its static constructor, explicit or the one the
    // compiler synthesizes for static field and property initializers.
    private IReadOnlyList<ReachableNode> TypeInitializers(ReachableNode node)
    {
        var symbol = node.Symbol;
        if (symbol is INamedTypeSymbol or IFieldSymbol { IsConst: true }) return Array.Empty<ReachableNode>();
        if (symbol.ContainingType is not { } type || node.ContainingTypeKey is not { } typeKey) return Array.Empty<ReachableNode>();
        if (_typeInitializers.TryGetValue(typeKey, out var cached)) return cached;
        var initializers = type.Locations.Any(l => l.IsInSource)
            ? type.StaticConstructors.Select(NodeFor).ToList()
            : new List<ReachableNode>();
        _typeInitializers[typeKey] = initializers;
        return initializers;
    }

    // The callees of a method body (CallEdgeCollector's edges), bound once per call.
    private async Task<IReadOnlyList<ReachableCallee>> CalleesAsync(ReachableNode node, CancellationToken cancellationToken)
    {
        if (node.Callees != null) return node.Callees;
        var callees = new List<ReachableCallee>();
        var method = (IMethodSymbol)node.Symbol;
        var compilation = await BindingCompilationAsync(method, cancellationToken);
        if (compilation != null)
        {
            var result = CallEdgeCollector.Collect(method, compilation, cancellationToken);
            var seen = new HashSet<(ReachabilityKey, bool)>();
            foreach (var edge in result.Edges)
            {
                var callee = new ReachableCallee(NodeFor(edge.Target), edge.ViaDispatch);
                if (seen.Add((callee.Node.Key, callee.ViaDispatch))) callees.Add(callee);
            }
            if (result.BodyBound) _bodyBindObserver?.Invoke(method);
        }
        node.Callees = callees;
        return callees;
    }

    // The compilation that contains the method's source: its own declaration, or for a
    // compiler-synthesized constructor its type's declaration. Null for a metadata member.
    private async Task<Compilation?> BindingCompilationAsync(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var body = (IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(method);
        var tree = body.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree
                   ?? (body.IsImplicitlyDeclared ? body.ContainingType?.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree : null);
        return tree == null ? null : await CompilationContainingAsync(tree, cancellationToken);
    }

    // The compilation that contains a tree, cached for the call.
    //
    // IMPORTANT: Must be the SAME compilation (GetProjectCompilationAsync, which runs source
    // generators) as the rest of the tool. document.GetSemanticModelAsync() returns the
    // workspace's default model, which on generator-using projects has DIFFERENT symbol
    // identity than our augmented compilation. A generated tree has no document, so its
    // project is found by the compilation that contains it.
    private async Task<Compilation?> CompilationContainingAsync(SyntaxTree tree, CancellationToken cancellationToken)
    {
        if (_compilations.TryGetValue(tree, out var cached)) return cached;
        Compilation? found = null;
        var documentProject = _solution.GetDocument(tree)?.Project;
        IEnumerable<Project> candidates = documentProject != null ? new[] { documentProject } : _solution.Projects;
        foreach (var project in candidates)
        {
            var compilation = await _getCompilation(project, cancellationToken);
            if (compilation != null && compilation.ContainsSyntaxTree(tree))
            {
                found = compilation;
                break;
            }
        }
        _compilations[tree] = found;
        return found;
    }
}

