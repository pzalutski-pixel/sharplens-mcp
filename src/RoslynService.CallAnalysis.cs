using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace SharpLensMcp;

// Call-relationship analysis: who calls a method (find_callers), what a method
// calls (get_outgoing_calls), and the transitive call graph (get_call_graph),
// plus the BFS/cycle-detection helpers they share. Extracted from the broader
// inspection partial so each partial's name predicts its contents.
public partial class RoslynService
{
    public async Task<object> FindCallersAsync(
        string filePath, int line, int column, int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var maxResultsToReturn = maxResults ?? 100; // Default to 100

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        // Find callers works best for methods, properties, and constructors
        if (symbol is not (IMethodSymbol or IPropertySymbol))
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a callable symbol",
                hint: "This tool works for methods, properties, and constructors. Use find_references for other symbol types.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        var callers = await SymbolFinder.FindCallersAsync(symbol, _solution!, cancellationToken);

        // totalCallers counts individual call SITES (every in-source location), matching
        // this tool's one-entry-per-site list. analyze_method reports totalCallerGroups
        // instead — distinct calling symbols — so the two never use the same field name
        // for different units. SymbolFinder now returns call sites inside generated trees
        // too, so the loop is deduped against the generated scan (authoritative for
        // generated callers) by (file, line, column); the scan supplies the flagged
        // entries, each counted once.
        var generatedHits = (await ScanGeneratedReferencesAsync(symbol, cancellationToken))
            .Where(h => h.Kind is "invocation" or "read" or "write")
            .ToList();
        var generatedSites = new HashSet<(string?, int, int)>(
            generatedHits.Select(h => ((string?)h.HintPath, h.Line, h.Column)));

        var totalCallers = 0;
        var callerList = new List<object>();
        foreach (var caller in callers)
        {
            var callingSymbol = caller.CallingSymbol;

            foreach (var location in caller.Locations.Where(loc => loc.IsInSource))
            {
                if (location.SourceTree == null) continue;

                var lineSpan = location.GetLineSpan();
                if (generatedSites.Contains((location.SourceTree.FilePath,
                    lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character)))
                    continue;

                var callerDocument = _solution!.GetDocument(location.SourceTree);
                if (callerDocument == null) continue;

                totalCallers++;
                if (callerList.Count >= maxResultsToReturn) continue;

                var text = location.SourceTree.GetText();
                var lineText = text.Lines[lineSpan.StartLinePosition.Line].ToString().Trim();

                callerList.Add(new
                {
                    callingSymbol = new
                    {
                        name = callingSymbol.Name,
                        kind = callingSymbol.Kind.ToString(),
                        containingType = callingSymbol.ContainingType?.ToDisplayString(),
                        signature = callingSymbol.ToDisplayString()
                    },
                    location = new
                    {
                        filePath = FormatPath(callerDocument.FilePath),
                        line = lineSpan.StartLinePosition.Line,
                        column = lineSpan.StartLinePosition.Character,
                        lineText
                    }
                });
            }
        }

        // Callers inside generator output, surfaced once (deduped against above).
        foreach (var hit in generatedHits)
        {
            totalCallers++;
            if (callerList.Count >= maxResultsToReturn) continue;
            callerList.Add(new
            {
                callingSymbol = new
                {
                    name = hit.EnclosingSymbol?.Name ?? "(generated)",
                    kind = hit.EnclosingSymbol?.Kind.ToString() ?? "Unknown",
                    containingType = hit.EnclosingSymbol?.ContainingType?.ToDisplayString(),
                    signature = hit.EnclosingSymbol?.ToDisplayString() ?? "(generated code)"
                },
                location = new
                {
                    filePath = FormatPath(hit.HintPath),
                    line = hit.Line,
                    column = hit.Column,
                    lineText = hit.LineText
                },
                inGeneratedCode = true
            });
        }

        return CreateSuccessResponse(
            data: new
            {
                symbolName = symbol.Name,
                symbolKind = symbol.Kind.ToString(),
                symbolSignature = symbol.ToDisplayString(),
                callers = callerList
            },
            suggestedNextTools: new[]
            {
                "get_containing_member for caller context",
                "find_references for all usages including non-call references"
            },
            totalCount: totalCallers,
            returnedCount: callerList.Count
        );
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

        var depth = maxDepth ?? 1;
        if (depth <= 0 || depth > 10)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxDepth must be between 1 and 10 (got {depth})",
                hint: "Depths above 10 explode for highly-connected codebases. Use get_call_graph for whole-graph traversal.",
                context: new { maxDepth = depth }
            );
        }

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;
        var semanticModel = pos.Model;

        // Find the nearest method OR local function declaration. Without including
        // LocalFunctionStatementSyntax, a cursor inside a local function walked past
        // it to the enclosing method and analyzed the WRONG member's outgoing calls.
        var methodDecl = pos.Token.Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => n is MethodDeclarationSyntax || n is LocalFunctionStatementSyntax);
        if (methodDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method declaration found at position",
                hint: "Place cursor inside a method or local function body",
                context: new { filePath, line, column }
            );
        }

        var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl, cancellationToken) as IMethodSymbol;
        if (methodSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Could not resolve method symbol",
                context: new { filePath, line, column }
            );
        }

        var calls = new List<object>();
        var recorded = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        // Methods whose bodies have been (or are queued to be) walked, keyed by
        // symbol so a method reached over two paths is expanded once.
        var expanded = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { methodSymbol };
        var truncatedByDepth = false;

        // BFS over method bodies: hop 1 records the direct calls (the default), each
        // further hop walks the bodies of in-source callees discovered one hop earlier.
        var queue = new Queue<(IMethodSymbol method, int hop)>();
        queue.Enqueue((methodSymbol, 1));

        while (queue.Count > 0)
        {
            var (current, hop) = queue.Dequeue();

            foreach (var reference in await CollectOutgoingReferencesAsync(current, includeProperties: true, cancellationToken))
            {
                var symbol = reference.Symbol;
                var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);

                if (recorded.Add(symbol))
                {
                    calls.Add(BuildOutgoingCall(reference, hop));
                }

                // Properties are recorded but never traversed; only in-source methods
                // can be traced deeper.
                if (reference.IsProperty || location == null) continue;
                var callee = (IMethodSymbol)symbol;
                if (hop < depth)
                {
                    if (expanded.Add(callee)) queue.Enqueue((callee, hop + 1));
                }
                // At the depth boundary, only a callee that ITSELF has callees represents
                // a path we truncated — a true leaf loses nothing by not being expanded.
                else if (!expanded.Contains(callee)
                    && (await CollectOutgoingReferencesAsync(callee, includeProperties: false, cancellationToken)).Count > 0)
                {
                    truncatedByDepth = true;
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                method = methodSymbol.ToDisplayString(),
                containingType = methodSymbol.ContainingType?.ToDisplayString(),
                maxDepth = depth,
                truncatedByDepth,
                calls
            },
            suggestedNextTools: new[]
            {
                "get_outgoing_calls on a called method to trace deeper",
                "find_callers to see who calls this method"
            },
            totalCount: calls.Count,
            returnedCount: calls.Count,
            // The calls array is complete for the requested depth; meta.Truncated
            // reflects the depth bound (paths beyond maxDepth were not expanded).
            truncated: truncatedByDepth
        );
    }

    // Transitive call-graph: BFS up to maxDepth, capped at maxNodes, with cycle detection.
    // Replaces the N-round-trip pattern of agents calling find_callers / get_outgoing_calls
    // iteratively to trace an impact chain. Returns nodes + edges (streamable) rather than
    // a nested tree (avoids exponential blowup on diamond-shaped graphs).
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

        if (maxDepth <= 0 || maxDepth > 10)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxDepth must be between 1 and 10 (got {maxDepth})",
                hint: "Depths above 10 explode for highly-connected codebases. Drill iteratively with smaller depths.",
                context: new { maxDepth });
        }

        var dir = direction?.ToLowerInvariant() ?? "callees";
        if (dir != "callees" && dir != "callers" && dir != "both")
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"direction must be 'callees', 'callers', or 'both' (got '{direction}')",
                context: new { direction });
        }

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not IMethodSymbol rootSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a method symbol",
                hint: "Position the cursor on a method declaration or call site.",
                context: new { filePath, line, column });
        }

        // Node table keyed by symbol; assign sequential ids for the wire format.
        var nodeIds = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        var nodes = new List<object>();
        var edges = new List<object>();
        // Typed adjacency for cycle-detection BFS — avoids dynamic dispatch through
        // anonymous-typed edge records (which was both slow and fragile).
        var adjacency = new Dictionary<int, List<int>>();
        // direction=both walks callees AND callers, so the same (from,to) edge is reached
        // from both ends; dedup so each edge is emitted once.
        var seenEdges = new HashSet<(int from, int to)>();
        var cyclesDetected = new HashSet<string>(StringComparer.Ordinal);
        var truncatedByDepth = false;
        var truncatedByNodes = false;

        int RegisterNode(IMethodSymbol sym, int depth)
        {
            if (nodeIds.TryGetValue(sym, out var existing)) return existing;
            var id = nodes.Count;
            nodeIds[sym] = id;
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

        int rootId = RegisterNode(rootSymbol, 0);

        var queue = new Queue<(IMethodSymbol sym, int depth)>();
        queue.Enqueue((rootSymbol, 0));
        // nodeIds is the registry of nodes in the graph: a method is registered
        // exactly once and enqueued only on first registration.

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                // Only flag truncation if this node actually has neighbors we declined
                // to expand; a leaf at the boundary loses nothing by not being walked.
                if (await HasBoundaryNeighborsAsync(current, dir, cancellationToken))
                    truncatedByDepth = true;
                continue;
            }

            var currentId = nodeIds[current];

            // Callees: walk the method body for invocations.
            if (dir == "callees" || dir == "both")
            {
                foreach (var callee in await CollectCalleesAsync(current, cancellationToken))
                {
                    // Already a node: draw the edge (back-edges and cycles included)
                    // regardless of the node cap.
                    if (nodeIds.TryGetValue(callee, out var existingId))
                    {
                        // A cycle only if this edge closes a loop — the callee can
                        // already reach `current`. Test BEFORE adding the edge: the edge
                        // we add here (current -> callee) is otherwise itself the path
                        // that makes the callee reach current, so every revisit to a
                        // shared node on a DAG (a join) would read as a false cycle.
                        if (SymbolEqualityComparer.Default.Equals(callee, current) ||
                            IsAncestor(currentId, existingId, adjacency))
                        {
                            cyclesDetected.Add($"{current.ToDisplayString()} -> {callee.ToDisplayString()}");
                        }
                        AddEdge(edges, adjacency, seenEdges,currentId, existingId);
                        continue;
                    }
                    // New node: register only within the cap; at the cap, flag
                    // truncation and skip.
                    if (nodes.Count >= maxNodes) { truncatedByNodes = true; continue; }
                    var calleeId = RegisterNode(callee, depth + 1);
                    AddEdge(edges, adjacency, seenEdges,currentId, calleeId);
                    queue.Enqueue((callee, depth + 1));
                }
            }

            // Callers: SymbolFinder gives us call sites; map each to the calling method.
            if (dir == "callers" || dir == "both")
            {
                var callers = await SymbolFinder.FindCallersAsync(current, _solution!, cancellationToken);
                foreach (var callerInfo in callers)
                {
                    if (callerInfo.CallingSymbol is not IMethodSymbol callerMethod) continue;
                    if (nodeIds.TryGetValue(callerMethod, out var existingId))
                    {
                        AddEdge(edges, adjacency, seenEdges,existingId, currentId);
                        if (SymbolEqualityComparer.Default.Equals(callerMethod, current) ||
                            IsAncestor(existingId, currentId, adjacency))
                        {
                            cyclesDetected.Add($"{callerMethod.ToDisplayString()} -> {current.ToDisplayString()}");
                        }
                        continue;
                    }
                    if (nodes.Count >= maxNodes) { truncatedByNodes = true; continue; }
                    var callerId = RegisterNode(callerMethod, depth + 1);
                    AddEdge(edges, adjacency, seenEdges,callerId, currentId);
                    queue.Enqueue((callerMethod, depth + 1));
                }

                // Callers inside generator output (invisible to SymbolFinder), the same
                // hits find_callers surfaces. Added as leaf nodes — generator internals
                // are not expanded further.
                foreach (var hit in await ScanGeneratedReferencesAsync(current, cancellationToken))
                {
                    if (hit.Kind is not ("invocation" or "read" or "write")) continue;
                    if (hit.EnclosingSymbol is not IMethodSymbol genCaller) continue;
                    if (nodeIds.TryGetValue(genCaller, out var existingGenId))
                    {
                        AddEdge(edges, adjacency, seenEdges,existingGenId, currentId);
                        continue;
                    }
                    if (nodes.Count >= maxNodes) { truncatedByNodes = true; continue; }
                    var genCallerId = RegisterNode(genCaller, depth + 1);
                    AddEdge(edges, adjacency, seenEdges,genCallerId, currentId);
                }
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
                cyclesDetected = cyclesDetected.ToList()
            },
            suggestedNextTools: new[]
            {
                "get_outgoing_calls or find_callers for single-hop detail at a specific node",
                "analyze_change_impact for the impact of changing this method"
            },
            totalCount: nodes.Count,
            returnedCount: nodes.Count,
            // The node set is complete only up to the depth and node caps; meta.Truncated
            // reflects either bound (data.truncatedByDepth / truncatedByNodes carry which).
            truncated: truncatedByDepth || truncatedByNodes);
    }

    // One method's outgoing reference (a called method or a referenced property),
    // the single shared notion of "a call" across get_outgoing_calls,
    // get_call_graph, and analyze_method.
    internal sealed record OutgoingReference(ISymbol Symbol, bool IsProperty);

    // The canonical "what does this method call" pass, shared so the three
    // call-flow tools agree. Counts ordinary/local-function invocations and —
    // when includeProperties is set — property references in BOTH qualified
    // (`obj.Prop`) and unqualified (`Prop`) form. Dedup is by symbol equality,
    // not display string, so two distinct symbols that render alike stay
    // distinct. Operator/conversion/constructor invocations are intentionally
    // excluded to keep the call set readable.
    private async Task<List<OutgoingReference>> CollectOutgoingReferencesAsync(
        IMethodSymbol method, bool includeProperties, CancellationToken cancellationToken = default)
    {
        var result = new List<OutgoingReference>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            var syntax = await declRef.GetSyntaxAsync(cancellationToken);
            var document = _solution!.GetDocument(declRef.SyntaxTree);
            if (document == null) continue;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null) continue;

            foreach (var node in syntax.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol called
                            && called.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction
                            && seen.Add(called))
                        {
                            result.Add(new OutgoingReference(called, IsProperty: false));
                        }
                        break;
                    // Qualified property reference; the inner name is skipped below.
                    case MemberAccessExpressionSyntax memberAccess when includeProperties:
                        if (!IsInNameofOrAttributeArgument(memberAccess)
                            && semanticModel.GetSymbolInfo(memberAccess, cancellationToken).Symbol is IPropertySymbol qualified
                            && seen.Add(qualified))
                        {
                            result.Add(new OutgoingReference(qualified, IsProperty: true));
                        }
                        break;
                    // Unqualified property reference (`Prop`, not the name part of `obj.Prop`).
                    case IdentifierNameSyntax identifier when includeProperties
                        && (identifier.Parent is not MemberAccessExpressionSyntax parentAccess
                            || parentAccess.Name != identifier):
                        if (!IsInNameofOrAttributeArgument(identifier)
                            && semanticModel.GetSymbolInfo(identifier, cancellationToken).Symbol is IPropertySymbol unqualified
                            && seen.Add(unqualified))
                        {
                            result.Add(new OutgoingReference(unqualified, IsProperty: true));
                        }
                        break;
                }
            }
        }
        return result;
    }

    // The single outgoing-call projection shared by get_outgoing_calls and
    // analyze_method, so the two never drift. depth is the BFS hop for get_outgoing_calls
    // and null for analyze_method's single-hop list.
    private object BuildOutgoingCall(OutgoingReference reference, int? depth)
    {
        var symbol = reference.Symbol;
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var lineSpan = location?.GetLineSpan();
        object? locationObject = lineSpan != null ? new
        {
            filePath = FormatPath(lineSpan.Value.Path),
            line = lineSpan.Value.StartLinePosition.Line,
            column = lineSpan.Value.StartLinePosition.Character
        } : null;

        if (reference.IsProperty)
        {
            var prop = (IPropertySymbol)symbol;
            return new
            {
                method = prop.ToDisplayString(),
                shortName = $"{prop.ContainingType?.Name}.{prop.Name}",
                returnType = prop.Type.ToDisplayString(),
                isAsync = false,
                isProperty = true,
                isExternal = location == null,
                depth,
                location = locationObject
            };
        }
        var calledMethod = (IMethodSymbol)symbol;
        return new
        {
            method = calledMethod.ToDisplayString(),
            shortName = $"{calledMethod.ContainingType?.Name}.{calledMethod.Name}",
            returnType = calledMethod.ReturnType.ToDisplayString(),
            isAsync = calledMethod.IsAsync,
            isExternal = location == null,
            depth,
            location = locationObject
        };
    }

    // A property/identifier inside nameof(...) or an attribute argument is a
    // compile-time reference, not a runtime read — following it as an outgoing call
    // or an exception source is a false positive. Shared by CollectOutgoingReferencesAsync
    // and the get_exception_flow walker so both exclude these contexts identically.
    internal static bool IsInNameofOrAttributeArgument(SyntaxNode node)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case AttributeSyntax:
                    return true;
                case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "nameof" } }:
                    return true;
                // Reached the enclosing statement/body/member without hitting nameof or
                // an attribute: this is an ordinary runtime reference. (Attributes are
                // children of the member declaration, so they are matched above first.)
                case StatementSyntax:
                case MemberDeclarationSyntax:
                    return false;
            }
        }
        return false;
    }

    // The call-graph's traversable callees: methods only, from the shared pass.
    private async Task<List<IMethodSymbol>> CollectCalleesAsync(
        IMethodSymbol method, CancellationToken cancellationToken = default) =>
        (await CollectOutgoingReferencesAsync(method, includeProperties: false, cancellationToken))
            .Select(r => (IMethodSymbol)r.Symbol)
            .ToList();

    // Whether a node at the depth boundary has any neighbor (in the active
    // direction) that would have been expanded with more budget. A true leaf
    // returns false, so the call graph does not over-report depth truncation.
    private async Task<bool> HasBoundaryNeighborsAsync(
        IMethodSymbol method, string direction, CancellationToken cancellationToken)
    {
        if (direction is "callees" or "both"
            && (await CollectCalleesAsync(method, cancellationToken)).Count > 0)
        {
            return true;
        }
        if (direction is "callers" or "both")
        {
            var callers = await SymbolFinder.FindCallersAsync(method, _solution!, cancellationToken);
            if (callers.Any(c => c.CallingSymbol is IMethodSymbol)) return true;
        }
        return false;
    }

    // Records the edge in both the wire-format list (for output) and the typed adjacency
    // map (for in-flight cycle detection). Kept in sync at every edge addition.
    private static void AddEdge(List<object> edges, Dictionary<int, List<int>> adjacency,
        HashSet<(int from, int to)> seen, int from, int to)
    {
        if (!seen.Add((from, to))) return; // already drawn (e.g. reached from both walks)
        edges.Add(new { from, to, kind = "calls" });
        if (!adjacency.TryGetValue(from, out var neighbors))
        {
            neighbors = new List<int>();
            adjacency[from] = neighbors;
        }
        neighbors.Add(to);
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
