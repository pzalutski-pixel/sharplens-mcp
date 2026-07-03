using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// find_tests: which tests cover a symbol — the inverse of find_untested_code.
// Reuses the same test-method detection and BFS reachability, but keeps a
// reachable set PER test method instead of merging them, then answers which
// tests' sets contain the target. Audit-time tool: one solution sweep per
// call, no caching (same caveat as find_untested_code).
public partial class RoslynService
{
    public async Task<object> FindTestsAsync(
        string typeName, string? memberName = null, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // The target: a specific member when memberName is given, else the type
        // itself (covered when a test reaches any of its members).
        var (targetSymbol, symbolError) = await ResolveSymbolByNameAsync(
            typeName, memberName, overloadIndex, cancellationToken);
        if (targetSymbol == null) return symbolError!;

        var targetType = targetSymbol as INamedTypeSymbol ?? targetSymbol.ContainingType;
        var memberTarget = memberName != null ? targetSymbol.OriginalDefinition : null;

        // Test methods across the solution, with the project each lives in.
        var testMethods = new List<(IMethodSymbol method, string projectName)>();
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            foreach (var type in GetAllNamedTypes(compilation))
            {
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (IsTestMethod(method)) testMethods.Add((method, project.Name));
                }
            }
        }

        var covering = new List<object>();
        foreach (var (testMethod, projectName) in testMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reachable = await BuildReachableSetAsync(testMethod, cancellationToken);

            var covers = memberTarget != null
                ? reachable.Contains(memberTarget)
                : targetType != null && reachable.Any(s =>
                    s.ContainingType != null && SymbolEqualityComparer.Default.Equals(
                        s.ContainingType.OriginalDefinition, targetType.OriginalDefinition));
            if (!covers) continue;

            var location = testMethod.Locations.FirstOrDefault(l => l.IsInSource);
            var lineSpan = location?.GetLineSpan();
            covering.Add(new
            {
                testMethod = testMethod.Name,
                containingType = testMethod.ContainingType?.ToDisplayString(),
                testProject = projectName,
                location = lineSpan is { } span
                    ? new { filePath = FormatPath(span.Path), line = span.StartLinePosition.Line }
                    : null as object
            });
        }

        return CreateSuccessResponse(
            data: new
            {
                target = targetSymbol.ToDisplayString(),
                totalTests = testMethods.Count,
                coveringTests = covering
            },
            suggestedNextTools: covering.Count > 0
                ? new[] { "get_method_source on a covering test to see how the symbol is exercised" }
                : new[]
                {
                    "No test reaches this symbol — generate_test_stub scaffolds one",
                    "find_untested_code lists the rest of the untested surface"
                },
            totalCount: covering.Count,
            returnedCount: covering.Count);
    }

    // The transitive set of symbols a single test method reaches, by BFS over
    // in-source callees — the per-test analogue of find_untested_code's merged
    // reachable set, using the same two-set walk (reachable = answer membership,
    // walked = body-traversal gate) and the shared override/interface seeding so
    // the two tools agree on what a test covers. The separate `walked` set is
    // load-bearing: a base method seeded into `reachable` must still be enqueued
    // for its own body when it is itself invoked, so its callees are not missed.
    private async Task<HashSet<ISymbol>> BuildReachableSetAsync(
        IMethodSymbol testMethod, CancellationToken cancellationToken)
    {
        var reachable = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var walked = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>();

        void MarkReached(IMethodSymbol method)
        {
            reachable.Add(method.OriginalDefinition);
            SeedRelatedDeclarations(method, reachable);
            if (walked.Add(method.OriginalDefinition)) queue.Enqueue(method);
        }

        MarkReached(testMethod);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var referenced in await CollectReferencedSymbolsAsync(current, cancellationToken))
            {
                if (referenced is IMethodSymbol method) MarkReached(method);
                else reachable.Add(referenced.OriginalDefinition);
            }
        }

        return reachable;
    }
}
