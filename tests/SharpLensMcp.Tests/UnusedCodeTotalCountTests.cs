using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_code caps the returned list at maxResults but must report the TRUE
// total: totalCount counts every unused symbol (so meta.Truncated fires when the
// cap drops some), independent of the cap. The fixture's DeadCode class is unused,
// as are its two methods — three unused symbols total.
public class UnusedCodeTotalCountTests : RoslynServiceTestBase
{
    private const string Code = @"
namespace Demo;

public static class DeadCode
{
    public static void Alpha() { }
    public static void Beta() { }
}
";

    private (RoslynService service, string projectName) Loaded()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Dead.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, "TestProject");
    }

    [Fact]
    public async Task TotalCount_IsTheTrueTotal_NotCapped()
    {
        var (service, project) = Loaded();

        var full = JObject.FromObject(await service.FindUnusedCodeAsync(
            project, includePrivate: false, includeInternal: false, symbolKindFilter: null, maxResults: 50));
        full["success"]!.Value<bool>().Should().BeTrue(full.ToString());
        full["meta"]!["TotalCount"]!.Value<int>().Should().Be(3,
            "the unused DeadCode class and its two unused methods are three unused symbols");
        full["meta"]!["Truncated"]!.Value<bool>().Should().BeFalse(
            "maxResults=50 returns everything, so nothing is truncated");
        (full["data"]!["unusedSymbols"] as JArray)!.Count.Should().Be(3);
    }

    [Fact]
    public async Task Capped_KeepsTrueTotalAndFlagsTruncated()
    {
        var (service, project) = Loaded();

        var capped = JObject.FromObject(await service.FindUnusedCodeAsync(
            project, includePrivate: false, includeInternal: false, symbolKindFilter: null, maxResults: 1));
        capped["success"]!.Value<bool>().Should().BeTrue(capped.ToString());

        capped["meta"]!["TotalCount"]!.Value<int>().Should().Be(3,
            "totalCount is the true total of unused symbols, independent of maxResults");
        capped["meta"]!["ReturnedCount"]!.Value<int>().Should().Be(1,
            "the returned list is capped at maxResults");
        capped["meta"]!["Truncated"]!.Value<bool>().Should().BeTrue(
            "more unused symbols exist than the cap returned");
        (capped["data"]!["unusedSymbols"] as JArray)!.Count.Should().Be(1);
    }
}
