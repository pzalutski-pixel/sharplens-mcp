using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Exercises the find_references kind classifier: cast detection and the
// optional server-side kind filter. Driven by ReferenceKindsFixture which
// uses TrackedField + TrackedTarget in every classifier branch.
public class FindReferencesKindTests : RoslynServiceTestBase
{
    private async Task<(string filePath, int line, int column)> LocateAsync(string symbolName, string kind)
    {
        var searchResult = await Service.SearchSymbolsAsync(symbolName, kind: kind, maxResults: 20);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty($"fixture must declare {symbolName}");
        var match = symbols!.First(s => s["name"]?.Value<string>() == symbolName);
        var loc = match["location"]!;
        return (loc["filePath"]!.Value<string>()!, loc["line"]!.Value<int>(), loc["column"]!.Value<int>());
    }

    [Fact]
    public async Task FindReferences_OnFixtureTrackedTarget_ClassifiesCast()
    {
        // Search returns the type-declaration position. find_references finds every use
        // of the type, including `(TrackedTarget)boxed` in Fixtures.TrackedTarget.Use.
        var (file, line, col) = await LocateAsync("TrackedTarget", kind: "Class");
        var result = await Service.FindReferencesAsync(file, line, col);
        AssertSuccess(result);

        var refs = GetData(result)["references"] as JArray;
        refs.Should().NotBeNullOrEmpty();

        var kinds = refs!.Select(r => r["kind"]?.Value<string>()).ToList();
        kinds.Should().Contain("cast",
            $"the fixture has `(TrackedTarget)boxed`; got kinds: {string.Join(", ", kinds)}");
    }

    [Fact]
    public async Task FindReferences_WithKindFilterWrite_OnlyReturnsWrites()
    {
        // TrackedField is a field with write (`TrackedField = () => 7`), invocation,
        // read, and nameof references. Filtering by "write" must drop all others.
        var (file, line, col) = await LocateAsync("TrackedField", kind: "Field");

        var unfiltered = await Service.FindReferencesAsync(file, line, col);
        var unfilteredCount = GetData(unfiltered)["totalReferences"]!.Value<int>();

        var filtered = await Service.FindReferencesAsync(file, line, col, maxResults: null, kindFilter: "write");
        AssertSuccess(filtered);
        var data = GetData(filtered);

        var refs = data["references"] as JArray;
        refs.Should().NotBeNullOrEmpty(
            "TrackedField has at least one write site in the fixture");
        refs!.All(r => r["kind"]?.Value<string>() == "write")
            .Should().BeTrue("kind filter must drop every non-write reference");

        // Both numbers must be present and filtered <= unfiltered.
        data["totalReferences"]?.Value<int>().Should().Be(unfilteredCount);
        var afterFilter = data["totalReferencesAfterFilter"]?.Value<int>();
        afterFilter.Should().NotBeNull("response with kind filter must include totalReferencesAfterFilter");
        afterFilter!.Value.Should().BeLessThan(unfilteredCount,
            "the fixture has more non-write references than write references");
    }

    [Fact]
    public async Task FindReferences_WithUnknownKindFilter_ReturnsEmptyButReportsTrueTotal()
    {
        var (file, line, col) = await LocateAsync("TrackedField", kind: "Field");
        var result = await Service.FindReferencesAsync(file, line, col, maxResults: null, kindFilter: "no-such-kind");
        AssertSuccess(result);

        var data = GetData(result);
        var refs = data["references"] as JArray;
        refs.Should().NotBeNull();
        refs!.Count.Should().Be(0, "no reference has the kind 'no-such-kind'");

        data["totalReferencesAfterFilter"]?.Value<int>().Should().Be(0);
        data["totalReferences"]?.Value<int>().Should().BeGreaterThan(0,
            "unfiltered total must still reflect all real references");
    }
}
