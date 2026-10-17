using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// search_symbols matchMode='fuzzy': Roslyn's IDE pattern matcher does
// camel-hump matching the default glob mode cannot — the pair of RSvc tests
// locks the mode distinction in both directions.
public class FuzzySearchTests : RoslynServiceTestBase
{
    [Fact]
    public async Task Fuzzy_CamelHump_FindsRoslynService()
    {
        // Roslyn's PatternMatcher matches each camel chunk as a hump PREFIX:
        // R + Serv prefix-match the Roslyn + Service humps.
        var result = await Service.SearchSymbolsAsync(
            "RServ", kind: "Class", maxResults: 20, matchMode: "fuzzy");

        var data = GetData(result);
        data["matchMode"]!.Value<string>().Should().Be("fuzzy");
        var names = (data["results"] as JArray)!.Select(r => r["name"]!.Value<string>()).ToList();
        names.Should().Contain("RoslynService",
            "camel-hump 'RServ' must match RoslynService");
    }

    [Fact]
    public async Task Glob_SameQuery_FindsNothing()
    {
        var result = await Service.SearchSymbolsAsync(
            "RServ", kind: "Class", maxResults: 20);

        var data = GetData(result);
        data["matchMode"]!.Value<string>().Should().Be("glob");
        (data["results"] as JArray)!.Should().BeEmpty(
            "'RServ' is not a substring of any class name — only fuzzy mode can hump-match it");
    }

    [Fact]
    public async Task Fuzzy_PaginationFieldsBehaveLikeGlobMode()
    {
        var result = await Service.SearchSymbolsAsync(
            "Fixture", kind: "Class", maxResults: 2, matchMode: "fuzzy");

        var json = JObject.FromObject(result);
        var data = json["data"]!;
        var returned = (data["results"] as JArray)!;
        returned.Count.Should().Be(2, "the solution has far more than two fixture classes");
        data["hasMore"]!.Value<bool>().Should().BeTrue();
        data["pagination"]!["nextOffset"]!.Value<int>().Should().Be(2);
        json["meta"]!["TotalCount"]!.Value<int>().Should().BeGreaterThan(2);
        json["meta"]!["ReturnedCount"]!.Value<int>().Should().Be(2);
    }

    [Fact]
    public async Task UnknownMatchMode_ReturnsInvalidParameter()
    {
        var result = await Service.SearchSymbolsAsync(
            "Anything", kind: null, maxResults: 10, matchMode: "regex");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Be("matchMode must be one of: glob, fuzzy (got 'regex')");
    }
}
