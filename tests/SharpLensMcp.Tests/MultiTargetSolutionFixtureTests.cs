using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks that MultiTargetSolutionFixture really has the multi-targeted shape the
// de-duplication and project-addressing tests depend on. Without this, a fixture that
// silently loaded one project per file, or gave both frameworks the same preprocessor
// symbols, would let every test built on it pass for the wrong reason.
public class MultiTargetSolutionFixtureTests
{
    private static async Task<Compilation> CompileAsync(MultiTargetSolutionFixture fixture, ProjectId id)
    {
        var compilation = (await fixture.Workspace.CurrentSolution.GetProject(id)!.GetCompilationAsync())!;
        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .Should().BeEmpty($"the {compilation.AssemblyName} fixture source must compile");
        return compilation;
    }

    [Fact]
    public void Projects_HaveTheMultiTargetNamesPathsAndAssemblyNames()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var solution = fixture.Workspace.CurrentSolution;

        solution.Projects.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).Should().Equal(
            "Consumer", "Multi(net10.0)", "Multi(net8.0)");

        var net8 = solution.GetProject(fixture.Net8ProjectId)!;
        var net10 = solution.GetProject(fixture.Net10ProjectId)!;
        var consumer = solution.GetProject(fixture.ConsumerProjectId)!;
        net8.Name.Should().Be("Multi(net8.0)");
        net10.Name.Should().Be("Multi(net10.0)");
        net8.FilePath.Should().Be(fixture.MultiProjectFilePath);
        net10.FilePath.Should().Be(fixture.MultiProjectFilePath);
        consumer.FilePath.Should().Be(fixture.ConsumerProjectFilePath);
        net8.AssemblyName.Should().Be("Multi");
        net10.AssemblyName.Should().Be("Multi");
        consumer.ProjectReferences.Select(r => r.ProjectId).Should().Equal(fixture.Net10ProjectId);
    }

    [Fact]
    public void MultiDocuments_AreLinked_SamePathDistinctDocuments()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var solution = fixture.Workspace.CurrentSolution;
        var net8Document = solution.GetProject(fixture.Net8ProjectId)!.Documents.Single();
        var net10Document = solution.GetProject(fixture.Net10ProjectId)!.Documents.Single();

        net8Document.FilePath.Should().Be(fixture.MultiFilePath("Greeter.cs"));
        net10Document.FilePath.Should().Be(fixture.MultiFilePath("Greeter.cs"));
        net8Document.Id.Should().NotBe(net10Document.Id);
        solution.GetDocumentIdsWithFilePath(fixture.MultiFilePath("Greeter.cs"))
            .Should().BeEquivalentTo(new[] { net8Document.Id, net10Document.Id });
    }

    [Fact]
    public void EachFramework_HasItsOwnPreprocessorSymbols()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var solution = fixture.Workspace.CurrentSolution;

        ((CSharpParseOptions)solution.GetProject(fixture.Net8ProjectId)!.ParseOptions!).PreprocessorSymbolNames
            .Should().Equal("NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER");
        ((CSharpParseOptions)solution.GetProject(fixture.Net10ProjectId)!.ParseOptions!).PreprocessorSymbolNames
            .Should().Equal("NET", "NETCOREAPP", "NET10_0", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER");
    }

    [Fact]
    public async Task ConditionalMember_ExistsInTheNet10CompilationOnly()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var net8Greeter = (await CompileAsync(fixture, fixture.Net8ProjectId)).GetTypeByMetadataName("Multi.Greeter")!;
        var net10Greeter = (await CompileAsync(fixture, fixture.Net10ProjectId)).GetTypeByMetadataName("Multi.Greeter")!;

        net8Greeter.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary)
            .Select(m => m.Name).Should().Equal("Greet", "Format");
        net10Greeter.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary)
            .Select(m => m.Name).Should().Equal("Greet", "Format", "Modern");
    }

    [Fact]
    public async Task OneDeclaration_IsTwoSymbols_WithOneSolutionKey()
    {
        // The de-duplication premise: each framework binds its own instance of a Multi
        // declaration, and the solution identity equates them (shared assembly name).
        using var fixture = new MultiTargetSolutionFixture();
        var net8Greet = (await CompileAsync(fixture, fixture.Net8ProjectId)).GetTypeByMetadataName("Multi.Greeter")!
            .GetMembers("Greet").Single();
        var net10Greet = (await CompileAsync(fixture, fixture.Net10ProjectId)).GetTypeByMetadataName("Multi.Greeter")!
            .GetMembers("Greet").Single();
        var consumer = await CompileAsync(fixture, fixture.ConsumerProjectId);
        var consumerTree = consumer.SyntaxTrees.Single();
        var greetCall = consumerTree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Single(i => i.ToString().EndsWith("Greet(\"app\")", StringComparison.Ordinal));
        var consumerGreet = consumer.GetSemanticModel(consumerTree).GetSymbolInfo(greetCall).Symbol!;

        SymbolEqualityComparer.Default.Equals(net8Greet, net10Greet).Should().BeFalse(
            "each framework's compilation binds its own instance");
        CrossCompilationSymbol.SolutionKey(net8Greet).Should().Be("Multi|M:Multi.Greeter.Greet(System.String)");
        CrossCompilationSymbol.SolutionKey(net10Greet).Should().Be("Multi|M:Multi.Greeter.Greet(System.String)");
        CrossCompilationSymbol.SolutionKey(consumerGreet).Should().Be("Multi|M:Multi.Greeter.Greet(System.String)");
    }

    [Fact]
    public void CustomDocuments_AreAddedToBothFrameworksAndTheConsumer()
    {
        using var fixture = new MultiTargetSolutionFixture(
            new[] { ("A.cs", "namespace Multi { public class A { } }"), ("B.cs", "namespace Multi { public class B { } }") },
            new[] { ("C.cs", "namespace Consumer { public class C { } }") });
        var solution = fixture.Workspace.CurrentSolution;

        foreach (var id in new[] { fixture.Net8ProjectId, fixture.Net10ProjectId })
        {
            solution.GetProject(id)!.Documents.Select(d => d.FilePath).Should().Equal(
                fixture.MultiFilePath("A.cs"), fixture.MultiFilePath("B.cs"));
        }
        solution.GetProject(fixture.ConsumerProjectId)!.Documents.Select(d => d.FilePath).Should().Equal(
            fixture.ConsumerFilePath("C.cs"));
    }
}
