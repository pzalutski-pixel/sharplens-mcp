using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks that the coverage walk's identity (ReachabilityKey) is the solution-wide
// cross-compilation identity (CrossCompilationSymbol.SolutionKey, applied by
// CrossCompilationSymbolComparer) over a symbol's OriginalDefinition: two symbols share a
// ReachabilityKey exactly when the comparer equates their definitions.
public class ReachabilityKeyTests
{
    private const string Source = """
        namespace KeyProbe
        {
            public class Host
            {
                public int First() { int Local() => 1; System.Func<int> f = () => 10; return Local() + f(); }
                public int Second() { int Local() => 2; System.Func<int> g = () => 20; return Local() + g(); }
                public void Use(System.Collections.Generic.List<int> a, System.Collections.Generic.List<string> b)
                {
                    a.Add(1);
                    b.Add("x");
                }
            }
        }
        """;

    private static async Task<(SemanticModel model, SyntaxNode root)> LoadAsync()
    {
        var (_, document) = TestHelpers.CreateWorkspaceWithCode(Source, "Host.cs");
        return ((await document.GetSemanticModelAsync())!, (await document.GetSyntaxRootAsync())!);
    }

    [Fact]
    public async Task SameNamedLocalFunctionsOfTwoMethods_HaveDistinctKeys()
    {
        // Both local functions have the documentation id "M:KeyProbe.Host.Local"; they are
        // still two declarations, so they must be two coverage nodes.
        var (model, root) = await LoadAsync();
        var locals = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .Select(f => model.GetDeclaredSymbol(f)!)
            .ToList();

        locals.Should().HaveCount(2);
        locals[0].GetDocumentationCommentId().Should().Be(locals[1].GetDocumentationCommentId(),
            "precondition: the documentation id does not tell the two local functions apart");
        ReachabilityKey.For(locals[0]).Should().NotBe(ReachabilityKey.For(locals[1]));
        ReachabilityKey.For(locals[0]).Should().Be(ReachabilityKey.For(locals[0]));
    }

    [Fact]
    public async Task LambdasOfOneType_HaveDistinctKeys()
    {
        // Every lambda of KeyProbe.Host has the documentation id "M:KeyProbe.Host.".
        var (model, root) = await LoadAsync();
        var lambdas = root.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>()
            .Select(l => model.GetSymbolInfo(l).Symbol!)
            .ToList();

        lambdas.Should().HaveCount(2);
        lambdas[0].GetDocumentationCommentId().Should().Be(lambdas[1].GetDocumentationCommentId(),
            "precondition: the documentation id does not tell the two lambdas apart");
        ReachabilityKey.For(lambdas[0]).Should().NotBe(ReachabilityKey.For(lambdas[1]));
    }

    [Fact]
    public async Task ConstructedMembers_ShareTheKeyOfTheirDefinition()
    {
        // The walk keys a node by its OriginalDefinition, so List<int>.Add and
        // List<string>.Add are one node: the single body of List<T>.Add.
        var (model, root) = await LoadAsync();
        var adds = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => model.GetSymbolInfo(i).Symbol)
            .OfType<IMethodSymbol>()
            .Where(m => m.Name == "Add")
            .ToList();

        adds.Should().HaveCount(2);
        SymbolEqualityComparer.Default.Equals(adds[0], adds[1]).Should().BeFalse(
            "precondition: List<int>.Add and List<string>.Add are distinct constructed symbols");
        ReachabilityKey.For(adds[0]).Should().Be(ReachabilityKey.For(adds[1]));
        ReachabilityKey.For(adds[0]).GetHashCode().Should().Be(ReachabilityKey.For(adds[1]).GetHashCode());
    }

    [Fact]
    public async Task RetargetedAndOwnInstances_ShareOneKey()
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests);
        var (libOwn, seenFromTests) = await CrossCompilationSymbolTests.AddFromBothCompilationsAsync(fixture);

        SymbolEqualityComparer.Default.Equals(libOwn, seenFromTests).Should().BeFalse(
            "precondition: the fixture yields two instances of one declaration");
        ReachabilityKey.For(libOwn).Should().Be(ReachabilityKey.For(seenFromTests));
        ReachabilityKey.For(libOwn).GetHashCode().Should().Be(ReachabilityKey.For(seenFromTests).GetHashCode());
    }

    [Fact]
    public async Task KeyEquality_AgreesWithTheCrossCompilationComparerOnDefinitions()
    {
        // The one-identity-rule lock: over every pair drawn from a mixed symbol set
        // (source methods, local functions, lambdas, constructed metadata members, the
        // containing type), ReachabilityKey equality equals comparer equality of the
        // definitions.
        var (model, root) = await LoadAsync();
        var symbols = new List<ISymbol>();
        symbols.AddRange(root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => model.GetDeclaredSymbol(m)!));
        symbols.AddRange(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Select(f => model.GetDeclaredSymbol(f)!));
        symbols.AddRange(root.DescendantNodes().OfType<ParenthesizedLambdaExpressionSyntax>().Select(l => model.GetSymbolInfo(l).Symbol!));
        symbols.AddRange(root.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => model.GetSymbolInfo(i).Symbol!));
        symbols.Add(symbols[0].ContainingType);

        symbols.Should().HaveCount(14, "3 methods, 2 local functions, 2 lambdas, 6 invocations and the type");
        foreach (var x in symbols)
        {
            foreach (var y in symbols)
            {
                var sameKey = ReachabilityKey.For(x).Equals(ReachabilityKey.For(y));
                sameKey.Should().Be(
                    CrossCompilationSymbolComparer.Instance.Equals(x.OriginalDefinition, y.OriginalDefinition),
                    $"ReachabilityKey and the comparer must agree on {x.ToDisplayString()} vs {y.ToDisplayString()}");
            }
        }

        // First, Second, Use, two local functions, two lambdas, Func<TResult>.Invoke,
        // List<T>.Add and Host: the two Local() calls bind the declared local functions,
        // and the f()/g() and a.Add/b.Add calls share one definition each.
        symbols.Select(ReachabilityKey.For).Distinct().Should().HaveCount(10);
    }
}
