using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The four fine-grained searches against FineGrainedFixture, each pinned to the
// specific shapes the fixture declares — including the includeDerived behavior
// for throw (derived thrown) and catch (base-clause catches derived).
public class FineGrainedSearchTests : RoslynServiceTestBase
{
    private static List<string> Kinds(JToken data) =>
        (data["matches"] as JArray)!.Select(m => m["kind"]!.Value<string>()!).ToList();

    [Fact]
    public async Task FindTypeInstantiations_FindsExplicitAndImplicitNew()
    {
        var data = GetData(await Service.FindTypeInstantiationsAsync("FineGrainedWidget"));

        var kinds = Kinds(data);
        kinds.Should().HaveCount(2, "Make uses `new FineGrainedWidget()` and MakeImplicit uses `new()`");
        kinds.Should().OnlyContain(k => k == "objectCreation");
        (data["matches"] as JArray)!.Should().OnlyContain(
            m => m["location"]!["filePath"]!.Value<string>()!.EndsWith("FineGrainedFixture.cs"));
    }

    [Fact]
    public async Task FindPatternUsages_FindsIsAsAndSwitchTypePattern()
    {
        var data = GetData(await Service.FindPatternUsagesAsync("FineGrainedWidget"));

        var kinds = Kinds(data);
        kinds.Should().Contain("isExpression", "IsWidget uses `o is FineGrainedWidget`");
        kinds.Should().Contain("asExpression", "AsWidget uses `o as FineGrainedWidget`");
        kinds.Should().Contain("typePattern", "Describe switches on the `FineGrainedWidget` type pattern");
    }

    [Fact]
    public async Task FindThrowSites_FindsTheThrow_Exact()
    {
        var data = GetData(await Service.FindThrowSitesAsync("FineGrainedException", includeDerived: false));

        Kinds(data).Should().ContainSingle().Which.Should().Be("throw");
    }

    [Fact]
    public async Task FindThrowSites_IncludeDerived_FindsDerivedThrowUnderBaseType()
    {
        var data = GetData(await Service.FindThrowSitesAsync("System.Exception", includeDerived: true));

        // The fixture throws FineGrainedException, which derives from Exception.
        (data["matches"] as JArray)!.Select(m => m["lineText"]!.Value<string>())
            .Should().Contain(t => t!.Contains("throw new FineGrainedException"));
    }

    [Fact]
    public async Task FindCatchBlocks_Exact_FindsTheTypedCatch()
    {
        var data = GetData(await Service.FindCatchBlocksAsync("FineGrainedException", includeDerived: false));

        // Exact mode: only `catch (FineGrainedException)`, not the base catch.
        var lines = (data["matches"] as JArray)!.Select(m => m["lineText"]!.Value<string>()).ToList();
        lines.Should().Contain(t => t!.Contains("catch (FineGrainedException)"));
        lines.Should().NotContain(t => t!.Contains("catch (System.Exception)"));
    }

    [Fact]
    public async Task FindCatchBlocks_IncludeDerived_AlsoFindsTheBaseCatch()
    {
        var data = GetData(await Service.FindCatchBlocksAsync("FineGrainedException", includeDerived: true));

        // A `catch (System.Exception)` would catch a FineGrainedException, so the
        // base-typed clause matches too.
        var lines = (data["matches"] as JArray)!.Select(m => m["lineText"]!.Value<string>()).ToList();
        lines.Should().Contain(t => t!.Contains("catch (FineGrainedException)"));
        lines.Should().Contain(t => t!.Contains("catch (System.Exception)"));
    }

    [Fact]
    public async Task UnknownType_ReturnsTypeNotFound()
    {
        var result = await Service.FindTypeInstantiationsAsync("NoSuchTypeAnywhere");

        AssertError(result, ErrorCodes.TypeNotFound);
    }
}
