using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace SharpLensMcp.Tests;

// TargetFrameworkDeduplication: per-framework duplicates collapse into one result that
// accumulates the frameworks it occurred in, by symbol identity or by location.
public class TargetFrameworkDeduplicationTests
{
    // One raw scan hit: the invoked symbol, where it was invoked, and the framework of the
    // project that reported it.
    private sealed record Hit(ISymbol Symbol, Location Location, string? TargetFramework);

    // Every call to Greeter.Format in every project of the fixture, scanned per project
    // the way a per-project tool scans: Greet's call site twice (once per framework),
    // Modern's once (net10.0 only).
    private static async Task<List<Hit>> FormatCallsAsync(MultiTargetSolutionFixture fixture)
    {
        var hits = new List<Hit>();
        foreach (var project in fixture.Workspace.CurrentSolution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var compilation = (await project.GetCompilationAsync())!;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(call).Symbol is { Name: "Format" } symbol)
                    {
                        hits.Add(new Hit(symbol, call.GetLocation(), LogicalProject.TargetFrameworkOf(project)));
                    }
                }
            }
        }
        return hits;
    }

    [Fact]
    public async Task ByLocation_CollapsesTheSharedCallSite_AndKeepsTheFrameworkOnlySite()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var hits = await FormatCallsAsync(fixture);
        hits.Should().HaveCount(3, "precondition: Greet's call site once per framework, Modern's once");

        var merged = TargetFrameworkDeduplication.ByLocation(hits, h => SourceSpanKey.From(h.Location)!.Value, h => h.TargetFramework);

        var (greetLine, _) = RetargetedSources.Locate(MultiTargetSources.Multi, "Format(name);");
        var (modernLine, _) = RetargetedSources.Locate(MultiTargetSources.Multi, "Format(\"net10\")");
        merged.Select(m => (m.Item.Location.GetLineSpan().StartLinePosition.Line, string.Join(",", m.TargetFrameworks), m.Occurrences))
            .Should().BeEquivalentTo(new[]
            {
                (greetLine, "net10.0,net8.0", 2),
                (modernLine, "net10.0", 1)
            });
    }

    [Fact]
    public async Task BySymbol_CollapsesOneDeclarationAcrossFrameworks()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var hits = await FormatCallsAsync(fixture);

        var merged = TargetFrameworkDeduplication.BySymbol(hits, h => h.Symbol, h => h.TargetFramework);

        merged.Should().ContainSingle();
        merged[0].Item.Symbol.ToDisplayString().Should().Be("Multi.Greeter.Format(string)");
        merged[0].TargetFrameworks.Should().Equal("net10.0", "net8.0");
        merged[0].Occurrences.Should().Be(3);
    }

    [Fact]
    public async Task Representative_IsTheOrdinallyFirstFrameworksOccurrence_WhateverTheScanOrder()
    {
        using var fixture = new MultiTargetSolutionFixture();
        var hits = await FormatCallsAsync(fixture);
        var net8First = hits.OrderBy(h => h.TargetFramework == "net8.0" ? 0 : 1).ToList();
        net8First[0].TargetFramework.Should().Be("net8.0", "precondition: the scan meets the net8.0 hit first");

        var merged = TargetFrameworkDeduplication.ByLocation(net8First, h => SourceSpanKey.From(h.Location)!.Value, h => h.TargetFramework);

        merged.Select(m => m.Item.TargetFramework).Should().Equal(new[] { "net10.0", "net10.0" },
            "net10.0 sorts before net8.0 ordinally, so its occurrence represents both groups");
    }

    [Fact]
    public void EmptyInput_IsEmpty()
    {
        TargetFrameworkDeduplication.By(Array.Empty<string>(), s => s, StringComparer.Ordinal, _ => null)
            .Should().BeEmpty();
    }

    [Fact]
    public void SingleItem_IsOneGroupOfOne()
    {
        var merged = TargetFrameworkDeduplication.By(new[] { "a" }, s => s, StringComparer.Ordinal, _ => "net8.0");

        merged.Should().ContainSingle();
        merged[0].Should().BeEquivalentTo(new MergedResult<string>("a", new[] { "net8.0" }, 1));
    }

    [Fact]
    public void ExactDuplicatesWithinOneFramework_MergeWithOneFramework()
    {
        // The single-framework duplicate: one location reported twice by one compilation.
        var merged = TargetFrameworkDeduplication.By(
            new[] { ("x", "net8.0"), ("x", "net8.0"), ("y", "net8.0") },
            i => i.Item1, StringComparer.Ordinal, i => i.Item2);

        merged.Select(m => (m.Item.Item1, string.Join(",", m.TargetFrameworks), m.Occurrences))
            .Should().Equal(("x", "net8.0", 2), ("y", "net8.0", 1));
    }

    [Fact]
    public void ItemsWithoutAFramework_HaveNoFrameworks_AndRepresentTheirGroup()
    {
        var merged = TargetFrameworkDeduplication.By(
            new[] { ("x", (string?)"net8.0", 1), ("x", null, 2), ("y", null, 3) },
            i => i.Item1, StringComparer.Ordinal, i => i.Item2);

        merged.Select(m => (m.Item.Item3, string.Join(",", m.TargetFrameworks), m.Occurrences))
            .Should().Equal((2, "net8.0", 2), (3, "", 1));
    }

    [Fact]
    public void Groups_KeepFirstSeenOrder()
    {
        var merged = TargetFrameworkDeduplication.By(
            new[] { "c", "a", "c", "b", "a" }, s => s, StringComparer.Ordinal, _ => null);

        merged.Select(m => m.Item).Should().Equal("c", "a", "b");
        merged.Select(m => m.Occurrences).Should().Equal(2, 2, 1);
    }

    [Fact]
    public void SourceSpanKey_NormalizesSeparators_AndComparesSpansExactly()
    {
        var a = new SourceSpanKey(@"C:\src\A.cs", new TextSpan(10, 5));
        var b = new SourceSpanKey("C:/src/A.cs", new TextSpan(10, 5));
        var shifted = new SourceSpanKey("C:/src/A.cs", new TextSpan(10, 6));

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.FilePath.Should().Be("C:/src/A.cs");
        a.Should().NotBe(shifted);
        SourceSpanKey.From(Location.None).Should().BeNull();
    }

    [Fact]
    public void SourceSpanKey_PathCase_FollowsThePlatform()
    {
        var upper = new SourceSpanKey("/src/A.cs", new TextSpan(0, 1));
        var lower = new SourceSpanKey("/src/a.cs", new TextSpan(0, 1));

        upper.Equals(lower).Should().Be(OperatingSystem.IsWindows(),
            "file paths are case-insensitive on Windows and case-sensitive elsewhere");
    }
}
