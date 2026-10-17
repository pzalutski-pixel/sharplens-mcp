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
    public async Task FindReferences_OnFixtureTrackedTarget_ClassifiesCastAndTypeof()
    {
        // Search returns the type-declaration position. find_references finds every use
        // of the type, including `(TrackedTarget)boxed` AND `typeof(TrackedTarget)` in
        // Fixtures.TrackedTarget.Use.
        var (file, line, col) = await LocateAsync("TrackedTarget", kind: "Class");
        var result = await Service.FindReferencesAsync(file, line, col);
        AssertSuccess(result);

        var data = GetData(result);
        var refs = data["references"] as JArray;
        refs.Should().NotBeNullOrEmpty();

        var kinds = refs!.Select(r => r["kind"]?.Value<string>()).ToList();
        kinds.Should().Contain("cast",
            $"the fixture has `(TrackedTarget)boxed`; got kinds: {string.Join(", ", kinds)}");
        kinds.Should().Contain("typeof",
            $"the fixture has `typeof(TrackedTarget)`; got kinds: {string.Join(", ", kinds)}");

        data["totalReferences"].Should().NotBeNull();
        data["totalReferences"]!.Value<int>().Should().Be(refs!.Count,
            "totalReferences must match the array length when no kind filter is applied");
    }

    [Fact]
    public async Task FindReferences_OnTrackedField_ClassifiesAllReadInvocationWriteNameofKinds()
    {
        // The fixture references TrackedField in every classifier branch except
        // "attribute" (which requires a non-wrapped attribute argument — covered
        // by TrackedMarker below).
        var (file, line, col) = await LocateAsync("TrackedField", kind: "Field");
        var result = await Service.FindReferencesAsync(file, line, col);
        AssertSuccess(result);

        var refs = (GetData(result)["references"] as JArray)!;
        var kinds = refs.Select(r => r["kind"]?.Value<string>()).Distinct().ToList();
        kinds.Should().Contain("write",
            $"the fixture has `TrackedField = () => 7`; got kinds: {string.Join(", ", kinds)}");
        kinds.Should().Contain("invocation",
            $"the fixture has `TrackedField()`; got kinds: {string.Join(", ", kinds)}");
        kinds.Should().Contain("read",
            $"the fixture has `var read = TrackedField`; got kinds: {string.Join(", ", kinds)}");
        kinds.Should().Contain("nameof",
            $"the fixture has `nameof(TrackedField)`; got kinds: {string.Join(", ", kinds)}");
    }

    [Fact]
    public async Task FindReferences_OnTrackedMarker_ClassifiesAttribute()
    {
        // TrackedMarker is a const referenced directly in `[Tracked(TrackedMarker)]`.
        // The classifier walks up from the identifier and reaches AttributeSyntax
        // before any other matched kind — returns "attribute".
        var (file, line, col) = await LocateAsync("TrackedMarker", kind: "Field");
        var result = await Service.FindReferencesAsync(file, line, col);
        AssertSuccess(result);

        var refs = (GetData(result)["references"] as JArray)!;
        refs.Should().NotBeNullOrEmpty();
        refs.Any(r => r["kind"]?.Value<string>() == "attribute")
            .Should().BeTrue("TrackedMarker is used directly as an attribute argument");
    }

    [Fact]
    public async Task FindReferences_OnReceiverField_ClassifiesReceiverAsReadNotInvocation()
    {
        // `Receiver.Use()` reads the field to invoke Use(); the reference to Receiver
        // must be "read". The classifier previously labeled it "invocation" because the
        // node sits spatially inside the call's expression.
        var (file, line, col) = await LocateAsync("Receiver", kind: "Field");
        var result = await Service.FindReferencesAsync(file, line, col);
        AssertSuccess(result);

        var refs = (GetData(result)["references"] as JArray)!;
        refs.Should().NotBeEmpty("Receiver is used as a call receiver in CallViaReceiver");
        refs.Should().OnlyContain(r => r["kind"]!.Value<string>() == "read",
            "Receiver's only use is as a call receiver, which is a read — Receiver itself is never invoked");
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
        // TrackedField's only write is `TrackedField = () => 7` — exactly one site.
        refs!.Count.Should().Be(1, "the fixture has exactly one write reference to TrackedField");
        refs[0]!["kind"]!.Value<string>().Should().Be("write");

        // The fixture references TrackedField four ways (nameof, write, invocation,
        // read); the declaration is excluded by SymbolFinder.
        data["totalReferences"]!.Value<int>().Should().Be(unfilteredCount);
        unfilteredCount.Should().Be(4, "nameof + write + invocation + read");
        data["totalReferencesAfterFilter"]!.Value<int>().Should().Be(1,
            "exactly one of the four references is a write");
    }

    [Fact]
    public async Task FindReferences_WithUnknownKindFilter_IsInvalidParameter()
    {
        // An unknown kind is a caller error naming the accepted kinds, never an empty
        // "no such references" success.
        var (file, line, col) = await LocateAsync("TrackedField", kind: "Field");
        var result = await Service.FindReferencesAsync(file, line, col, maxResults: null, kindFilter: "no-such-kind");
        AssertError(result, ErrorCodes.InvalidParameter);

        var error = JObject.FromObject(result)["error"]!;
        error["Context"]!["parameter"]!.Value<string>().Should().Be("kind");
        error["Context"]!["allowedValues"]!.Values<string>().Should().Equal(
            "write", "invocation", "cast", "typeof", "nameof", "attribute", "read");
    }
}
