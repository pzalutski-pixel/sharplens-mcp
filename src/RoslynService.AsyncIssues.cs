using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SharpLensMcp;

// find_async_issues: the three highest-signal async-hygiene problems —
// async void that isn't an event handler (methods, local functions, and async lambdas
// converted to a void-returning delegate), blocking on async work (.Result / .Wait() /
// .GetAwaiter().GetResult() on Task-like types), and a CancellationToken in scope that
// isn't forwarded to an awaited call that could take one (a cancellable overload, or an
// optional token parameter left to its default; an await through ConfigureAwait is
// unwrapped to the call it awaits). Every executable body is scanned: methods,
// constructors, operators, accessors, expression-bodied members, initializers, local
// functions and lambdas. Each finding is reported once per source location — a
// multi-targeted project's frameworks merge into targetFrameworks. Read-only; the count
// also feeds get_project_health.
public partial class RoslynService
{
    private const int MaxAsyncIssueResults = 1000;

    public async Task<object> FindAsyncIssuesAsync(
        string? projectName = null, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxAsyncIssueResults) is { } invalid)
            return CreateErrorResponse(invalid);

        var (scope, scopeError) = ResolveProjectScope(projectName);
        if (scope == null) return scopeError!;

        var issues = await FindAsyncIssuesDataAsync(scope, cancellationToken);
        var returned = issues.Take(maxResults).ToList();

        return CreateSuccessResponse(
            data: new
            {
                projectName = projectName ?? "All projects",
                countByCategory = issues
                    .GroupBy(i => i.Item.Category)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
                issues = returned.Select(i => new
                {
                    category = i.Item.Category,
                    symbol = i.Item.Symbol,
                    detail = i.Item.Detail,
                    location = i.Item.Location is { } location
                        ? new { filePath = location.FilePath, line = location.Line, column = location.Column }
                        : null,
                    targetFrameworks = FrameworksOrNull(i.TargetFrameworks)
                }).ToList()
            },
            ResultCounts.Capped(issues.Count, returned.Count),
            suggestedNextTools: issues.Count > 0
                ? new[] { "get_method_source on a flagged method to apply the async fix" }
                : new[] { "No async-hygiene issues found" });
    }

    // A merged finding's targetFrameworks as reported: the frameworks when it comes from a
    // multi-targeted project (whose per-framework projects carry a framework), else null.
    internal static IReadOnlyList<string>? FrameworksOrNull(IReadOnlyList<string> targetFrameworks) =>
        targetFrameworks.Count > 0 ? targetFrameworks : null;

    // The issues of the scope's projects, each source location once (with the frameworks
    // it occurs in), in canonical location order, then by category. Shared with
    // get_project_health. Scans the generator-aware compilation, so generated async code
    // is covered like the diagnostics bucket.
    internal async Task<List<MergedResult<AsyncIssue>>> FindAsyncIssuesDataAsync(
        IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        var raw = new List<(AsyncIssue Issue, string? TargetFramework)>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);

            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                var root = await tree.GetRootAsync(cancellationToken);
                foreach (var node in root.DescendantNodes())
                {
                    if (DetectAsyncIssue(node, model, cancellationToken) is { } issue)
                    {
                        raw.Add((issue, targetFramework));
                    }
                }
            }
        }

        return TargetFrameworkDeduplication.By(
                raw,
                r => (r.Issue.Category, r.Issue.Span, r.Issue.Detail),
                EqualityComparer<(string, SourceSpanKey?, string)>.Default,
                r => r.TargetFramework)
            .Select(m => new MergedResult<AsyncIssue>(m.Item.Issue, m.TargetFrameworks, m.Occurrences))
            .OrderBy(m => m.Item.Location ?? new ResultLocation("", 0, 0), ResultLocationComparer.Instance)
            .ThenBy(m => m.Item.Category, StringComparer.Ordinal)
            .ToList();
    }

    // The issue a node is, if any.
    private AsyncIssue? DetectAsyncIssue(SyntaxNode node, SemanticModel model, CancellationToken ct)
    {
        switch (node)
        {
            case MethodDeclarationSyntax method
                when model.GetDeclaredSymbol(method, ct) is IMethodSymbol symbol && IsAsyncVoid(symbol):
                return Issue("asyncVoid", symbol.ToDisplayString(),
                    "async void is not awaitable and its exceptions crash the process; return Task instead",
                    method.Identifier.GetLocation());
            case LocalFunctionStatementSyntax local
                when model.GetDeclaredSymbol(local, ct) is IMethodSymbol symbol && IsAsyncVoid(symbol):
                return Issue("asyncVoid", ContainingMemberDisplay(model, local.SpanStart),
                    $"async void local function '{local.Identifier.ValueText}' is not awaitable and its exceptions crash the process; return Task instead",
                    local.Identifier.GetLocation());
            case AnonymousFunctionExpressionSyntax lambda
                when lambda.AsyncKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AsyncKeyword)
                     && model.GetSymbolInfo(lambda, ct).Symbol is IMethodSymbol symbol && IsAsyncVoid(symbol):
                return Issue("asyncVoid", ContainingMemberDisplay(model, lambda.SpanStart),
                    "async lambda converted to a void-returning delegate is async void: not awaitable, and its exceptions crash the process; use a Task-returning delegate",
                    lambda.GetLocation());
            case AwaitExpressionSyntax awaitExpression
                when CancellationTokenInScope(model, awaitExpression.SpanStart)
                     && AwaitedInvocation(awaitExpression.Expression, model, ct) is { } invocation
                     && TokenNotForwarded(invocation, model, ct) is { } detail:
                return Issue("tokenNotForwarded", ContainingMemberDisplay(model, awaitExpression.SpanStart),
                    detail, awaitExpression.GetLocation());
            default:
                return DetectBlockingCall(node, model, ct) is { } blocking
                    ? Issue("blockingCall", ContainingMemberDisplay(model, node.SpanStart), blocking, node.GetLocation())
                    : null;
        }
    }

    private AsyncIssue Issue(string category, string symbol, string detail, Location location) =>
        new(category, symbol, detail,
            ResultLocation.From(location) is { } at ? at with { FilePath = FormatPath(at.FilePath) } : null,
            SourceSpanKey.From(location));

    private static bool IsAsyncVoid(IMethodSymbol method) =>
        method.IsAsync && method.ReturnsVoid && !IsEventHandlerShape(method);

    // The member the position is in, as the caller knows it: lambdas and local functions
    // resolve to the member that declares them, an accessor to its property/indexer/event.
    private static string ContainingMemberDisplay(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        while (symbol is IMethodSymbol { MethodKind: MethodKind.LambdaMethod or MethodKind.LocalFunction })
        {
            symbol = symbol.ContainingSymbol;
        }
        if (symbol is IMethodSymbol { AssociatedSymbol: { } associated }) symbol = associated;
        return symbol?.ToDisplayString() ?? "";
    }

    // Whether a CancellationToken parameter is in scope at the position: a parameter of the
    // enclosing function, or of a function enclosing it that a nested lambda or local
    // function can capture (a static one captures nothing).
    private static bool CancellationTokenInScope(SemanticModel model, int position)
    {
        for (var symbol = model.GetEnclosingSymbol(position); symbol is IMethodSymbol method; symbol = method.ContainingSymbol)
        {
            if (method.Parameters.Any(p => IsCancellationToken(p.Type))) return true;
            if (method.AssociatedSymbol is IPropertySymbol { Parameters: var indexerParameters }
                && indexerParameters.Any(p => IsCancellationToken(p.Type))) return true;
            if (method.MethodKind is not (MethodKind.LambdaMethod or MethodKind.LocalFunction) || method.IsStatic) return false;
        }
        return false;
    }

    // The invocation an await expression awaits, through parentheses and ConfigureAwait.
    private static InvocationExpressionSyntax? AwaitedInvocation(ExpressionSyntax expression, SemanticModel model, CancellationToken ct)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    continue;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait" } access }
                    when IsTaskLike(model.GetTypeInfo(access.Expression, ct).Type):
                    expression = access.Expression;
                    continue;
                case InvocationExpressionSyntax invocation:
                    return invocation;
                default:
                    return null;
            }
        }
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
        var detail = $"awaited call to {called.Name} accepts a CancellationToken, but the method's token isn't forwarded";

        // An optional CancellationToken parameter the call leaves to its default.
        if (model.GetOperation(invocation, ct) is IInvocationOperation operation
            && operation.Arguments.Any(a => a.ArgumentKind == ArgumentKind.DefaultValue && IsCancellationToken(a.Parameter?.Type)))
        {
            return detail;
        }

        var callerPassedToken = invocation.ArgumentList.Arguments.Any(
            a => IsCancellationToken(model.GetTypeInfo(a.Expression, ct).Type));
        if (callerPassedToken) return null;

        // True only when a cancellable VARIANT of the called overload exists — the same
        // leading parameters plus a trailing CancellationToken — not merely some
        // same-named overload that happens to take a token (which false-positives on an
        // unrelated overload like Foo(string) when only Foo(CancellationToken) exists).
        // Compared on the unconstructed definitions, so a generic overload matches.
        var calledDefinition = Unreduced(called);
        var acceptsToken =
            (called.ContainingType?.GetMembers(called.Name).OfType<IMethodSymbol>()
                .Any(m => MatchesWithTrailingToken(Unreduced(m), calledDefinition)) ?? false)
            // The token-taking VARIANT of an extension method commonly lives in a DIFFERENT
            // static class (e.g. EF Core's ToListAsync(this IQueryable<T>, CancellationToken)),
            // which GetMembers on the called method's own type never sees. Look up the
            // reduced extension overloads applicable to the receiver type too.
            || (called.MethodKind == MethodKind.ReducedExtension && called.ReceiverType is { } receiverType
                && model.LookupSymbols(invocation.SpanStart, receiverType, called.Name, includeReducedExtensionMethods: true)
                    .OfType<IMethodSymbol>()
                    .Any(m => MatchesWithTrailingToken(Unreduced(m), calledDefinition)));
        return acceptsToken ? detail : null;
    }

    // The unconstructed, unreduced definition of a method.
    private static IMethodSymbol Unreduced(IMethodSymbol method) => (method.ReducedFrom ?? method).OriginalDefinition;

    private static bool MatchesWithTrailingToken(IMethodSymbol candidate, IMethodSymbol called) =>
        candidate.Parameters.Length == called.Parameters.Length + 1
        && candidate.Arity == called.Arity
        && IsCancellationToken(candidate.Parameters[^1].Type)
        && called.Parameters
            .Zip(candidate.Parameters, (c, o) => c.RefKind == o.RefKind && SameTypeShape(c.Type, o.Type))
            .All(match => match);

    // Type equality that treats two methods' type parameters at the same position as the
    // same type (Run<T>(Func<T>) and Run<T>(Func<T>, CancellationToken) share the shape).
    private static bool SameTypeShape(ITypeSymbol left, ITypeSymbol right) => (left, right) switch
    {
        (ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } l,
         ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } r) => l.Ordinal == r.Ordinal,
        (INamedTypeSymbol l, INamedTypeSymbol r) =>
            SymbolEqualityComparer.Default.Equals(l.OriginalDefinition, r.OriginalDefinition)
            && l.TypeArguments.Length == r.TypeArguments.Length
            && l.TypeArguments.Zip(r.TypeArguments, SameTypeShape).All(match => match),
        (IArrayTypeSymbol l, IArrayTypeSymbol r) => l.Rank == r.Rank && SameTypeShape(l.ElementType, r.ElementType),
        (IPointerTypeSymbol l, IPointerTypeSymbol r) => SameTypeShape(l.PointedAtType, r.PointedAtType),
        _ => SymbolEqualityComparer.Default.Equals(left, right)
    };

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
}
