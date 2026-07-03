using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// find_async_issues: the three highest-signal async-hygiene problems —
// async void that isn't an event handler, blocking on async work
// (.Result / .Wait() / .GetAwaiter().GetResult() on Task-like types), and a
// CancellationToken parameter that isn't forwarded to an awaited call that
// could take one. Read-only; the count also feeds get_project_health.
public partial class RoslynService
{
    internal sealed record AsyncIssue(string Category, string Symbol, string Detail, string? FilePath, int Line);

    public async Task<object> FindAsyncIssuesAsync(
        string? projectName = null, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (maxResults is < 1 or > 1000)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxResults must be between 1 and 1000 (got {maxResults})",
                context: new { maxResults });
        }

        if (projectName != null && !_solution!.Projects.Any(
                p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectNotFoundError(projectName);
        }

        var issues = await FindAsyncIssuesDataAsync(projectName, cancellationToken);

        return CreateSuccessResponse(
            data: new
            {
                projectName = projectName ?? "All projects",
                countByCategory = issues
                    .GroupBy(i => i.Category)
                    .ToDictionary(g => g.Key, g => g.Count()),
                issues = issues.Take(maxResults).Select(i => new
                {
                    category = i.Category,
                    symbol = i.Symbol,
                    detail = i.Detail,
                    location = i.FilePath != null
                        ? new { filePath = i.FilePath, line = i.Line }
                        : null as object
                }).ToList()
            },
            suggestedNextTools: issues.Count > 0
                ? new[] { "get_method_source on a flagged method to apply the async fix" }
                : new[] { "No async-hygiene issues found" },
            totalCount: issues.Count,
            returnedCount: Math.Min(issues.Count, maxResults));
    }

    // The typed issue list, shared with get_project_health. Scans method
    // declarations of each in-scope project's own source (base compilation).
    internal async Task<List<AsyncIssue>> FindAsyncIssuesDataAsync(
        string? projectName, CancellationToken cancellationToken)
    {
        var issues = new List<AsyncIssue>();
        var projects = projectName == null
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Augmented (generator-aware) compilation so generated async methods are
            // scanned and the asyncHygiene bucket agrees with get_project_health's
            // diagnostics bucket (which also runs over the augmented compilation).
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken);

                foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(method, cancellationToken) is not IMethodSymbol symbol) continue;
                    var display = symbol.ToDisplayString();

                    // (a) async void that isn't an event handler.
                    if (symbol.IsAsync && symbol.ReturnsVoid && !IsEventHandlerShape(symbol))
                    {
                        issues.Add(new AsyncIssue("asyncVoid", display,
                            "async void is not awaitable and its exceptions crash the process; return Task instead",
                            FormatPath(tree.FilePath), LineOf(method)));
                    }

                    var hasTokenParameter = symbol.Parameters.Any(p => IsCancellationToken(p.Type));

                    foreach (var node in method.DescendantNodes())
                    {
                        // (b) blocking on async — flagged everywhere it appears.
                        var blocking = DetectBlockingCall(node, model, cancellationToken);
                        if (blocking != null)
                        {
                            issues.Add(new AsyncIssue("blockingCall", display, blocking,
                                FormatPath(tree.FilePath), LineOf(node)));
                        }

                        // (c) token-not-forwarded: only meaningful when the
                        // method actually has a token to forward.
                        if (hasTokenParameter && node is AwaitExpressionSyntax { Expression: InvocationExpressionSyntax inv }
                            && TokenNotForwarded(inv, model, cancellationToken) is { } detail)
                        {
                            issues.Add(new AsyncIssue("tokenNotForwarded", display, detail,
                                FormatPath(tree.FilePath), LineOf(node)));
                        }
                    }
                }
            }
        }

        return issues;
    }

    // Event-handler shape: (object, EventArgs-or-derived).
    private static bool IsEventHandlerShape(IMethodSymbol method)
    {
        if (method.Parameters.Length != 2) return false;
        if (method.Parameters[0].Type.SpecialType != SpecialType.System_Object) return false;
        for (var t = method.Parameters[1].Type; t != null; t = t.BaseType)
        {
            if (t.ToDisplayString() == "System.EventArgs") return true;
        }
        return false;
    }

    private static string? DetectBlockingCall(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        switch (node)
        {
            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Result" } ma
                when IsTaskLike(model.GetTypeInfo(ma.Expression, ct).Type):
                return "blocking `.Result` on a Task — await it instead";
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Wait" } w }
                when IsTaskLike(model.GetTypeInfo(w.Expression, ct).Type):
                return "blocking `.Wait()` on a Task — await it instead";
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetResult" } g }
                when IsAwaiter(model.GetTypeInfo(g.Expression, ct).Type):
                return "blocking `.GetAwaiter().GetResult()` — await it instead";
            default:
                return null;
        }
    }

    private static string? TokenNotForwarded(InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken ct)
    {
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol called) return null;

        var callerPassedToken = invocation.ArgumentList.Arguments.Any(
            a => IsCancellationToken(model.GetTypeInfo(a.Expression, ct).Type));
        if (callerPassedToken) return null;

        // True only when a cancellable VARIANT of the called overload exists — the same
        // leading parameters plus a trailing CancellationToken — not merely some
        // same-named overload that happens to take a token (which false-positives on an
        // unrelated overload like Foo(string) when only Foo(CancellationToken) exists).
        static bool MatchesWithTrailingToken(IMethodSymbol candidate, IMethodSymbol called) =>
            candidate.Parameters.Length == called.Parameters.Length + 1
            && IsCancellationToken(candidate.Parameters[^1].Type)
            && called.Parameters
                .Zip(candidate.Parameters, (c, o) => SymbolEqualityComparer.Default.Equals(c.Type, o.Type))
                .All(match => match);

        var acceptsToken =
            (called.ContainingType?.GetMembers(called.Name).OfType<IMethodSymbol>()
                .Any(m => MatchesWithTrailingToken(m, called)) ?? false)
            // The token-taking VARIANT of an extension method commonly lives in a DIFFERENT
            // static class (e.g. EF Core's ToListAsync(this IQueryable<T>, CancellationToken)),
            // which GetMembers on the called method's own type never sees. Look up the
            // reduced extension overloads applicable to the receiver type too.
            || (called.MethodKind == MethodKind.ReducedExtension && called.ReceiverType is { } receiverType
                && model.LookupSymbols(invocation.SpanStart, receiverType, called.Name, includeReducedExtensionMethods: true)
                    .OfType<IMethodSymbol>()
                    .Any(m => MatchesWithTrailingToken(m, called)));
        return acceptsToken
            ? $"awaited call to {called.Name} accepts a CancellationToken, but the method's token isn't forwarded"
            : null;
    }

    private static bool IsCancellationToken(ITypeSymbol? type) =>
        type?.ToDisplayString() == "System.Threading.CancellationToken";

    private static bool IsTaskLike(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
        && named.Name is "Task" or "ValueTask";

    private static bool IsAwaiter(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices"
        && named.Name.EndsWith("Awaiter", StringComparison.Ordinal);

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line;
}
