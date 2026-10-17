using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace SharpLensMcp.Tests;

// SymbolParts exposes every declaring part of a partial member (definition, then the
// body-carrying implementation), all their locations in canonical order, and the one
// location to show or address: the implementation part when user-authored, else the
// earliest user-authored part — never a generator-emitted part.
public class SymbolPartsTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location)
    };

    private const string Definition = @"namespace Lib
{
    public partial class Gen
    {
        public partial void Run(int x);
        public partial int Size { get; }
    }
}";

    private const string Implementation = @"namespace Lib
{
    public partial class Gen
    {
        public partial void Run(int x) { }
        public partial int Size => 1;
    }
}";

    private static INamedTypeSymbol Gen(string definitionPath, string implementationPath)
    {
        var compilation = CSharpCompilation.Create("SymbolPartsFixture",
            new[]
            {
                CSharpSyntaxTree.ParseText(Definition, new CSharpParseOptions(LanguageVersion.Latest), path: definitionPath),
                CSharpSyntaxTree.ParseText(Implementation, new CSharpParseOptions(LanguageVersion.Latest), path: implementationPath)
            },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetTypeByMetadataName("Lib.Gen")!;
    }

    private static string Where(Location location) =>
        $"{location.SourceTree!.FilePath}:{location.GetLineSpan().StartLinePosition.Line}";

    [Theory]
    [InlineData("Run", 4)]
    [InlineData("Size", 5)]
    public void PartialMember_DeclarationPartsAreDefinitionThenImplementation(string memberName, int line)
    {
        var definition = Gen("Gen.cs", "Gen.Impl.cs").GetMembers(memberName).Single();

        var parts = SymbolParts.DeclarationParts(definition);
        parts.Select(p => Where(p.Locations.Single())).Should().Equal($"Gen.cs:{line}", $"Gen.Impl.cs:{line}");
        Where(SymbolParts.ImplementationPart(definition).Locations.Single()).Should().Be($"Gen.Impl.cs:{line}");

        // Either part normalizes to the definition, so both index the same member.
        SymbolParts.SameMember(parts[1], definition).Should().BeTrue();
        SymbolParts.DeclarationParts(parts[1]).Should().Equal(parts);
    }

    [Fact]
    public void SourceLocations_AreInCanonicalPathOrder()
    {
        var run = Gen("b.cs", "A.cs").GetMembers("Run").Single();
        SymbolParts.SourceLocations(run).Select(Where).Should().Equal("A.cs:4", "b.cs:4");
    }

    [Fact]
    public void PreferredLocation_IsTheUserAuthoredImplementation()
    {
        var run = Gen("Gen.cs", "Gen.Impl.cs").GetMembers("Run").Single();
        Where(SymbolParts.PreferredLocation(run, _ => true)!).Should().Be("Gen.Impl.cs:4");
    }

    [Fact]
    public void PreferredLocation_SkipsAGeneratedImplementation()
    {
        var run = Gen("Gen.cs", "Gen.g.cs").GetMembers("Run").Single();
        Where(SymbolParts.PreferredLocation(run, tree => tree.FilePath != "Gen.g.cs")!).Should().Be("Gen.cs:4");
        SymbolParts.PreferredLocation(run, _ => false).Should().BeNull(
            "a member with no user-authored part has no location to address");
    }
}
