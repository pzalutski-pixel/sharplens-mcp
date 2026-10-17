using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// Call-relationship analysis: who calls a method (find_callers), what a method calls
// (get_outgoing_calls, and analyze_method's outgoing list), and the transitive call graph
// (get_call_graph). All of them read one call model, CallGraphSession over
// CallEdgeCollector, so they agree on what a call is: every call the compiler emits
// (extension and generic calls as one edge per declaration, constructors, accessors,
// operators, partial implementations), lambdas and local functions as part of their
// containing member, and interface or virtual calls fanned out to the implementations the
// receiver type can reach, flagged viaDispatch.
public partial class RoslynService
{
    private const int MaxCallersResults = 1000;
    private const int MaxCallDepth = 10;
    private const int MaxCallGraphNodes = 10000;

    private static readonly string[] CallGraphDirections = { "callees", "callers", "both" };

    // The call model of one tool call.
    private CallGraphSession CreateCallGraphSession() =>
        new(_solution!, GetProjectCompilationAsync, GetLogicalProjects());

    // The wire name of a call kind: its CallEdgeKind name in camelCase (invocation,
    // objectCreation, propertyGet, ...).
    internal static string CallKindName(CallEdgeKind kind)
    {
        var name = kind.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    public async Task<object> FindCallersAsync(
        string filePath, int line, int column, int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxCallersResults) is { } invalid) return CreateErrorResponse(invalid);
        var maxResultsToReturn = maxResults ?? 100;

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        // A property is called through its accessors.
        var targets = symbol switch
        {
            IMethodSymbol method => new[] { method },
            IPropertySymbol property => new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>().ToArray(),
            _ => null
        };
        if (targets == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a callable symbol",
                hint: "This tool works for methods, constructors, accessors, operators and properties. Use find_references for other symbol types.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        var session = CreateCallGraphSession();
        var sites = new List<CallerSite>();
        foreach (var target in targets) sites.AddRange(await session.CallersAsync(target, cancellationToken));

        // One entry per call site, in canonical location order; the cap keeps the first.
        var entries = CanonicalOrder.ByLocation(
            sites.Select(site => (site, location: SiteResultLocation(site.Edge.Site))),
            entry => entry.location);
        var callerList = new List<object>();
        foreach (var (site, location) in entries.Take(maxResultsToReturn))
        {
            var caller = site.Caller;
            var tree = site.Edge.Site.SourceTree!;
            callerList.Add(new
            {
                callingSymbol = new
                {
                    name = caller.Name,
                    kind = caller.Kind.ToString(),
                    containingType = caller.ContainingType?.ToDisplayString(),
                    signature = caller.ToDisplayString()
                },
                location = new
                {
                    filePath = location.FilePath,
                    line = location.Line,
                    column = location.Column,
                    lineText = tree.GetText(cancellationToken).Lines[location.Line].ToString().Trim()
                },
                kind = CallKindName(site.Edge.Kind),
                viaDispatch = site.Edge.ViaDispatch,
                inGeneratedCode = !IsSolutionSourceTree(tree)
            });
        }

        // totalCount counts individual call SITES (every in-source location), matching this
        // tool's one-entry-per-site list. analyze_method reports totalCallerGroups instead —
        // distinct calling symbols — so the two never use the same field name for different
        // units.
        return CreateSuccessResponse(
            data: new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                symbolSignature = symbol.ToDisplayString(),
                callers = callerList
            },
            ResultCounts.Capped(entries.Count, callerList.Count),
            suggestedNextTools: new[]
            {
                "get_containing_member for caller context",
                "find_references for all usages including non-call references"
            });
    }

    // A call site as a location result, with the path as the tools report it.
    private ResultLocation SiteResultLocation(Location site)
    {
        var span = site.GetLineSpan();
        return new ResultLocation(FormatPath(span.Path), span.StartLinePosition.Line, span.StartLinePosition.Character);
    }

    /// <summary>
    /// Gets all methods called by a given method, optionally tracing transitively
    /// into in-source callees up to maxDepth hops.
    /// </summary>
    public async Task<object> GetOutgoingCallsAsync(
        string filePath, int line, int column, int? maxDepth = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxDepth", maxDepth, MaxCallDepth) is { } invalid) return CreateErrorResponse(invalid);
        var depth = maxDepth ?? 1;

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        var (methodSymbol, memberError) = MemberAtPosition(pos, filePath, line, column, cancellationToken);
        if (methodSymbol == null) return memberError!;

        var session = CreateCallGraphSession();
        var (calls, truncatedByDepth) = await CollectOutgoingCallsAsync(session, methodSymbol, depth, cancellationToken);

        return CreateSuccessResponse(
            data: new
            {
                method = methodSymbol.ToDisplayString(),
                containingType = methodSymbol.ContainingType?.ToDisplayString(),
                maxDepth = depth,
                truncatedByDepth,
                calls = calls.Select(BuildOutgoingCall).ToList()
            },
            // The calls array is complete for the requested depth; truncated reflects the
            // depth bound (paths beyond maxDepth were not expanded).
            ResultCounts.Bounded(calls.Count, truncatedByDepth),
            suggestedNextTools: new[]
            {
                "get_outgoing_calls on a called method to trace deeper",
                "find_callers to see who calls this method"
            });
    }

    // The member whose body a position addresses: the nearest enclosing method,
    // constructor, finalizer, operator, conversion, accessor or local function (a lambda
    // belongs to its containing member); a property's expression body or a property with a
    // single accessor addresses that accessor.
    private (IMethodSymbol? method, object? error) MemberAtPosition(
        ResolvedPosition pos, string filePath, int line, int column, CancellationToken cancellationToken)
    {
        var declaration = pos.Token.Parent?.AncestorsAndSelf().FirstOrDefault(n => n
            is LocalFunctionStatementSyntax
            or AccessorDeclarationSyntax
            or BaseMethodDeclarationSyntax
            or ArrowExpressionClauseSyntax { Parent: BasePropertyDeclarationSyntax }
            or BasePropertyDeclarationSyntax);

        var symbol = declaration switch
        {
            ArrowExpressionClauseSyntax { Parent: BasePropertyDeclarationSyntax property } =>
                (pos.Model.GetDeclaredSymbol(property, cancellationToken) as IPropertySymbol)?.GetMethod,
            BasePropertyDeclarationSyntax property =>
                pos.Model.GetDeclaredSymbol(property, cancellationToken) switch
                {
                    IPropertySymbol { GetMethod: { } getter, SetMethod: null } => getter,
                    IPropertySymbol { GetMethod: null, SetMethod: { } setter } => setter,
                    _ => null
                },
            null => null,
            _ => pos.Model.GetDeclaredSymbol(declaration, cancellationToken) as IMethodSymbol
        };

        if (symbol != null) return (symbol, null);
        return (null, CreateErrorResponse(
            ErrorCodes.NotAMethod,
            declaration is BasePropertyDeclarationSyntax
                ? "The position is on a property with several accessors"
                : "No method declaration found at position",
            hint: "Place the cursor inside a method, constructor, finalizer, operator, accessor or local function body",
            context: new { filePath, line, column }));
    }

    // What `root` calls, breadth-first to maxDepth hops: each callee once, at the first hop
    // it is found, with the first call site that reaches it (a direct call preferred over a
    // dispatched one at the same hop). A hop's entries are in call-site location order, a
    // call's dispatch targets after the member it is bound to. Only callees with a body in
    // the solution are expanded. truncatedByDepth: a callee at the last hop calls a member
    // not yet listed.
    private static async Task<(List<OutgoingCall> calls, bool truncatedByDepth)> CollectOutgoingCallsAsync(
        CallGraphSession session, IMethodSymbol root, int maxDepth, CancellationToken cancellationToken)
    {
        var calls = new List<OutgoingCall>();
        var recorded = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        var expanded = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance) { root };
        var frontier = new List<IMethodSymbol> { root };

        for (var hop = 1; hop <= maxDepth && frontier.Count > 0; hop++)
        {
            var discovered = new List<OutgoingCall>();
            foreach (var node in frontier)
            {
                foreach (var edge in (await session.BodyAsync(node, cancellationToken)).Callees)
                {
                    discovered.Add(new OutgoingCall(edge.Target, edge.Kind, edge.ViaDispatch, edge.Site, hop));
                }
            }

            var ordered = OrderOutgoing(discovered);
            var added = new List<OutgoingCall>();
            foreach (var call in ordered.Where(c => !c.ViaDispatch).Concat(ordered.Where(c => c.ViaDispatch)))
            {
                if (recorded.Add(call.Target)) added.Add(call);
            }

            frontier = new List<IMethodSymbol>();
            foreach (var call in OrderOutgoing(added))
            {
                calls.Add(call);
                if (CallGraphSession.HasSourceBody(call.Target) && expanded.Add(call.Target)) frontier.Add(call.Target);
            }
        }

        var truncatedByDepth = false;
        foreach (var node in frontier)
        {
            if ((await session.BodyAsync(node, cancellationToken)).Callees.Any(edge => !recorded.Contains(edge.Target)))
            {
                truncatedByDepth = true;
                break;
            }
        }
        return (calls, truncatedByDepth);
    }

    private static List<OutgoingCall> OrderOutgoing(IEnumerable<OutgoingCall> calls) => calls
        .OrderBy(c => ResultLocation.From(c.Site) ?? new ResultLocation("", 0, 0), ResultLocationComparer.Instance)
        .ThenBy(c => c.ViaDispatch)
        .ThenBy(c => SymbolOrderKey.Of(c.Target))
        .ToList();

    // analyze_method's outgoing list: the direct calls, in get_outgoing_calls' projection.
    internal async Task<List<object>> CollectDirectOutgoingCallsAsync(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var (calls, _) = await CollectOutgoingCallsAsync(CreateCallGraphSession(), method, 1, cancellationToken);
        return calls.Select(call => BuildOutgoingCall(call with { Depth = 0 })).ToList();
    }

    // The single outgoing-call projection shared by get_outgoing_calls and analyze_method,
    // so the two never drift. depth is the hop for get_outgoing_calls, null for
    // analyze_method's single-hop list (Depth 0).
    private object BuildOutgoingCall(OutgoingCall call)
    {
        var target = call.Target;
        var location = target.Locations.FirstOrDefault(l => l.IsInSource);
        var lineSpan = location?.GetLineSpan();
        return new
        {
            method = target.ToDisplayString(),
            shortName = ShortName(target),
            returnType = target.ReturnType.ToDisplayString(),
            isAsync = target.IsAsync,
            isExternal = location == null,
            kind = CallKindName(call.Kind),
            viaDispatch = call.ViaDispatch,
            depth = call.Depth == 0 ? (int?)null : call.Depth,
            location = lineSpan != null
                ? new
                {
                    filePath = FormatPath(lineSpan.Value.Path),
                    line = lineSpan.Value.StartLinePosition.Line,
                    column = lineSpan.Value.StartLinePosition.Character
                }
                : null
        };
    }

    // A callee's name with its type's simple name, as C# spells the member: Type.Method,
    // Type.Property.get / .set, Type.Event.add / .remove, Type.Type for a constructor,
    // Type.~Type for a finalizer, Type.operator + for an operator.
    private static string ShortName(IMethodSymbol method)
    {
        var typeName = method.ContainingType?.Name;
        var memberName = method switch
        {
            { AssociatedSymbol: { } associated } => method.MethodKind switch
            {
                MethodKind.PropertyGet => $"{associated.Name}.get",
                MethodKind.PropertySet => $"{associated.Name}.set",
                MethodKind.EventAdd => $"{associated.Name}.add",
                MethodKind.EventRemove => $"{associated.Name}.remove",
                _ => method.Name
            },
            { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => typeName,
            { MethodKind: MethodKind.Destructor } => $"~{typeName}",
            { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } =>
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Split('(')[0],
            _ => method.Name
        };
        return $"{typeName}.{memberName}";
    }

    // Transitive call graph: a breadth-first walk to maxDepth, capped at maxNodes, with cycle
    // detection. Replaces the N-round-trip pattern of agents calling find_callers /
    // get_outgoing_calls iteratively to trace an impact chain. Returns nodes + edges
    // (streamable) rather than a nested tree (avoids exponential blowup on diamond-shaped
    // graphs). direction "both" is the callers tree of the root united with its callees
    // tree: a node reached by the callee walk is expanded only for its callees, one reached
    // by the caller walk only for its callers. The walk is level by level; the members a
    // level discovers are registered in canonical symbol order, so node ids and the set kept
    // at the node cap are the same on every run.
    public async Task<object> GetCallGraphAsync(
        string filePath,
        int line,
        int column,
        string direction = "callees",
        int maxDepth = 3,
        int maxNodes = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxDepth", maxDepth, MaxCallDepth) is { } invalidDepth) return CreateErrorResponse(invalidDepth);
        if (ParameterValidation.Limit("maxNodes", maxNodes, MaxCallGraphNodes) is { } invalidNodes) return CreateErrorResponse(invalidNodes);
        if (ParameterValidation.Choice("direction", direction ?? "callees", CallGraphDirections, out var chosenDirection) is { } invalidDirection)
        {
            return CreateErrorResponse(invalidDirection);
        }
        var dir = chosenDirection!;

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not IMethodSymbol rootMethod)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a method symbol",
                hint: "Position the cursor on a method, constructor, accessor or operator declaration, or a call site.",
                context: new { filePath, line, column });
        }
        var rootSymbol = (IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(rootMethod);

        var session = CreateCallGraphSession();
        var nodeIds = new Dictionary<ISymbol, int>(CrossCompilationSymbolComparer.Instance);
        var nodeSymbols = new List<IMethodSymbol>();
        var nodes = new List<object>();
        var edges = new List<object>();
        // Typed adjacency for cycle detection.
        var adjacency = new Dictionary<int, List<int>>();
        // The same (from, to) pair can be reached from both walks; each edge is emitted once.
        var seenEdges = new HashSet<(int from, int to)>();
        var cyclesDetected = new List<string>();
        var cycleSet = new HashSet<string>(StringComparer.Ordinal);
        var truncatedByNodes = false;

        int RegisterNode(IMethodSymbol sym, int depth)
        {
            var id = nodes.Count;
            nodeIds[sym] = id;
            nodeSymbols.Add(sym);
            nodes.Add(new
            {
                id,
                fullName = sym.ToDisplayString(),
                kind = sym.MethodKind.ToString(),
                location = GetSymbolLocation(sym),
                depth
            });
            return id;
        }

        void Connect(int from, int to, CallEdgeKind kind, bool viaDispatch)
        {
            if (!seenEdges.Add((from, to))) return;
            // A cycle only if this edge closes a loop: `to` can already reach `from`. Tested
            // BEFORE adding the edge, which would otherwise itself be the path that makes `to`
            // reach `from`, reading every join of a DAG as a false cycle.
            if (from == to || IsAncestor(from, to, adjacency))
            {
                var cycle = $"{nodeSymbols[from].ToDisplayString()} -> {nodeSymbols[to].ToDisplayString()}";
                if (cycleSet.Add(cycle)) cyclesDetected.Add(cycle);
            }
            edges.Add(new { from, to, kind = CallKindName(kind), viaDispatch });
            if (!adjacency.TryGetValue(from, out var neighbours)) adjacency[from] = neighbours = new List<int>();
            neighbours.Add(to);
        }

        // One walk level: every (from, to) link the frontier produces, the new members
        // registered in canonical order within the cap, then the links drawn. Returns the
        // newly registered members, the next level's frontier.
        async Task<List<IMethodSymbol>> ExpandAsync(
            List<IMethodSymbol> frontier, bool callees, int depth)
        {
            var links = new List<(IMethodSymbol from, IMethodSymbol to, CallEdgeKind kind, bool viaDispatch)>();
            foreach (var node in frontier)
            {
                foreach (var (neighbour, kind, viaDispatch) in await NeighboursAsync(session, node, callees, cancellationToken))
                {
                    links.Add(callees ? (node, neighbour, kind, viaDispatch) : (neighbour, node, kind, viaDispatch));
                }
            }

            var discovered = links
                .Select(link => callees ? link.to : link.from)
                .Where(member => !nodeIds.ContainsKey(member))
                .Distinct(CrossCompilationSymbolComparer.Instance)
                .Cast<IMethodSymbol>();
            var registered = new List<IMethodSymbol>();
            foreach (var member in CanonicalOrder.BySymbol(discovered, m => m))
            {
                if (nodes.Count >= maxNodes)
                {
                    truncatedByNodes = true;
                    break;
                }
                RegisterNode(member, depth + 1);
                registered.Add(member);
            }

            foreach (var (from, to, kind, viaDispatch) in links)
            {
                if (nodeIds.TryGetValue(from, out var fromId) && nodeIds.TryGetValue(to, out var toId))
                {
                    Connect(fromId, toId, kind, viaDispatch);
                }
            }
            return registered;
        }

        var rootId = RegisterNode(rootSymbol, 0);
        var calleeFrontier = dir is "callees" or "both" ? new List<IMethodSymbol> { rootSymbol } : new List<IMethodSymbol>();
        var callerFrontier = dir is "callers" or "both" ? new List<IMethodSymbol> { rootSymbol } : new List<IMethodSymbol>();
        for (var depth = 0; depth < maxDepth && (calleeFrontier.Count > 0 || callerFrontier.Count > 0); depth++)
        {
            var nextCallees = await ExpandAsync(calleeFrontier, callees: true, depth);
            var nextCallers = await ExpandAsync(callerFrontier, callees: false, depth);
            calleeFrontier = nextCallees;
            callerFrontier = nextCallers;
        }

        // Depth truncation: a node at the depth boundary that has a neighbour in its walk's
        // direction (an edge the walk did not draw); a leaf there loses nothing.
        var truncatedByDepth = false;
        foreach (var (frontier, callees) in new[] { (calleeFrontier, true), (callerFrontier, false) })
        {
            foreach (var node in frontier)
            {
                if ((await NeighboursAsync(session, node, callees, cancellationToken)).Count > 0) truncatedByDepth = true;
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                root = new
                {
                    id = rootId,
                    fullName = rootSymbol.ToDisplayString(),
                    kind = rootSymbol.MethodKind.ToString(),
                    location = GetSymbolLocation(rootSymbol)
                },
                direction = dir,
                maxDepth,
                nodes,
                edges,
                truncatedByDepth,
                truncatedByNodes,
                cyclesDetected
            },
            // The node set is complete only up to the depth and node caps; truncated reflects
            // either bound (data.truncatedByDepth / truncatedByNodes carry which).
            ResultCounts.Bounded(nodes.Count, truncatedByDepth || truncatedByNodes),
            suggestedNextTools: new[]
            {
                "get_outgoing_calls or find_callers for single-hop detail at a specific node",
                "analyze_change_impact for the impact of changing this method"
            });
    }

    // A member's neighbours in one direction, each once: its callees, or the members calling
    // it. Direct edges come before dispatched ones, each in call-site order, so a neighbour
    // reached both directly and through dispatch keeps the direct edge.
    private static async Task<List<(IMethodSymbol neighbour, CallEdgeKind kind, bool viaDispatch)>> NeighboursAsync(
        CallGraphSession session, IMethodSymbol member, bool callees, CancellationToken cancellationToken)
    {
        var edges = callees
            ? (await session.BodyAsync(member, cancellationToken)).Callees.Select(e => (neighbour: e.Target, edge: e))
            : (await session.CallersAsync(member, cancellationToken)).Select(s => (neighbour: s.Caller, edge: s.Edge));
        var ordered = edges
            .OrderBy(e => e.edge.ViaDispatch)
            .ThenBy(e => ResultLocation.From(e.edge.Site) ?? new ResultLocation("", 0, 0), ResultLocationComparer.Instance)
            .ToList();
        var seen = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        return ordered
            .Where(e => seen.Add(e.neighbour))
            .Select(e => (e.neighbour, e.edge.Kind, e.edge.ViaDispatch))
            .ToList();
    }

    // Detects whether `candidateId` is reachable from `targetId` via existing edges
    // (in-flight graph). Used to record true cycles (A->B->A) rather than back-edges
    // to siblings. Typed adjacency: O(V+E) BFS, no boxing.
    private static bool IsAncestor(int candidateId, int targetId, Dictionary<int, List<int>> adjacency)
    {
        if (candidateId == targetId) return true;
        var seen = new HashSet<int> { targetId };
        var queue = new Queue<int>();
        queue.Enqueue(targetId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!adjacency.TryGetValue(cur, out var neighbors)) continue;
            foreach (var to in neighbors)
            {
                if (to == candidateId) return true;
                if (seen.Add(to)) queue.Enqueue(to);
            }
        }
        return false;
    }
}
