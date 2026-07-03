using FluentAssertions;
using Microsoft.CodeAnalysis;
using SharpLensMcp;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks the cross-compilation identity helper's two canonical forms and the
// SameReference null-guard. These are the strings every cross-compilation
// matcher (generated-code references, fine-grained type searches) relies on.
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
}
