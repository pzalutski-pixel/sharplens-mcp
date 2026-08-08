using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace SharpLensMcp;

// find_path_between: bounded bidirectional BFS over the call graph. Answers
// "can A reach B, and through what call chain?" — the point-to-point reachability
// that get_call_graph (single-source) and find_callers/get_outgoing_calls (one hop)
// don't. The tool is a neutral mechanism: the caller names both endpoints and any
// barriers; it has no built-in notion of what matters.
//
// Over-approximate CONTROL reachability, not data flow: virtual/interface dispatch is
// followed (a reported path is "could reach", not "will"), and it is blind to
// reflection, delegates dispatched through fields, and DI-by-string, and stops at the
// metadata boundary (a path TO a BCL/NuGet member means the app calls it).
public partial class RoslynService
{
    // One resolved endpoint: the (possibly overloaded) methods a MemberRef names.
    private sealed record EndpointSet(string Display, List<IMethodSymbol> Methods);

    // A visited node's link toward its seed: parent (forward, toward a source) or
    // child (backward, toward a target). Null at a seed.
    private sealed record PathNode(IMethodSymbol Symbol, string? LinkDocId);

    private sealed record SearchResult(
        bool Reachable, List<IMethodSymbol> Path, bool HitBounds,
        int SourceFrontierSize, int TargetFrontierSize);

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

