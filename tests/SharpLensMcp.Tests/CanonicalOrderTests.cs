using FluentAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace SharpLensMcp.Tests;

// The canonical result orders: locations by (path ordinal with '/' separators, line,
// column); symbols by (display string ordinal, first source location, solution key).
public class CanonicalOrderTests
{
    [Fact]
    public void Locations_OrderByPathOrdinal_ThenLineThenColumn()
    {
        var input = new[]
        {
            new ResultLocation("src/b.cs", 1, 0),
            new ResultLocation("src/a.cs", 7, 3),
            new ResultLocation("src/a.cs", 7, 1),
            new ResultLocation("src/B.cs", 9, 9),
            new ResultLocation("src/a.cs", 2, 50)
        };

        CanonicalOrder.ByLocation(input, l => l).Should().Equal(
            new ResultLocation("src/B.cs", 9, 9),
            new ResultLocation("src/a.cs", 2, 50),
            new ResultLocation("src/a.cs", 7, 1),
            new ResultLocation("src/a.cs", 7, 3),
            new ResultLocation("src/b.cs", 1, 0));
    }

    [Fact]
    public void Locations_BackslashAndSlashPaths_SortAsOnePath()
    {
        // Unnormalized, '\' (0x5C) sorts after '/' (0x2F), so "src\a.cs" would follow
        // "src/m.cs"; with separators normalized the file names decide.
        var input = new[]
        {
            new ResultLocation(@"src\z.cs", 0, 0),
            new ResultLocation("src/m.cs", 0, 0),
            new ResultLocation(@"src\a.cs", 0, 0)
        };

        CanonicalOrder.ByLocation(input, l => l).Select(l => l.FilePath).Should().Equal(
            @"src\a.cs", "src/m.cs", @"src\z.cs");
        ResultLocationComparer.Instance.Compare(new ResultLocation(@"a\b.cs", 3, 4), new ResultLocation("a/b.cs", 3, 4))
            .Should().Be(0);
    }

    [Fact]
    public void Locations_TiesKeepInputOrder()
    {
        var input = new[] { ("first", new ResultLocation("a.cs", 1, 1)), ("second", new ResultLocation("a.cs", 1, 1)) };

        CanonicalOrder.ByLocation(input, i => i.Item2).Select(i => i.Item1).Should().Equal("first", "second");
        CanonicalOrder.ByLocation(input.Reverse(), i => i.Item2).Select(i => i.Item1).Should().Equal("second", "first");
    }

    [Fact]
    public void Locations_EmptyAndSingle()
    {
        CanonicalOrder.ByLocation(Array.Empty<ResultLocation>(), l => l).Should().BeEmpty();
        CanonicalOrder.ByLocation(new[] { new ResultLocation("a.cs", 0, 0) }, l => l)
            .Should().Equal(new ResultLocation("a.cs", 0, 0));
    }

    [Fact]
    public async Task Symbols_OrderByDisplayString_ThenLocation()
    {
        // Same-named Shared.Program declared by two projects: equal display strings, so
        // the source location decides — ".../Lib.Tests/Tests.cs" before ".../Lib/Lib.cs",
        // since after the shared ".../Lib" prefix '.' (0x2E) sorts before '/' (0x2F).
        const string declares = "namespace Shared { public class Program { public void Run() { } } public class Alpha { } }";
        using var fixture = new RetargetedSolutionFixture(declares, declares);
        var solution = fixture.Workspace.CurrentSolution;
        var lib = (await solution.GetProject(fixture.LibProjectId)!.GetCompilationAsync())!.Assembly;
        var tests = (await solution.GetProject(fixture.TestProjectId)!.GetCompilationAsync())!.Assembly;
        var symbols = new ISymbol[]
        {
            tests.GetTypeByMetadataName("Shared.Program")!,
            lib.GetTypeByMetadataName("Shared.Program")!.GetMembers("Run").Single(),
            lib.GetTypeByMetadataName("Shared.Program")!,
            tests.GetTypeByMetadataName("Shared.Alpha")!
        };

        CanonicalOrder.BySymbol(symbols, s => s)
            .Select(s => (s.ToDisplayString(), s.ContainingAssembly.Name))
            .Should().Equal(
                ("Shared.Alpha", "Lib.Tests"),
                ("Shared.Program", "Lib.Tests"),
                ("Shared.Program", "Lib"),
                ("Shared.Program.Run()", "Lib"));
        SymbolResultComparer.Instance.Compare(symbols[2], symbols[0]).Should().BePositive();
        SymbolResultComparer.Instance.Compare(symbols[0], symbols[0]).Should().Be(0);
        SymbolResultComparer.Instance.Compare(null, symbols[0]).Should().BeNegative();
    }

    [Fact]
    public void SymbolOrderKey_NoLocationSortsFirst_ThenSolutionKeyBreaksTies()
    {
        var metadata = new SymbolOrderKey("N.T", null, "T:N.T");
        var source = new SymbolOrderKey("N.T", new ResultLocation("a.cs", 0, 0), "A|T:N.T");
        var sameSpotOtherKey = new SymbolOrderKey("N.T", new ResultLocation("a.cs", 0, 0), "B|T:N.T");
        var noKey = new SymbolOrderKey("N.T", new ResultLocation("a.cs", 0, 0), null);

        new[] { sameSpotOtherKey, source, metadata, noKey }.OrderBy(k => k)
            .Should().Equal(metadata, noKey, source, sameSpotOtherKey);
    }

    [Fact]
    public void SymbolOrderKey_DisplayStringIsOrdinal()
    {
        var upper = new SymbolOrderKey("N.Zeta", null, null);
        var lower = new SymbolOrderKey("N.alpha", null, null);

        upper.CompareTo(lower).Should().BeNegative("'Z' (0x5A) sorts before 'a' (0x61) ordinally");
    }
}
