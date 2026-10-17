using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks CrossCompilationSymbolComparer: equality by SolutionKey across compilations,
// distinctness across assemblies, and the symbol-equality fallback for symbols that
// have no SolutionKey.
public class CrossCompilationSymbolComparerTests
{
    private static readonly CrossCompilationSymbolComparer Comparer = CrossCompilationSymbolComparer.Instance;

    [Fact]
    public async Task RetargetedAndOwnInstances_AreEqual_WithEqualHashCodes()
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests);
        var (libOwn, seenFromTests) = await CrossCompilationSymbolTests.AddFromBothCompilationsAsync(fixture);

        SymbolEqualityComparer.Default.Equals(libOwn, seenFromTests).Should().BeFalse(
            "precondition: the fixture yields two instances of one declaration");
        Comparer.Equals(libOwn, seenFromTests).Should().BeTrue();
        Comparer.GetHashCode(libOwn).Should().Be(Comparer.GetHashCode(seenFromTests));
        new HashSet<ISymbol>(Comparer) { libOwn, seenFromTests }.Should().HaveCount(1);
    }

    [Fact]
    public async Task SameNamedSourceTypesOfTwoAssemblies_AreNotEqual()
    {
        const string declaresProgram = "namespace Shared { public class Program { } }";
        using var fixture = new RetargetedSolutionFixture(declaresProgram, declaresProgram);
        var solution = fixture.Workspace.CurrentSolution;
        var libProgram = (await solution.GetProject(fixture.LibProjectId)!.GetCompilationAsync())!.Assembly
            .GetTypeByMetadataName("Shared.Program")!;
        var testsProgram = (await solution.GetProject(fixture.TestProjectId)!.GetCompilationAsync())!.Assembly
            .GetTypeByMetadataName("Shared.Program")!;

        Comparer.Equals(libProgram, testsProgram).Should().BeFalse(
            "Lib's Shared.Program and Lib.Tests' Shared.Program are two declarations");
    }

    [Fact]
    public async Task SymbolsWithoutASolutionKey_FallBackToSymbolEquality()
    {
        const string code = """
            namespace LocalProbe
            {
                public class Host
                {
                    public int First() { int Helper() => 1; return Helper(); }
                    public int Second() { int Helper() => 2; return Helper(); }
                }
            }
            """;
        var (_, document) = TestHelpers.CreateWorkspaceWithCode(code, "Host.cs");
        var model = (await document.GetSemanticModelAsync())!;
        var root = (await document.GetSyntaxRootAsync())!;
        var declared = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
            .Select(f => model.GetDeclaredSymbol(f)!)
            .ToList();
        var invoked = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => model.GetSymbolInfo(i).Symbol!)
            .ToList();

        // The two same-named local functions are distinct; each call binds its own.
        Comparer.Equals(declared[0], declared[1]).Should().BeFalse();
        Comparer.Equals(declared[0], invoked[0]).Should().BeTrue();
        Comparer.Equals(declared[1], invoked[1]).Should().BeTrue();
        Comparer.GetHashCode(declared[0]).Should().Be(Comparer.GetHashCode(invoked[0]));
    }

    [Fact]
    public async Task NullOperands_AreEqualOnlyToNull()
    {
        var (_, document) = TestHelpers.CreateWorkspaceWithCode("namespace N { public class C { } }", "C.cs");
        var type = (await document.Project.GetCompilationAsync())!.GetTypeByMetadataName("N.C")!;

        Comparer.Equals(null, null).Should().BeTrue();
        Comparer.Equals(type, null).Should().BeFalse();
        Comparer.Equals(null, type).Should().BeFalse();
    }
}
