using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// find_tests: which tests cover a symbol — the inverse of find_untested_code.
// Uses the same entry-point detection and reachability graph, but walks from each
// entry point separately and answers which ones reach the target. Audit-time tool:
// one solution sweep per call, no caching across calls (same caveat as
// find_untested_code).
public partial class RoslynService
{
    // Test seam: observes each method body the coverage walk binds to collect its
    // callees, so tests can lock how often a call binds a body. Null in production.
    private Action<IMethodSymbol>? _coverageBodyBindObserver;

    internal void ObserveCoverageBodyBindsForTesting(Action<IMethodSymbol> observer) =>
        _coverageBodyBindObserver = observer;

    // The per-call reachability graph both coverage tools walk.
    private TestReachabilityGraph CreateCoverageGraph() =>
        new(_solution!, GetProjectCompilationAsync, _coverageBodyBindObserver);

    // Every runner-invoked entry point the coverage tools walk from, across the solution,
    // each with the roots its walk starts from (TestEntryPoint). Only each project's own
    // source types are scanned: GetAllNamedTypes includes every referenced assembly, so
    // without the filter an entry point declared in a referenced test project is counted
    // again for each project referencing it, and the BCL is scanned for attributes it
    // cannot carry.
    private async Task<List<TestEntryPoint>> CollectTestEntryPointsAsync(CancellationToken cancellationToken)
    {
        var entryPoints = new List<TestEntryPoint>();
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            var sourceTypes = GetAllNamedTypes(compilation)
                .Where(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly))
                .ToList();
            var lifecycle = new TestLifecycleResolver(sourceTypes);

            foreach (var type in sourceTypes)
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (TestFrameworkContracts.ClassifyEntryPoint(method) is not { } kind) continue;
                    var roots = new List<IMethodSymbol> { method };
                    if (kind == TestEntryPointKind.Test) roots.AddRange(lifecycle.RootsForTestsOf(type));
                    else if (!method.IsStatic) roots.AddRange(lifecycle.ConstructionAndDisposal(type));
                    entryPoints.Add(new TestEntryPoint(method, kind, project.Name, roots));
                }
            }

            foreach (var fixture in lifecycle.AssemblyFixtures(compilation.Assembly))
            {
                var constructor = fixture.InstanceConstructors
                    .OrderBy(c => c.DeclaredAccessibility == Accessibility.Public ? 0 : 1)
                    .FirstOrDefault();
                if (constructor == null) continue;
                entryPoints.Add(new TestEntryPoint(
                    constructor, TestEntryPointKind.Hook, project.Name, lifecycle.ConstructionAndDisposal(fixture)));
            }
        }
        return entryPoints;
    }

    private static EntryPointCounts CountEntryPoints(IReadOnlyCollection<TestEntryPoint> entryPoints) => new(
        Tests: entryPoints.Count(e => e.Kind == TestEntryPointKind.Test),
        StepBindings: entryPoints.Count(e => e.Kind == TestEntryPointKind.StepBinding),
        Hooks: entryPoints.Count(e => e.Kind == TestEntryPointKind.Hook));

    public async Task<object> FindTestsAsync(
        string typeName, string? memberName = null, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // The target: a specific member when memberName is given, else the type
        // itself (covered when a test reaches any of its members). The type resolution's
        // shadow notice is reported with the answer.
        var resolution = await ResolveMemberByNameAsync(typeName, memberName, overloadIndex, cancellationToken);
        if (resolution.Symbol is not { } targetSymbol) return resolution.Error!;

        var graph = CreateCoverageGraph();
        var targetNode = graph.NodeFor(targetSymbol);
        Func<ReachableNode, bool> isTarget = memberName != null
            ? node => node.Key.Equals(targetNode.Key)
            : node => node.ContainingTypeKey is { } typeKey && typeKey.Equals(targetNode.Key);

        // The interface and base members the target (or, for a type, any of its members)
        // implements or overrides. An entry point that dispatches through one of them
        // without reaching the target itself reaches it only via dispatch: what runs
        // behind that receiver may be another implementation or a mock.
        var targetMembers = memberName != null
            ? new[] { targetNode }
            : ((INamedTypeSymbol)targetNode.Symbol).GetMembers()
                .Where(m => m is IMethodSymbol or IPropertySymbol or IEventSymbol)
                .Select(graph.NodeFor)
                .ToArray();
        var dispatchTargets = targetMembers
            .SelectMany(graph.RelatedDeclarations)
            .Select(declaration => declaration.Key)
            .ToHashSet();

        var entryPoints = await CollectTestEntryPointsAsync(cancellationToken);

        // One walk per entry point over the shared graph; each walk stops as soon as it
        // reaches the target, since that already answers the entry point.
        var covering = new List<object>();
        var dispatchOnly = new List<object>();
        foreach (var entryPoint in entryPoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var walk = await graph.WalkAsync(entryPoint.Roots, isTarget, cancellationToken);
            if (walk.ReachedTarget) covering.Add(EntryPointEntry(entryPoint));
            else if (dispatchTargets.Overlaps(walk.Dispatched)) dispatchOnly.Add(EntryPointEntry(entryPoint));
        }

        return CreateSuccessResponse(
            data: new
            {
                target = targetSymbol.ToDisplayString(),
                totalTests = entryPoints.Count(e => e.Kind == TestEntryPointKind.Test),
                coveringTests = covering,
                dispatchOnlyTests = dispatchOnly,
                notice = resolution.Type?.ShadowNotice
            },
            suggestedNextTools: covering.Count > 0
                ? new[] { "get_method_source on a covering test to see how the symbol is exercised" }
                : dispatchOnly.Count > 0
                    ? new[]
                    {
                        "dispatchOnlyTests call it only through an interface or base member — a test that runs this implementation directly would cover it",
                        "generate_test_stub scaffolds one"
                    }
                    : new[]
                    {
                        "No test reaches this symbol — generate_test_stub scaffolds one",
                        "find_untested_code lists the rest of the untested surface"
                    },
            totalCount: covering.Count,
            returnedCount: covering.Count);
    }

    private object EntryPointEntry(TestEntryPoint entryPoint)
    {
        var method = entryPoint.Method;
        var lineSpan = method.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
        return new
        {
            testMethod = method.Name,
            containingType = method.ContainingType?.ToDisplayString(),
            testProject = entryPoint.ProjectName,
            kind = entryPoint.KindName,
            location = lineSpan is { } span
                ? new { filePath = FormatPath(span.Path), line = span.StartLinePosition.Line }
                : null as object
        };
    }
}
