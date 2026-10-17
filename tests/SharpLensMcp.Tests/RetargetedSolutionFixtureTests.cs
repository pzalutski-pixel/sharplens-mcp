using FluentAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks that RetargetedSolutionFixture really produces the retargeted shape the
// cross-compilation tests depend on. Without this, a fixture that silently shared
// symbols would let every identity test built on it pass for the wrong reason.
public class RetargetedSolutionFixtureTests
{
    private const string LibSource = "namespace Lib { public class Calculator { public int Add(int a, int b) => a + b; } }";
    private const string TestSource = "namespace Lib.Tests { public class Uses { public int Call() => new Lib.Calculator().Add(1, 2); } }";

    private static async Task<(IMethodSymbol libOwn, IMethodSymbol seenFromTests)> AddFromBothSides(
        RetargetedSolutionFixture fixture)
    {
        var solution = fixture.Workspace.CurrentSolution;
        var libCompilation = (await solution.GetProject(fixture.LibProjectId)!.GetCompilationAsync())!;
        var testCompilation = (await solution.GetProject(fixture.TestProjectId)!.GetCompilationAsync())!;

        testCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty("the test project must compile against Lib");

        var libOwn = libCompilation.GetTypeByMetadataName("Lib.Calculator")!
            .GetMembers("Add").OfType<IMethodSymbol>().Single();

        // Lib.Calculator as Lib.Tests binds it: through Lib.Tests' view of the Lib reference.
        var libReference = testCompilation.References.OfType<CompilationReference>().Single();
        var libAssemblyFromTests = (IAssemblySymbol)testCompilation.GetAssemblyOrModuleSymbol(libReference)!;
        var seenFromTests = libAssemblyFromTests.GetTypeByMetadataName("Lib.Calculator")!
            .GetMembers("Add").OfType<IMethodSymbol>().Single();

        return (libOwn, seenFromTests);
    }

    [Fact]
    public async Task Retarget_TestProjectSeesDistinctLibSymbols_WithTheSameDocId()
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, TestSource);
        var (libOwn, seenFromTests) = await AddFromBothSides(fixture);

        SymbolEqualityComparer.Default.Equals(libOwn, seenFromTests).Should().BeFalse(
            "a reference-set difference makes Lib.Tests see Lib through a retargeting assembly");
        seenFromTests.GetDocumentationCommentId().Should().Be("M:Lib.Calculator.Add(System.Int32,System.Int32)");
        libOwn.GetDocumentationCommentId().Should().Be("M:Lib.Calculator.Add(System.Int32,System.Int32)");
    }

    [Fact]
    public async Task NoRetarget_TestProjectSharesLibSymbols()
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, TestSource, retarget: false);
        var (libOwn, seenFromTests) = await AddFromBothSides(fixture);

        SymbolEqualityComparer.Default.Equals(libOwn, seenFromTests).Should().BeTrue(
            "with identical reference sets Lib.Tests reuses Lib's own assembly symbol");
    }
}
