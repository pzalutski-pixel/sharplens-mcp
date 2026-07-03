using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// get_exception_flow: which exception types can escape a method (transitively
// through its in-source callees), and where each is caught along the way.
// C# has no checked exceptions — nothing else answers this. Direct throws are
// matched against the try/catch blocks enclosing the THROW SITE; a callee's
// escaping set is matched against the try/catch blocks enclosing the CALL
// SITE. Callees include ordinary/local-function invocations, constructor
// (`new T()`) throws, and the get/set accessor a property reference dispatches
// to. Conservative choices: a catch with a `when` filter is treated as
// non-catching (the exception may still escape), metadata-only callees (BCL,
// NuGet) contribute nothing (their throws are unknowable from source), and
// async/Task wrapping is not modeled — a thrown exception is attributed to
// the call site whether or not it is awaited.
public partial class RoslynService
{
    internal sealed record ThrownExceptionInfo(ITypeSymbol Type, Location ThrowSite);

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

        if (maxDepth <= 0 || maxDepth > 10)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxDepth must be between 1 and 10 (got {maxDepth})",
                hint: "Depth 1 analyzes only the method's own throws; each extra level follows in-source callees.",
                context: new { maxDepth });
        }

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName, overloadIndex,
            memberNameRequired: true, cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var (symbol, _, error) = await ResolveSymbolAtAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not IMethodSymbol rootMethod)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a method symbol",
                hint: "Address a method declaration (or use typeName+memberName).",
                context: new { actualKind = symbol.Kind.ToString() });
        }

        var memo = new Dictionary<IMethodSymbol, (Dictionary<string, ThrownExceptionInfo> escaping, bool truncated)>(
            SymbolEqualityComparer.Default);
        var stack = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var caught = new List<(ITypeSymbol type, Location catchLocation, Location thrownFrom)>();

        var (escaping, truncatedByDepth) = await AnalyzeExceptionFlowAsync(
            rootMethod, maxDepth, stack, memo, caught, cancellationToken);

        // The same type can be caught at the same site via several call paths;
        // collapse identical (type, catch, throw) triples defensively so the
        // array and the meta counts agree.
        var distinctCaught = caught
            .GroupBy(c => (c.type.ToDisplayString(), c.catchLocation, c.thrownFrom))
            .Select(g => g.First())
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
                        sampleThrowSite = DescribeLocation(e.ThrowSite)
                    })
                    .ToList(),
                caught = distinctCaught
                    .OrderBy(c => c.type.ToDisplayString(), StringComparer.Ordinal)
                    .Select(c => new
                    {
                        exceptionType = c.type.ToDisplayString(),
                        catchLocation = DescribeLocation(c.catchLocation),
                        thrownFrom = DescribeLocation(c.thrownFrom)
                    })
                    .ToList(),
                truncatedByDepth
            },
            suggestedNextTools: new[]
            {
                "get_call_graph to see the call paths the exceptions travel",
                "find_catch_blocks callers should add handling at the sites that let these escape"
            });
    }

    // Returns the exceptions that escape `method`. Direct throws and callee
    // escapes that an enclosing catch absorbs are appended to `caught` (the
    // root call passes the shared list; recursive calls pass null so only the
    // analyzed method's own catches are reported). Memoized per symbol within
    // one tool call; a memo entry computed under a smaller depth budget may
    // under-report deeper chains, which the truncated flag surfaces.
    private async Task<(Dictionary<string, ThrownExceptionInfo> escaping, bool truncated)>
        AnalyzeExceptionFlowAsync(
            IMethodSymbol method,
            int remainingDepth,
            HashSet<IMethodSymbol> stack,
            Dictionary<IMethodSymbol, (Dictionary<string, ThrownExceptionInfo> escaping, bool truncated)> memo,
            List<(ITypeSymbol type, Location catchLocation, Location thrownFrom)>? caught,
            CancellationToken cancellationToken)
    {
        if (caught == null && memo.TryGetValue(method, out var memoized)) return memoized;

        var escaping = new Dictionary<string, ThrownExceptionInfo>(StringComparer.Ordinal);
        var truncated = false;
        stack.Add(method);

        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            var document = _solution!.GetDocument(declRef.SyntaxTree);
            if (document == null) continue;
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null) continue;
            var declaration = await declRef.GetSyntaxAsync(cancellationToken);

            // Nested lambda/local-function bodies are excluded from the direct
            // scan: their throws only fire when THEY run. Local functions get
            // analyzed as callees when this method invokes them.
            var bodyNodes = declaration.DescendantNodes(n =>
                n is not AnonymousFunctionExpressionSyntax && n is not LocalFunctionStatementSyntax);

            foreach (var node in bodyNodes)
            {
                switch (node)
                {
                    case ThrowStatementSyntax throwStatement:
                    {
                        var thrownType = throwStatement.Expression != null
                            ? model.GetTypeInfo(throwStatement.Expression, cancellationToken).Type
                            : EnclosingCatchType(throwStatement, model, cancellationToken);
                        RecordThrow(thrownType, throwStatement.GetLocation(), throwStatement);
                        break;
                    }
                    case ThrowExpressionSyntax throwExpression:
                    {
                        var thrownType = model.GetTypeInfo(throwExpression.Expression, cancellationToken).Type;
                        RecordThrow(thrownType, throwExpression.GetLocation(), throwExpression);
                        break;
                    }
                    case InvocationExpressionSyntax invocation:
                    {
                        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol callee
                            && callee.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction)
                        {
                            await ProcessCalleeAsync(callee, invocation);
                        }
                        break;
                    }
                    case ObjectCreationExpressionSyntax creation:
                    {
                        if (model.GetSymbolInfo(creation, cancellationToken).Symbol is IMethodSymbol constructor)
                        {
                            await ProcessCalleeAsync(constructor, creation);
                        }
                        break;
                    }
                    case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    {
                        if (model.GetSymbolInfo(implicitCreation, cancellationToken).Symbol is IMethodSymbol constructor)
                        {
                            await ProcessCalleeAsync(constructor, implicitCreation);
                        }
                        break;
                    }
                    // A property reference dispatches to its accessor, which can throw.
                    // Qualified access (`obj.Prop`) is handled at the member-access node;
                    // the inner name is skipped here so a write target is not also read.
                    case MemberAccessExpressionSyntax memberAccess:
                    {
                        if (!IsInNameofOrAttributeArgument(memberAccess)
                            && model.GetSymbolInfo(memberAccess, cancellationToken).Symbol is IPropertySymbol property)
                        {
                            await ProcessPropertyAsync(property, memberAccess);
                        }
                        break;
                    }
                    case IdentifierNameSyntax identifier
                        when identifier.Parent is not MemberAccessExpressionSyntax parentAccess
                            || parentAccess.Name != identifier:
                    {
                        if (!IsInNameofOrAttributeArgument(identifier)
                            && model.GetSymbolInfo(identifier, cancellationToken).Symbol is IPropertySymbol property)
                        {
                            await ProcessPropertyAsync(property, identifier);
                        }
                        break;
                    }
                }
            }

            void RecordThrow(ITypeSymbol? thrownType, Location throwSite, SyntaxNode throwNode)
            {
                if (thrownType is null or IErrorTypeSymbol) return;
                var catchClause = FindEnclosingCatch(throwNode, thrownType, declaration, model, cancellationToken);
                if (catchClause != null)
                {
                    caught?.Add((thrownType, catchClause.GetLocation(), throwSite));
                    return;
                }
                var key = thrownType.ToDisplayString();
                if (!escaping.ContainsKey(key)) escaping[key] = new ThrownExceptionInfo(thrownType, throwSite);
            }

            async Task ProcessPropertyAsync(IPropertySymbol property, SyntaxNode reference)
            {
                var (followGet, followSet) = PropertyAccessorsToFollow(reference);
                if (followGet && property.GetMethod is { } getter) await ProcessCalleeAsync(getter, reference);
                if (followSet && property.SetMethod is { } setter) await ProcessCalleeAsync(setter, reference);
            }

            async Task ProcessCalleeAsync(IMethodSymbol callee, SyntaxNode callSite)
            {
                if (callee.DeclaringSyntaxReferences.Length == 0) return; // metadata: unknowable
                if (stack.Contains(callee))
                {
                    // A recursion cycle: this branch's deeper escapes are not followed, so
                    // the result is INCOMPLETE. Mark it truncated so it isn't memoized as
                    // an authoritative (complete) per-symbol result and re-used elsewhere.
                    truncated = true;
                    return;
                }
                if (remainingDepth <= 1)
                {
                    truncated = true;
                    return;
                }

                var (calleeEscaping, calleeTruncated) = await AnalyzeExceptionFlowAsync(
                    callee, remainingDepth - 1, stack, memo, caught: null, cancellationToken);
                truncated |= calleeTruncated;

                foreach (var thrown in calleeEscaping.Values)
                {
                    var catchClause = FindEnclosingCatch(callSite, thrown.Type, declaration, model, cancellationToken);
                    if (catchClause != null)
                    {
                        caught?.Add((thrown.Type, catchClause.GetLocation(), thrown.ThrowSite));
                    }
                    else
                    {
                        var key = thrown.Type.ToDisplayString();
                        if (!escaping.ContainsKey(key)) escaping[key] = thrown;
                    }
                }
            }
        }

        stack.Remove(method);
        // Only a complete (non-truncated) result is depth-independent and safe to
        // reuse; a depth-truncated result computed under a smaller budget would
        // under-report a later, deeper need, so it is recomputed rather than cached.
        if (caught == null && !truncated) memo[method] = (escaping, truncated);
        return (escaping, truncated);
    }

    // Which property accessors a reference dispatches to: a simple-assignment
    // target writes (set) only; a compound-assignment target reads then writes
    // (get + set); every other reference reads (get).
    private static (bool followGet, bool followSet) PropertyAccessorsToFollow(SyntaxNode reference)
    {
        if (reference.Parent is AssignmentExpressionSyntax assignment && assignment.Left == reference)
        {
            return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? (false, true) : (true, true);
        }
        return (true, false);
    }

    // The first try/catch between `site` and the method boundary whose try
    // BLOCK contains the site and whose catch list handles `thrownType`.
    // A catch participates only when the site sits in the try block proper —
    // a rethrow inside a catch clause is NOT re-caught by the same try. A
    // catch carrying a `when` filter is skipped (it may decline at runtime).
    private static CatchClauseSyntax? FindEnclosingCatch(
        SyntaxNode site, ITypeSymbol thrownType, SyntaxNode methodScope,
        SemanticModel model, CancellationToken cancellationToken)
    {
        for (SyntaxNode? child = site, parent = site.Parent;
             parent != null && child != methodScope;
             child = parent, parent = parent.Parent)
        {
            if (parent is not TryStatementSyntax tryStatement || child != tryStatement.Block) continue;

            foreach (var catchClause in tryStatement.Catches)
            {
                if (catchClause.Filter != null) continue;
                if (catchClause.Declaration == null) return catchClause; // catch-all

                var caughtType = model.GetTypeInfo(catchClause.Declaration.Type, cancellationToken).Type;
                if (caughtType != null && IsOrDerivesFrom(thrownType, caughtType)) return catchClause;
            }
        }
        return null;
    }

    // Type of a bare `throw;`: the enclosing catch's declared exception type,
    // or System.Exception for an untyped catch.
    private static ITypeSymbol? EnclosingCatchType(
        ThrowStatementSyntax bareThrow, SemanticModel model, CancellationToken cancellationToken)
    {
        var catchClause = bareThrow.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
        if (catchClause?.Declaration?.Type is { } declaredType)
        {
            return model.GetTypeInfo(declaredType, cancellationToken).Type;
        }
        return model.Compilation.GetTypeByMetadataName("System.Exception");
    }

    // Base-chain walk compared by display string: exception symbols collected
    // from different projects' compilations are distinct symbol instances for
    // the same metadata type, so symbol equality (and ClassifyConversion on a
    // single compilation) would miss cross-project matches.
    private static bool IsOrDerivesFrom(ITypeSymbol thrown, ITypeSymbol caught)
    {
        var caughtName = caught.ToDisplayString();
        var caughtSourceAssembly = caught.Locations.Any(l => l.IsInSource)
            ? caught.ContainingAssembly?.Name
            : null;
        for (ITypeSymbol? current = thrown; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() != caughtName) continue;
            // Two source-defined exception types can share a fully-qualified name
            // across assemblies; require the assembly name to match so they don't
            // cross-match. Metadata types keep FQN-only matching — the same BCL
            // exception surfaces under different facade assemblies across TFMs.
            if (caughtSourceAssembly != null && current.Locations.Any(l => l.IsInSource)
                && current.ContainingAssembly?.Name != caughtSourceAssembly)
            {
                continue;
            }
            return true;
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
