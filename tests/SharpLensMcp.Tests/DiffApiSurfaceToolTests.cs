using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// End-to-end diff_api_surface through the real git path: the baseline is
// rebuilt from `git show HEAD:...` for the src project and diffed against the
// live compilation. Scoped to the src project because parallel test classes
// transiently mutate fixture files in the TESTS project, which would make a
// HEAD comparison of that project flaky.
public class DiffApiSurfaceToolTests : RoslynServiceTestBase
{
    [Fact]
    public async Task DiffAgainstHead_SrcProject_ReportsNoBreakingChanges()
    {
        var result = await Service.DiffApiSurfaceAsync("HEAD", "SharpLensMcp");

        var data = GetData(result);
        data["baselineRef"]!.Value<string>().Should().Be("HEAD");
        data["projectName"]!.Value<string>().Should().Be("SharpLensMcp");

        var compared = data["projectsCompared"] as JArray;
        compared.Should().NotBeNull();
        compared!.Count.Should().Be(1);
        compared[0]["project"]!.Value<string>().Should().Be("SharpLensMcp");
        compared[0]["baselineFileCount"]!.Value<int>().Should().BeGreaterThan(20,
            "the src project's .cs files at HEAD must all parse into the baseline");
        compared[0]["baselineSurfaceCount"]!.Value<int>().Should().BeGreaterThan(50,
            "RoslynService alone exposes dozens of public tool methods");

        // The working tree may contain not-yet-committed ADDITIONS (new public
        // tools mid-development), but the public surface at HEAD must survive:
        // this is the API-compatibility canary the tool exists for.
        data["breakingCount"]!.Value<int>().Should().Be(0,
            $"no public API at HEAD may be removed or changed; got: {data["breaking"]}");
        (data["breaking"] as JArray)!.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownBaselineRef_ReturnsInvalidParameter()
    {
        var result = await Service.DiffApiSurfaceAsync("definitely-not-a-ref-xyz", "SharpLensMcp");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("definitely-not-a-ref-xyz");
    }

    [Fact]
    public async Task UnknownProjectName_ReturnsInvalidParameterListingProjects()
    {
        var result = await Service.DiffApiSurfaceAsync("HEAD", "NoSuchProject");

        // An unknown project name is an invalid argument, routed through the shared
        // ProjectNotFoundError like every other tool — not FileNotFound.
        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Hint"]!.Value<string>().Should().Contain("SharpLensMcp",
            "the error must list the available project names");
    }
}
