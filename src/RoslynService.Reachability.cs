using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// find_path_between: bounded bidirectional BFS over the call graph. Answers
// "can A reach B, and through what call chain?" — the point-to-point reachability
// that get_call_graph (single-source) and find_callers/get_outgoing_calls (one hop)
// don't. The tool is a neutral mechanism: the caller names both endpoints and any
// barriers; it has no built-in notion of what matters.
//
// The graph is the one every call tool reads (CallGraphSession): forward edges are a
// member's callees, backward edges its callers, a lambda or local function is part of its
// containing member, and interface or virtual calls reach the implementations the
// receiver type can run. It is over-approximate CONTROL reachability, not data flow (a
// reported path is "could reach", not "will"), blind to reflection, delegates
// dispatched through fields, and DI-by-string, and stops at the metadata boundary (a path
// TO a BCL/NuGet member means the app calls it). Members are keyed by the solution's
// cross-compilation identity, and every frontier is expanded in canonical symbol order, so
// the path reported among equally short ones is the same on every run. maxHops counts
// edges, the unit hopCount reports; with mustPassThrough, maxHops and maxNodes bound the
// whole spliced path.
public partial class RoslynService
{
    private const int MaxPathHops = 12;
    private const int MaxPathNodes = 10000;

    // One resolved endpoint: the (possibly overloaded) methods a MemberRef names.
    private sealed record EndpointSet(string Display, List<IMethodSymbol> Methods);

    // A visited node's link toward its seed: the parent (forward, toward a source) or the
    // child (backward, toward a target). Null at a seed.
    private sealed record PathNode(IMethodSymbol Symbol, IMethodSymbol? Link);

    private sealed record SearchResult(
        bool Reachable, List<IMethodSymbol> Path, bool HitBounds,
        int SourceFrontierSize, int TargetFrontierSize, string? FailedLeg = null);

    public async Task<object> FindPathBetweenAsync(
        List<MemberRef> sources,
        List<MemberRef> targets,
        List<MemberRef>? avoidThrough,
        List<MemberRef>? mustPassThrough,
        int maxHops = 8,
        int maxNodes = 500,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (sources is null || sources.Count == 0 || targets is null || targets.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "sources and targets are both required and must be non-empty",
                hint: "Provide at least one source and one target, each as { typeName, memberName }.");
        }

        if (ParameterValidation.Limit("maxHops", maxHops, MaxPathHops) is { } invalidHops) return CreateErrorResponse(invalidHops);
        if (ParameterValidation.Limit("maxNodes", maxNodes, MaxPathNodes) is { } invalidNodes) return CreateErrorResponse(invalidNodes);

