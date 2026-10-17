using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpLensMcp;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks the cross-compilation identity helper's three canonical forms and the
// SameReference null-guard. These are the strings every cross-compilation
// matcher (generated-code references, fine-grained type searches, pooled
// call-graph and dependency-graph symbols) relies on.
public class CrossCompilationSymbolTests
{
    private const string Source = """
        namespace IdProbe;

        public class Widget
        {
            public int Compute(int seed) => seed * 2;
        }
        """;

    [Fact]
    public async Task ReferenceId_And_TypeKey_AreCanonical_AndSameReferenceGuardsNull()
    {
        var (_, document) = TestHelpers.CreateWorkspaceWithCode(Source, "Widget.cs");
        var compilation = await document.Project.GetCompilationAsync();
        var widget = compilation!.GetTypeByMetadataName("IdProbe.Widget");
        widget.Should().NotBeNull();
        var compute = widget!.GetMembers("Compute").OfType<IMethodSymbol>().Single();

        CrossCompilationSymbol.TypeKey(widget).Should().Be("IdProbe.Widget");
        CrossCompilationSymbol.ReferenceId(widget).Should().Be("T:IdProbe.Widget");
        CrossCompilationSymbol.ReferenceId(compute).Should().Be("M:IdProbe.Widget.Compute(System.Int32)");

        // SameReference: a matching pre-computed id is a hit; a null target id and
        // a non-matching id are not.
        CrossCompilationSymbol.SameReference(compute, "M:IdProbe.Widget.Compute(System.Int32)")
            .Should().BeTrue();
        CrossCompilationSymbol.SameReference(compute, null).Should().BeFalse();
        CrossCompilationSymbol.SameReference(compute, "T:IdProbe.Widget").Should().BeFalse();
    }

    private const string GenericSource = """
        namespace KeyProbe
        {
            public class Box<T>
            {
                public T Value = default!;
                public U Echo<U>(U item) => item;
            }

            public class Uses
            {
                public int Run()
                {
                    var ints = new Box<int>();
                    var strings = new Box<string>();
                    int Local() => 1;
                    var anon = new { Size = 2 };
                    return ints.Echo(1) + strings.Echo(2) + Local() + anon.Size + ints.Value;
                }
            }
        }
        """;

