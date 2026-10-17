using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// get_exception_flow: which exception types can escape a method (transitively through its
// in-source callees), and where each is caught along the way. C# has no checked
// exceptions — nothing else answers this.
//
// The walk follows the call edges every call tool reads (CallGraphSession): ordinary,
// extension and generic calls, constructors (with the initializers they run; a primary
// constructor runs only those and its base call), the accessor a property or indexer use
// runs (both for ++/--, compound assignment and ??=), operators, conversions, partial
// implementations, and, flagged viaDispatch, the implementations an interface or virtual
// call's receiver type can reach. Direct throws are matched against the try/catch blocks
// enclosing the THROW SITE; a callee's escaping set against those enclosing the CALL
// SITE. Lambda bodies are part of their containing method and matched lexically; a
// local function's throws are matched at each of its call sites (a local function never
// called contributes nothing). A catch that rethrows what it catches (`throw;`, or
// `throw e;` of its own variable) absorbs nothing: the exception continues outward and
// keeps its original throw site. Conservative choices: a catch with a `when` filter is
// treated as non-catching (the exception may still escape), metadata-only callees (BCL,
// NuGet) contribute nothing (their throws are unknowable from source), and async/Task
// wrapping is not modeled — a thrown exception is attributed to the call site whether or
// not it is awaited.
public partial class RoslynService
{
    private const int MaxExceptionFlowDepth = 10;

    // An exception escaping a member: its type, one throw site it comes from, and whether
    // it arrives only through a dispatched call (an implementation the receiver may run).
    internal sealed record ThrownExceptionInfo(ITypeSymbol Type, Location ThrowSite, bool ViaDispatch);

    private sealed record CaughtException(ITypeSymbol Type, Location CatchLocation, Location ThrownFrom, bool ViaDispatch);