        const int maxPairs = 50;
        if ((long)sources.Count * targets.Count > maxPairs)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Too many endpoint pairs ({sources.Count} x {targets.Count}); the maximum is {maxPairs}",
                hint: "Narrow the source or target set.");
        }

        if (mustPassThrough is { Count: > 1 })
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"mustPassThrough supports a single checkpoint ({mustPassThrough.Count} given)",
                hint: "Routing a path through several required nodes is not supported; check one checkpoint, or compose two calls.");
        }

        var notices = new List<string>();
        var (sourceSets, sErr) = await ResolveEndpointsAsync(sources, notices, cancellationToken);
        if (sErr != null) return sErr;
        var (targetSets, tErr) = await ResolveEndpointsAsync(targets, notices, cancellationToken);
        if (tErr != null) return tErr;

        var barriers = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        if (avoidThrough is { Count: > 0 })
        {
            var (barrierSets, bErr) = await ResolveEndpointsAsync(avoidThrough, notices, cancellationToken);
            if (bErr != null) return bErr;
            foreach (var set in barrierSets) barriers.UnionWith(set.Methods);
        }

        EndpointSet? checkpoint = null;
        if (mustPassThrough is { Count: 1 })
        {
            var (checkpointSets, cErr) = await ResolveEndpointsAsync(mustPassThrough, notices, cancellationToken);
            if (cErr != null) return cErr;
            checkpoint = checkpointSets[0];
        }

        var session = CreateCallGraphSession();
        var pairs = new List<object>();
        var anyBounded = false;
        var reachableCount = 0;

        foreach (var src in sourceSets)
        foreach (var tgt in targetSets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SearchResult result;
            if (checkpoint == null)
            {
                result = await BidirectionalSearchAsync(session, src.Methods, tgt.Methods, barriers, maxHops, maxNodes, cancellationToken);
            }
            else
            {
                // source ⇝ checkpoint ⇝ target: both legs must connect, and the spliced path
                // stays within maxHops edges and maxNodes visited nodes in total. The first
                // leg is a shortest one, so the second leg gets the most budget any path
                // through the checkpoint could leave it.
                var leg1 = await BidirectionalSearchAsync(session, src.Methods, checkpoint.Methods, barriers, maxHops, maxNodes, cancellationToken);
                if (!leg1.Reachable)
                {
                    result = leg1 with { FailedLeg = "sourceToCheckpoint" };
                }
                else
                {
                    var leg2 = await BidirectionalSearchAsync(session, checkpoint.Methods, tgt.Methods, barriers,
                        maxHops - (leg1.Path.Count - 1), maxNodes - (leg1.SourceFrontierSize + leg1.TargetFrontierSize), cancellationToken);
                    result = leg2.Reachable
                        ? new SearchResult(true, leg1.Path.Concat(leg2.Path.Skip(1)).ToList(), leg1.HitBounds || leg2.HitBounds,
                            leg1.SourceFrontierSize, leg2.TargetFrontierSize)
                        : leg2 with { FailedLeg = "checkpointToTarget" };
                }
            }

            if (result.HitBounds) anyBounded = true;
            if (result.Reachable) reachableCount++;

            pairs.Add(BuildPairPayload(src, tgt, result));
        }

        return CreateSuccessResponse(
            data: new
            {
                pairs,
                reachableCount,
                pairCount = pairs.Count,
                notice = notices.Count > 0 ? string.Join(" ", notices) : null
            },
            ResultCounts.Explicit(pairs.Count, pairs.Count, anyBounded),
            suggestedNextTools: reachableCount > 0
                ? new[] { "get_call_graph or get_outgoing_calls to expand a hop on the path" }
                : new[] { "Raise maxHops if hitHopLimit is true, or confirm the endpoints with search_symbols" });
    }

    // Bounded bidirectional BFS between two seed sets. Expands the shallower side each
    // step (callees forward, callers backward) until the frontiers meet, the hop budget
    // (in edges) is spent, the node cap is hit, or both frontiers are exhausted.
    private static async Task<SearchResult> BidirectionalSearchAsync(
        CallGraphSession session, List<IMethodSymbol> sourceSeeds, List<IMethodSymbol> targetSeeds,
        HashSet<ISymbol> barriers, int maxHops, int maxNodes, CancellationToken ct)
    {
        var fwd = new Dictionary<ISymbol, PathNode>(CrossCompilationSymbolComparer.Instance);
        var bwd = new Dictionary<ISymbol, PathNode>(CrossCompilationSymbolComparer.Instance);
        var fwdFrontier = Seed(sourceSeeds, fwd, barriers);
        var bwdFrontier = Seed(targetSeeds, bwd, barriers);

        int Total() => fwd.Count + bwd.Count;

        var meet = FindMeet(fwd, bwd);
        var hitBounds = false;
        int fwdDepth = 0, bwdDepth = 0;

        while (meet == null && (fwdFrontier.Count > 0 || bwdFrontier.Count > 0))
        {
            if (fwdDepth + bwdDepth >= maxHops) { hitBounds = true; break; }
            if (Total() >= maxNodes) { hitBounds = true; break; }

            var expandForward = fwdFrontier.Count > 0 && (bwdFrontier.Count == 0 || fwdDepth <= bwdDepth);
            if (expandForward)
            {
                fwdFrontier = await ExpandFrontierAsync(session, fwdFrontier, fwd, barriers, maxNodes, Total, forward: true, ct);
                fwdDepth++;
            }
            else
            {
                bwdFrontier = await ExpandFrontierAsync(session, bwdFrontier, bwd, barriers, maxNodes, Total, forward: false, ct);
                bwdDepth++;
            }
            meet = FindMeet(fwd, bwd);
        }

        if (meet != null)
        {
            return new SearchResult(true, ReconstructPath(meet, fwd, bwd), hitBounds, fwd.Count, bwd.Count);
        }

        // Unreachable. hitBounds distinguishes "ran out of budget" (raise maxHops/maxNodes)
        // from a genuinely disconnected pair (both frontiers exhausted).
        var moreToExplore = fwdFrontier.Count > 0 || bwdFrontier.Count > 0;
        return new SearchResult(false, new List<IMethodSymbol>(), hitBounds || moreToExplore, fwd.Count, bwd.Count);
    }

    private static List<IMethodSymbol> Seed(List<IMethodSymbol> seeds, Dictionary<ISymbol, PathNode> visited, HashSet<ISymbol> barriers)
    {
        var frontier = new List<IMethodSymbol>();
        foreach (var seed in CanonicalOrder.BySymbol(seeds, s => s))
        {
            if (barriers.Contains(seed)) continue;
            if (visited.TryAdd(seed, new PathNode(seed, null))) frontier.Add(seed);
        }
        return frontier;
    }

    // Expands one frontier one level, registering newly-seen non-barrier neighbours with a
    // link back toward their seed. Stops adding once the combined node cap is reached.
    private static async Task<List<IMethodSymbol>> ExpandFrontierAsync(
        CallGraphSession session, List<IMethodSymbol> frontier, Dictionary<ISymbol, PathNode> visited,
        HashSet<ISymbol> barriers, int maxNodes, Func<int> total, bool forward, CancellationToken ct)
    {
        var next = new List<IMethodSymbol>();
        foreach (var node in frontier)
        {
            ct.ThrowIfCancellationRequested();
            var neighbours = forward
                ? (await session.BodyAsync(node, ct)).Callees.Select(edge => edge.Target)
                : (await session.CallersAsync(node, ct)).Select(site => site.Caller);

            foreach (var neighbour in CanonicalOrder.BySymbol(neighbours.Distinct(CrossCompilationSymbolComparer.Instance), s => s))
            {
                if (barriers.Contains(neighbour) || visited.ContainsKey(neighbour)) continue;
                if (total() >= maxNodes) return next;
                visited[neighbour] = new PathNode((IMethodSymbol)neighbour, node);
                next.Add((IMethodSymbol)neighbour);
            }
        }
        return next;
    }

    // The canonically smallest node present in both frontiers, for deterministic path
    // selection when several meeting points exist at the same depth.
    private static IMethodSymbol? FindMeet(Dictionary<ISymbol, PathNode> fwd, Dictionary<ISymbol, PathNode> bwd)
    {
        var (small, large) = fwd.Count <= bwd.Count ? (fwd, bwd) : (bwd, fwd);
        return CanonicalOrder.BySymbol(small.Values.Select(n => n.Symbol).Where(s => large.ContainsKey(s)), s => s)
            .FirstOrDefault();
    }

    // Source → meet (from the forward links) spliced with meet → target (from the backward
    // links), meet included once.
    private static List<IMethodSymbol> ReconstructPath(
        IMethodSymbol meet, Dictionary<ISymbol, PathNode> fwd, Dictionary<ISymbol, PathNode> bwd)
    {
        var forwardChain = new List<IMethodSymbol>();
        for (IMethodSymbol? node = meet; node != null; node = fwd[node].Link)
            forwardChain.Add(fwd[node].Symbol);
        forwardChain.Reverse(); // source .. meet

        for (var node = bwd[meet].Link; node != null; node = bwd[node].Link)
            forwardChain.Add(bwd[node].Symbol); // meet-child .. target

        return forwardChain;
    }

    private object BuildPairPayload(EndpointSet src, EndpointSet tgt, SearchResult result)
    {
        if (result.Reachable)
        {
            return new
            {
                source = src.Display,
                target = tgt.Display,
                reachable = true,
                // Edges, the unit maxHops bounds: one fewer than the members on the path.
                hopCount = result.Path.Count - 1,
                hops = result.Path.Select(m => new
                {
                    symbol = m.ToDisplayString(),
                    location = GetSymbolLocation(m)
                }).ToList()
            };
        }
        return new
        {
            source = src.Display,
            target = tgt.Display,
            reachable = false,
            hitHopLimit = result.HitBounds,
            sourceFrontierSize = result.SourceFrontierSize,
            targetFrontierSize = result.TargetFrontierSize,
            // With mustPassThrough, the leg that did not connect: "sourceToCheckpoint" or
            // "checkpointToTarget" (its search's frontier sizes are the ones reported).
            failedLeg = result.FailedLeg
        };
    }

    // Resolves each MemberRef to the bodies it names: a method's overloads, a property's or
    // event's accessors, or the type's constructors (memberName ".ctor" or the type's simple
    // name). Collects each type resolution's shadow notice. Returns a structured error
    // (type not found / ambiguous / no such member) on the first that fails to resolve.
    private async Task<(List<EndpointSet> sets, object? error)> ResolveEndpointsAsync(
        List<MemberRef> refs, List<string> notices, CancellationToken ct)
    {
        var sets = new List<EndpointSet>();
        foreach (var reference in refs)
        {
            if (string.IsNullOrWhiteSpace(reference.TypeName) || string.IsNullOrWhiteSpace(reference.MemberName))
            {
                return (sets, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    "Each endpoint must have a non-empty typeName and memberName",
                    context: new { reference.TypeName, reference.MemberName }));
            }

            var resolution = await ResolveTypeByNameAsync(reference.TypeName, ct);
            if (resolution.Error != null) return (sets, resolution.Error);
            if (resolution.Type is not { } type)
            {
                return (sets, CreateErrorResponse(
                    ErrorCodes.TypeNotFound,
                    $"Type not found: {reference.TypeName}",
                    hint: "Use search_symbols to find the correct type name.",
                    context: new { reference.TypeName }));
            }

            var methods = GetMembersByName(type, reference.MemberName, includeInherited: false).Members
                .SelectMany(member => member switch
                {
                    IMethodSymbol method => new[] { method },
                    IPropertySymbol property => new[] { property.GetMethod, property.SetMethod }.OfType<IMethodSymbol>(),
                    IEventSymbol @event => new[] { @event.AddMethod, @event.RemoveMethod }.OfType<IMethodSymbol>(),
                    _ => Enumerable.Empty<IMethodSymbol>()
                })
                .Select(method => (IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(method))
                .Distinct(CrossCompilationSymbolComparer.Instance)
                .Cast<IMethodSymbol>()
                .ToList();
            if (methods.Count == 0)
            {
                return (sets, CreateErrorResponse(
                    ErrorCodes.SymbolNotFound,
                    $"Method '{reference.MemberName}' not found in type '{reference.TypeName}'",
                    hint: "Use get_type_members to list available methods, constructors, properties and events.",
                    context: new { reference.TypeName, reference.MemberName }));
            }

            if (resolution.ShadowNotice is { } notice && !notices.Contains(notice)) notices.Add(notice);
            sets.Add(new EndpointSet($"{type.Name}.{reference.MemberName}", methods));
        }
        return (sets, null);
    }
}