    [Fact]
    public async Task SolutionKey_QualifiesSourceDeclarations_AndCarriesTypeArguments()
    {
        var (_, document) = TestHelpers.CreateWorkspaceWithCode(GenericSource, "Box.cs", projectName: "KeyProject");
        var model = (await document.GetSemanticModelAsync())!;
        var root = (await document.GetSyntaxRootAsync())!;
        var compilation = model.Compilation;
        var box = compilation.GetTypeByMetadataName("KeyProbe.Box`1")!;
        var int32 = compilation.GetSpecialType(SpecialType.System_Int32);

        // A source definition: assembly-qualified, its own type parameter as argument.
        CrossCompilationSymbol.SolutionKey(box).Should().Be(
            "KeyProject|T:KeyProbe.Box`1{KeyProject|T:KeyProbe.Box`1!0}");
        // A metadata type: the reference id alone.
        CrossCompilationSymbol.SolutionKey(int32).Should().Be("T:System.Int32");
        CrossCompilationSymbol.SolutionKey(compilation.CreateArrayTypeSymbol(int32)).Should().Be("T:System.Int32[1]");
        CrossCompilationSymbol.SolutionKey(box.Construct(int32)).Should().Be(
            "KeyProject|T:KeyProbe.Box`1{T:System.Int32}");

        // Constructed members: the method's own type arguments, then the containing type's.
        var echoCalls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => model.GetSymbolInfo(i).Symbol)
            .OfType<IMethodSymbol>()
            .Where(m => m.Name == "Echo")
            .ToList();
        echoCalls.Select(CrossCompilationSymbol.SolutionKey).Should().Equal(
            "KeyProject|M:KeyProbe.Box`1.Echo``1(``0){T:System.Int32,T:System.Int32}",
            "KeyProject|M:KeyProbe.Box`1.Echo``1(``0){T:System.Int32,T:System.String}");
        var valueRead = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(m => m.Name.Identifier.Text == "Value");
        CrossCompilationSymbol.SolutionKey(model.GetSymbolInfo(valueRead).Symbol!).Should().Be(
            "KeyProject|F:KeyProbe.Box`1.Value{T:System.Int32}");
    }

    [Fact]
    public async Task SolutionKey_IsNull_ForCompilationLocalAndErrorSymbols()
    {
        var (_, document) = TestHelpers.CreateWorkspaceWithCode(GenericSource, "Box.cs", projectName: "KeyProject");
        var model = (await document.GetSemanticModelAsync())!;
        var root = (await document.GetSyntaxRootAsync())!;

        var local = model.GetDeclaredSymbol(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single())!;
        var anonymousCreation = root.DescendantNodes().OfType<AnonymousObjectCreationExpressionSyntax>().Single();
        var anonymousType = model.GetTypeInfo(anonymousCreation).Type!;
        var sizeRead = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(m => m.Name.Identifier.Text == "Size");

        CrossCompilationSymbol.SolutionKey(local).Should().BeNull("a local function is bound only in its own body");
        CrossCompilationSymbol.SolutionKey(anonymousType).Should().BeNull("each assembly synthesizes its own anonymous types");
        CrossCompilationSymbol.SolutionKey(model.GetSymbolInfo(sizeRead).Symbol!).Should().BeNull(
            "a member of an anonymous type is as local as the type");
        CrossCompilationSymbol.SolutionKey(model.Compilation.CreateErrorTypeSymbol(null, "Missing", 0)).Should().BeNull();
    }

    [Fact]
    public async Task SolutionKey_EquatesARetargetedInstanceWithTheDeclaringCompilationsOwn()
    {
        using var fixture = new RetargetedSolutionFixture(RetargetedSources.Lib, RetargetedSources.Tests);
        var (libOwn, seenFromTests) = await AddFromBothCompilationsAsync(fixture);

        SymbolEqualityComparer.Default.Equals(libOwn, seenFromTests).Should().BeFalse(
            "precondition: the fixture yields two instances of one declaration");
        CrossCompilationSymbol.SolutionKey(libOwn).Should().Be("Lib|M:Lib.Calculator.Add(System.Int32,System.Int32)");
        CrossCompilationSymbol.SolutionKey(seenFromTests).Should().Be("Lib|M:Lib.Calculator.Add(System.Int32,System.Int32)");
    }

    [Fact]
    public async Task SolutionKey_KeepsSameNamedSourceTypesOfTwoAssembliesDistinct()
    {
        const string declaresProgram = "namespace Shared { public class Program { } }";
        using var fixture = new RetargetedSolutionFixture(declaresProgram, declaresProgram);
        var solution = fixture.Workspace.CurrentSolution;
        var libProgram = (await solution.GetProject(fixture.LibProjectId)!.GetCompilationAsync())!.Assembly
            .GetTypeByMetadataName("Shared.Program")!;
        var testsProgram = (await solution.GetProject(fixture.TestProjectId)!.GetCompilationAsync())!.Assembly
            .GetTypeByMetadataName("Shared.Program")!;

        CrossCompilationSymbol.SolutionKey(libProgram).Should().Be("Lib|T:Shared.Program");
        CrossCompilationSymbol.SolutionKey(testsProgram).Should().Be("Lib.Tests|T:Shared.Program");
    }

    // Lib.Calculator.Add from Lib's own compilation, and as Lib.Tests' compilation binds it.
    internal static async Task<(IMethodSymbol libOwn, IMethodSymbol seenFromTests)> AddFromBothCompilationsAsync(
        RetargetedSolutionFixture fixture)
    {
        var solution = fixture.Workspace.CurrentSolution;
        var libCompilation = (await solution.GetProject(fixture.LibProjectId)!.GetCompilationAsync())!;
        var testCompilation = (await solution.GetProject(fixture.TestProjectId)!.GetCompilationAsync())!;

        var libOwn = libCompilation.GetTypeByMetadataName("Lib.Calculator")!
            .GetMembers("Add").OfType<IMethodSymbol>().Single();
        var libReference = testCompilation.References.OfType<CompilationReference>().Single();
        var libAssemblyFromTests = (IAssemblySymbol)testCompilation.GetAssemblyOrModuleSymbol(libReference)!;
        var seenFromTests = libAssemblyFromTests.GetTypeByMetadataName("Lib.Calculator")!
            .GetMembers("Add").OfType<IMethodSymbol>().Single();
        return (libOwn, seenFromTests);
    }
}