    public async Task<object> GetExceptionFlowAsync(
        string? filePath,
        int? line,
        int? column,
        int maxDepth = 3,
        string? typeName = null,
        string? memberName = null,
        int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxDepth", maxDepth, MaxExceptionFlowDepth) is { } invalid) return CreateErrorResponse(invalid);

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName, overloadIndex,
            memberNameRequired: true, cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var symbol = address.Symbol;
        if (symbol == null)
        {
            var (atPosition, _, error) = await ResolveSymbolAtAsync(
                address.FilePath, address.Line, address.Column, cancellationToken);
            if (atPosition == null) return error!;
            symbol = atPosition;
        }

        if (symbol is not IMethodSymbol method)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a method symbol",
                hint: "Address a method declaration (or use typeName+memberName).",
                context: new { actualKind = symbol.Kind.ToString() });
        }
        var rootMethod = (IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(method);

        var session = CreateCallGraphSession();
        var memo = new Dictionary<ISymbol, (Dictionary<ISymbol, ThrownExceptionInfo> escaping, bool truncated)>(
            CrossCompilationSymbolComparer.Instance);
        var stack = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        var analyzed = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        var caught = new List<CaughtException>();

        var (escaping, truncatedByDepth) = await AnalyzeExceptionFlowAsync(
            session, rootMethod, maxDepth, stack, memo, caught, analyzed, cancellationToken);

        // The same type can be caught at the same site via several call paths; collapse
        // identical (type, catch, throw) triples so the array and the meta counts agree.
        var distinctCaught = caught
            .GroupBy(c => (c.Type.ToDisplayString(), SourceSpanKey.From(c.CatchLocation), SourceSpanKey.From(c.ThrownFrom)))
            .Select(g => g.OrderBy(c => c.ViaDispatch).First())
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                method = rootMethod.ToDisplayString(),
                maxDepth,
                escaping = escaping.Values
                    .OrderBy(e => e.Type.ToDisplayString(), StringComparer.Ordinal)
                    .Select(e => new
                    {
                        exceptionType = e.Type.ToDisplayString(),
                        sampleThrowSite = DescribeLocation(e.ThrowSite),
                        viaDispatch = e.ViaDispatch
                    })
                    .ToList(),
                caught = distinctCaught
                    .OrderBy(c => c.Type.ToDisplayString(), StringComparer.Ordinal)
                    .Select(c => new
                    {
                        exceptionType = c.Type.ToDisplayString(),
                        catchLocation = DescribeLocation(c.CatchLocation),
                        thrownFrom = DescribeLocation(c.ThrownFrom),
                        viaDispatch = c.ViaDispatch
                    })
                    .ToList(),
                // The members whose bodies the walk analyzed: the method itself, then the
                // in-source callees it followed, in canonical order.
                analyzedMembers = new[] { rootMethod.ToDisplayString() }
                    .Concat(CanonicalOrder.BySymbol(analyzed.Where(m => !CrossCompilationSymbolComparer.Instance.Equals(m, rootMethod)), m => m)
                        .Select(m => m.ToDisplayString()))
                    .ToList(),
                truncatedByDepth,
                notice = address.TypeResolution?.ShadowNotice
            },
            suggestedNextTools: new[]
            {
                "get_call_graph to see the call paths the exceptions travel",
                "find_catch_blocks callers should add handling at the sites that let these escape"
            });
    }

    // Returns the exceptions that escape `method`. Direct throws and callee escapes that an
    // enclosing catch absorbs are appended to `caught` (the root call passes the shared
    // list; recursive calls pass null so only the analyzed method's own catches are
    // reported). Memoized per member within one tool call; a result cut by the depth budget
    // or a recursion cycle is incomplete, is reported truncated, and is not memoized.
    private async Task<(Dictionary<ISymbol, ThrownExceptionInfo> escaping, bool truncated)> AnalyzeExceptionFlowAsync(
        CallGraphSession session,
        IMethodSymbol method,
        int remainingDepth,
        HashSet<ISymbol> stack,
        Dictionary<ISymbol, (Dictionary<ISymbol, ThrownExceptionInfo> escaping, bool truncated)> memo,
        List<CaughtException>? caught,
        HashSet<ISymbol> analyzed,
        CancellationToken cancellationToken)
    {
        if (caught == null && memo.TryGetValue(method, out var memoized)) return memoized;

        var escaping = new Dictionary<ISymbol, ThrownExceptionInfo>(CrossCompilationSymbolComparer.Instance);
        var truncated = false;
        analyzed.Add(method);
        stack.Add(method);

        void Escape(ITypeSymbol type, Location throwSite, bool viaDispatch)
        {
            // One sample per type; a direct path is preferred over one through dispatch.
            if (!escaping.TryGetValue(type, out var existing) || (existing.ViaDispatch && !viaDispatch))
            {
                escaping[type] = new ThrownExceptionInfo(type, throwSite, viaDispatch);
            }
        }

        var body = await session.BodyAsync(method, cancellationToken);
        // Keyed by source span: a body bound for several target frameworks reports its sites
        // from more than one syntax tree of the same file.
        var rethrowingCatches = body.Throws
            .Where(t => t.IsRethrow)
            .Select(t => new SourceSpanKey(t.RethrownFrom!.SyntaxTree.FilePath, t.RethrownFrom.Span))
            .ToHashSet();
        var flow = new CatchResolution(method, rethrowingCatches);

        foreach (var thrown in body.Throws)
        {
            if (thrown.IsRethrow || thrown.ExceptionType is null or IErrorTypeSymbol) continue;
            var model = await session.SemanticModelAsync(thrown.Syntax.SyntaxTree, cancellationToken);
            if (model == null) continue;
            foreach (var catchClause in flow.Outcomes(thrown.Syntax, thrown.ExceptionType, model, cancellationToken))
            {
                if (catchClause != null) caught?.Add(new CaughtException(thrown.ExceptionType, catchClause.GetLocation(), thrown.Syntax.GetLocation(), false));
                else Escape(thrown.ExceptionType, thrown.Syntax.GetLocation(), false);
            }
        }

        foreach (var edge in body.Callees)
        {
            if (!CallGraphSession.HasSourceBody(edge.Target)) continue; // metadata or body-less: unknowable
            if (stack.Contains(edge.Target))
            {
                // A recursion cycle: this branch's deeper escapes are not followed, so the
                // result is INCOMPLETE and must not be memoized as authoritative.
                truncated = true;
                continue;
            }
            if (remainingDepth <= 1)
            {
                truncated = true;
                continue;
            }

            var (calleeEscaping, calleeTruncated) = await AnalyzeExceptionFlowAsync(
                session, edge.Target, remainingDepth - 1, stack, memo, caught: null, analyzed, cancellationToken);
            truncated |= calleeTruncated;
            if (calleeEscaping.Count == 0) continue;

            var model = await session.SemanticModelAsync(edge.Syntax.SyntaxTree, cancellationToken);
            if (model == null) continue;
            foreach (var thrown in calleeEscaping.Values)
            {
                var viaDispatch = edge.ViaDispatch || thrown.ViaDispatch;
                foreach (var catchClause in flow.Outcomes(edge.Syntax, thrown.Type, model, cancellationToken))
                {
                    if (catchClause != null) caught?.Add(new CaughtException(thrown.Type, catchClause.GetLocation(), thrown.ThrowSite, viaDispatch));
                    else Escape(thrown.Type, thrown.ThrowSite, viaDispatch);
                }
            }
        }

        stack.Remove(method);
        if (caught == null && !truncated) memo[method] = (escaping, truncated);
        return (escaping, truncated);
    }

    // Where an exception thrown at a site within one analyzed member ends up: each catch
    // clause that absorbs it, or null when it escapes the member. A site in a local
    // function is resolved at every call site of that local function; a catch that
    // rethrows passes the exception on to the try statements around its own.
    private sealed class CatchResolution
    {
        private readonly IMethodSymbol _member;
        private readonly HashSet<SourceSpanKey> _rethrowingCatches;

        public CatchResolution(IMethodSymbol member, HashSet<SourceSpanKey> rethrowingCatches)
        {
            _member = member;
            _rethrowingCatches = rethrowingCatches;
        }

        public IEnumerable<CatchClauseSyntax?> Outcomes(
            SyntaxNode site, ITypeSymbol thrownType, SemanticModel model, CancellationToken cancellationToken) =>
            Outcomes(site, thrownType, model, new HashSet<SyntaxNode>(), cancellationToken);

        private IEnumerable<CatchClauseSyntax?> Outcomes(
            SyntaxNode site, ITypeSymbol thrownType, SemanticModel model, HashSet<SyntaxNode> visitedLocalFunctions,
            CancellationToken cancellationToken)
        {
            for (SyntaxNode? child = site, parent = site.Parent; parent != null; child = parent, parent = parent.Parent)
            {
                if (parent is MemberDeclarationSyntax) break;

                if (parent is LocalFunctionStatementSyntax localFunction)
                {
                    var localSymbol = model.GetDeclaredSymbol(localFunction, cancellationToken);
                    // The analyzed member itself (a local function addressed directly).
                    if (SymbolEqualityComparer.Default.Equals(localSymbol, _member)) break;
                    if (localSymbol == null || !visitedLocalFunctions.Add(localFunction)) yield break;
                    foreach (var callSite in CallSitesOf(localFunction, localSymbol, model, cancellationToken))
                    {
                        foreach (var outcome in Outcomes(callSite, thrownType, model, visitedLocalFunctions, cancellationToken))
                            yield return outcome;
                    }
                    yield break;
                }

                if (parent is not TryStatementSyntax tryStatement || child != tryStatement.Block) continue;
                // No absorbing catch (none handles the type, or the one that does rethrows):
                // the loop continues outward from the try statement, since a rethrow is not
                // re-caught by its own try.
                if (AbsorbingCatch(tryStatement, thrownType, model, cancellationToken) is { } absorbed)
                {
                    yield return absorbed;
                    yield break;
                }
            }
            yield return null;
        }

        // The catch of a try statement that absorbs the thrown type: the first unfiltered
        // catch that handles it, unless that catch rethrows. Null when none absorbs it.
        private CatchClauseSyntax? AbsorbingCatch(
            TryStatementSyntax tryStatement, ITypeSymbol thrownType, SemanticModel model, CancellationToken cancellationToken)
        {
            foreach (var catchClause in tryStatement.Catches)
            {
                if (catchClause.Filter != null) continue;
                if (catchClause.Declaration != null)
                {
                    var caughtType = model.GetTypeInfo(catchClause.Declaration.Type, cancellationToken).Type;
                    if (caughtType == null || !IsOrDerivesFrom(thrownType, caughtType)) continue;
                }
                return _rethrowingCatches.Contains(new SourceSpanKey(catchClause.SyntaxTree.FilePath, catchClause.Span))
                    ? null
                    : catchClause;
            }
            return null;
        }

        // The places a local function is invoked or converted to a delegate within the
        // member declaring it.
        private static IEnumerable<SyntaxNode> CallSitesOf(
            LocalFunctionStatementSyntax localFunction, ISymbol localSymbol, SemanticModel model, CancellationToken cancellationToken)
        {
            var scope = localFunction.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault()
                        ?? localFunction.SyntaxTree.GetRoot(cancellationToken);
            return scope.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(name => name.Identifier.ValueText == localFunction.Identifier.ValueText
                               && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name, cancellationToken).Symbol, localSymbol));
        }
    }

    // Whether the thrown type is the caught type or derives from it, by the solution's
    // cross-compilation identity: exception types bound in different projects'
    // compilations are distinct instances of one declaration (a metadata type matches
    // across facade assemblies; two source types of one name in different assemblies do
    // not).
    private static bool IsOrDerivesFrom(ITypeSymbol thrown, ITypeSymbol caught)
    {
        for (ITypeSymbol? current = thrown; current != null; current = current.BaseType)
        {
            if (CrossCompilationSymbolComparer.Instance.Equals(current.OriginalDefinition, caught.OriginalDefinition)) return true;
        }
        return false;
    }

    private object? DescribeLocation(Location location)
    {
        if (!location.IsInSource) return null;
        var lineSpan = location.GetLineSpan();
        return new
        {
            filePath = FormatPath(lineSpan.Path),
            line = lineSpan.StartLinePosition.Line
        };
    }
}