        if (maxHops < 1 || maxHops > 12)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxHops must be between 1 and 12 (got {maxHops})",
                hint: "Deep reachability searches explode; drill with smaller bounds.",
                context: new { maxHops });
        }

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

        var (sourceSets, sErr) = await ResolveEndpointsAsync(sources, cancellationToken);
        if (sErr != null) return sErr;
        var (targetSets, tErr) = await ResolveEndpointsAsync(targets, cancellationToken);
        if (tErr != null) return tErr;

        var barrierIds = new HashSet<string>(StringComparer.Ordinal);
        if (avoidThrough is { Count: > 0 })
        {
            var (barrierSets, bErr) = await ResolveEndpointsAsync(avoidThrough, cancellationToken);
            if (bErr != null) return bErr;
            foreach (var set in barrierSets)
                foreach (var m in set.Methods)
                    barrierIds.Add(DocId(m));
        }

        EndpointSet? checkpoint = null;
        if (mustPassThrough is { Count: 1 })
        {
            var (checkpointSets, cErr) = await ResolveEndpointsAsync(mustPassThrough, cancellationToken);
            if (cErr != null) return cErr;
            checkpoint = checkpointSets[0];
        }

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
                result = await BidirectionalSearchAsync(src.Methods, tgt.Methods, barrierIds, maxHops, maxNodes, cancellationToken);
            }
            else
            {
                // source ⇝ checkpoint ⇝ target: both legs must connect, and the checkpoint
                // itself must not be a barrier. Splice the two shortest legs.
                var leg1 = await BidirectionalSearchAsync(src.Methods, checkpoint.Methods, barrierIds, maxHops, maxNodes, cancellationToken);
                var leg2 = leg1.Reachable
                    ? await BidirectionalSearchAsync(checkpoint.Methods, tgt.Methods, barrierIds, maxHops, maxNodes, cancellationToken)
                    : new SearchResult(false, new List<IMethodSymbol>(), leg1.HitBounds, leg1.SourceFrontierSize, 0);
                result = SpliceLegs(leg1, leg2);
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
                pairCount = pairs.Count
            },
            suggestedNextTools: reachableCount > 0
                ? new[] { "get_call_graph or get_outgoing_calls to expand a hop on the path" }
                : new[] { "Raise maxHops if hitHopLimit is true, or confirm the endpoints with search_symbols" },
            totalCount: pairs.Count,
            returnedCount: pairs.Count,
            truncated: anyBounded);
    }

    // Bounded bidirectional BFS between two seed sets. Expands the shallower side each
    // step (callees + dispatch forward, callers + reverse dispatch backward) until the
    // frontiers meet, the hop budget is spent, the node cap is hit, or both frontiers
    // are exhausted.
    private async Task<SearchResult> BidirectionalSearchAsync(
        List<IMethodSymbol> sourceSeeds, List<IMethodSymbol> targetSeeds,
        HashSet<string> barrierIds, int maxHops, int maxNodes, CancellationToken ct)
    {
        var fwd = new Dictionary<string, PathNode>(StringComparer.Ordinal);
        var bwd = new Dictionary<string, PathNode>(StringComparer.Ordinal);
        var fwdFrontier = new List<IMethodSymbol>();
        var bwdFrontier = new List<IMethodSymbol>();

        foreach (var m in sourceSeeds)
        {
            var id = DocId(m);
            if (barrierIds.Contains(id)) continue;
            if (fwd.TryAdd(id, new PathNode(m, null))) fwdFrontier.Add(m);
        }
        foreach (var m in targetSeeds)
        {
            var id = DocId(m);
            if (barrierIds.Contains(id)) continue;
            if (bwd.TryAdd(id, new PathNode(m, null))) bwdFrontier.Add(m);
        }

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
                fwdFrontier = await ExpandFrontierAsync(fwdFrontier, fwd, barrierIds, maxNodes, Total, forward: true, ct);
                fwdDepth++;
            }
            else
            {
                bwdFrontier = await ExpandFrontierAsync(bwdFrontier, bwd, barrierIds, maxNodes, Total, forward: false, ct);
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

    // Expands one frontier one level, registering newly-seen non-barrier neighbours with a
    // link back toward their seed. Stops adding once the combined node cap is reached.
    private async Task<List<IMethodSymbol>> ExpandFrontierAsync(
        List<IMethodSymbol> frontier, Dictionary<string, PathNode> parent,
        HashSet<string> barrierIds, int maxNodes, Func<int> total, bool forward, CancellationToken ct)
    {
        var next = new List<IMethodSymbol>();
        foreach (var node in frontier)
        {
            ct.ThrowIfCancellationRequested();
            var neighbours = forward
                ? await ExpandCalleesWithDispatchAsync(node, ct)
                : await ExpandCallersWithDispatchAsync(node, ct);

            foreach (var neighbour in neighbours)
            {
                var id = DocId(neighbour);
                if (barrierIds.Contains(id)) continue;
                if (parent.ContainsKey(id)) continue;
                if (total() >= maxNodes) return next;
                parent[id] = new PathNode(neighbour, DocId(node));
                next.Add(neighbour);
            }
        }
        return next;
    }

    // Forward edges: the method's callees, plus — when a callee is dispatched through an
    // interface/virtual member — its implementations/overrides (over-approximate).
    private async Task<List<IMethodSymbol>> ExpandCalleesWithDispatchAsync(IMethodSymbol method, CancellationToken ct)
    {
        var result = new List<IMethodSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var callee in await CollectCalleesAsync(method, ct))
        {
            if (seen.Add(DocId(callee))) result.Add(callee);
            foreach (var impl in await FindDispatchTargetsAsync(callee, ct))
                if (seen.Add(DocId(impl))) result.Add(impl);
        }
        return result;
    }

    // Backward edges: the method's callers (resolved via FindReferencesAsync — solution-wide,
    // so cross-project callers are not lost the way SymbolFinder.FindCallersAsync loses them,
    // Roslyn issue #41616). Also searches callers of any interface/base member this method
    // satisfies, so a call through the interface reaches this override.
    private async Task<List<IMethodSymbol>> ExpandCallersWithDispatchAsync(IMethodSymbol method, CancellationToken ct)
    {
        var result = new List<IMethodSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var searchSymbols = new List<IMethodSymbol> { method };
        searchSymbols.AddRange(GetSatisfiedMembers(method));

        foreach (var symbol in searchSymbols)
        {
            var references = await SymbolFinder.FindReferencesAsync(symbol, _solution!, ct);
            foreach (var referenced in references)
            {
                foreach (var loc in referenced.Locations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!loc.Location.IsInSource || loc.Location.SourceTree == null) continue;

                    var tree = loc.Location.SourceTree;
                    var root = await tree.GetRootAsync(ct);
                    var node = root.FindNode(loc.Location.SourceSpan);
                    if (ClassifyReferenceKind(node) != "invocation") continue;

                    var document = _solution!.GetDocument(tree);
                    if (document == null) continue;
                    var semanticModel = await document.GetSemanticModelAsync(ct);
                    if (semanticModel == null) continue;

                    var enclosing = semanticModel.GetEnclosingSymbol(loc.Location.SourceSpan.Start, ct);
                    for (var s = enclosing; s != null; s = s.ContainingSymbol)
                    {
                        if (s is IMethodSymbol caller && caller.Locations.Any(l => l.IsInSource))
                        {
                            if (seen.Add(DocId(caller))) result.Add(caller);
                            break;
                        }
                    }
                }
            }
        }
        return result;
    }

    // Implementations of an interface member, or overrides of a virtual/abstract method.
    private async Task<IEnumerable<IMethodSymbol>> FindDispatchTargetsAsync(IMethodSymbol method, CancellationToken ct)
    {
        if (method.ContainingType?.TypeKind == TypeKind.Interface)
            return (await SymbolFinder.FindImplementationsAsync(method, _solution!, cancellationToken: ct)).OfType<IMethodSymbol>();
        if (method.IsVirtual || method.IsAbstract || method.IsOverride)
            return (await SymbolFinder.FindOverridesAsync(method, _solution!, cancellationToken: ct)).OfType<IMethodSymbol>();
        return Array.Empty<IMethodSymbol>();
    }

    // The interface members and overridden base members this method satisfies — a call
    // through any of them dispatches to this method.
    private static IEnumerable<IMethodSymbol> GetSatisfiedMembers(IMethodSymbol method)
    {
        for (var b = method.OverriddenMethod; b != null; b = b.OverriddenMethod)
            yield return b;

        var type = method.ContainingType;
        if (type == null) yield break;
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(member), method))
                    yield return member;
            }
        }
    }

    // The lexicographically smallest node present in both frontiers, for deterministic
    // path selection when several meeting points exist at the same depth.
    private static string? FindMeet(Dictionary<string, PathNode> fwd, Dictionary<string, PathNode> bwd)
    {
        var (small, large) = fwd.Count <= bwd.Count ? (fwd, bwd) : (bwd, fwd);
        string? best = null;
        foreach (var id in small.Keys)
        {
            if (large.ContainsKey(id) && (best == null || string.CompareOrdinal(id, best) < 0))
                best = id;
        }
        return best;
    }

    // Source → meet (from the forward links) spliced with meet → target (from the backward
    // links), meet included once.
    private static List<IMethodSymbol> ReconstructPath(
        string meet, Dictionary<string, PathNode> fwd, Dictionary<string, PathNode> bwd)
    {
        var forwardChain = new List<IMethodSymbol>();
        for (string? id = meet; id != null; id = fwd[id].LinkDocId)
            forwardChain.Add(fwd[id].Symbol);
        forwardChain.Reverse(); // source .. meet

        for (string? id = bwd[meet].LinkDocId; id != null; id = bwd[id].LinkDocId)
            forwardChain.Add(bwd[id].Symbol); // meet-child .. target

        return forwardChain;
    }

    // Joins source⇝checkpoint and checkpoint⇝target into one result, dropping the duplicate
    // checkpoint node. Unreachable if either leg failed.
    private static SearchResult SpliceLegs(SearchResult leg1, SearchResult leg2)
    {
        if (!leg1.Reachable || !leg2.Reachable)
        {
            return new SearchResult(false, new List<IMethodSymbol>(),
                leg1.HitBounds || leg2.HitBounds, leg1.SourceFrontierSize, leg2.TargetFrontierSize);
        }
        var path = new List<IMethodSymbol>(leg1.Path);
        path.AddRange(leg2.Path.Skip(1)); // leg2 starts at the checkpoint, already the tail of leg1
        return new SearchResult(true, path, leg1.HitBounds || leg2.HitBounds, leg1.SourceFrontierSize, leg2.TargetFrontierSize);
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
                hopCount = result.Path.Count,
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
            targetFrontierSize = result.TargetFrontierSize
        };
    }

    // Resolves each MemberRef to its method overloads. Returns a structured error (type not
    // found / ambiguous / no such member) on the first that fails to resolve.
    private async Task<(List<EndpointSet> sets, object? error)> ResolveEndpointsAsync(
        List<MemberRef> refs, CancellationToken ct)
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

            var (type, ambiguous) = await FindTypeByNameAsync(reference.TypeName, ct);
            if (ambiguous != null) return (sets, ambiguous);
            if (type == null)
            {
                return (sets, CreateErrorResponse(
                    ErrorCodes.TypeNotFound,
                    $"Type not found: {reference.TypeName}",
                    hint: "Use search_symbols to find the correct type name.",
                    context: new { reference.TypeName }));
            }

            var methods = type.GetMembers(reference.MemberName)
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind is MethodKind.Ordinary)
                .ToList();
            if (methods.Count == 0)
            {
                return (sets, CreateErrorResponse(
                    ErrorCodes.SymbolNotFound,
                    $"Method '{reference.MemberName}' not found in type '{reference.TypeName}'",
                    hint: "Use get_type_members to list available methods.",
                    context: new { reference.TypeName, reference.MemberName }));
            }

            sets.Add(new EndpointSet($"{type.Name}.{reference.MemberName}", methods));
        }
        return (sets, null);
    }

    // Cross-compilation-stable identity: the documentation-comment id of the original
    // definition. Robust across the base/augmented(generator) compilation boundary where
    // SymbolEqualityComparer.Default can diverge; falls back to the display string.
    private static string DocId(ISymbol symbol) =>
        symbol.OriginalDefinition.GetDocumentationCommentId() ?? symbol.OriginalDefinition.ToDisplayString();
}
