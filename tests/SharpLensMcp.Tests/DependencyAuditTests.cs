using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_dependencies: an integration run against this repo's own src project. The
// src project is a deliberately interesting subject: the two *.Features packages are
// loaded ONLY via MEF reflection (GetRoslynMefAssemblies), so the compile-time verdict
// truthfully reports them unused - exactly the caveat the schema warns about. The
// evaluated, restore-assets-attributed path is pinned on a deterministic fixture in
// DependencyEvaluationTests.
public class DependencyAuditTests : RoslynServiceTestBase
{
    [Fact]
    public async Task SrcProject_ReportsReflectionLoadedFeaturePackages_AndSkipsBuildOnlyPackages()
    {
        var result = await Service.FindUnusedDependenciesAsync("SharpLensMcp");

        var data = GetData(result);
        var projects = (data["projects"] as JArray)!;
        projects.Count.Should().Be(1);
        var src = projects[0];
        src["projectName"]!.Value<string>().Should().Be("SharpLensMcp");
        src["packagesChecked"]!.Value<int>().Should().Be(8,
            "the src csproj declares exactly eight PackageReference entries");
        src["projectReferencesChecked"]!.Value<int>().Should().Be(0);
        (src["unusedProjectReferences"] as JArray)!.Should().BeEmpty();

        // The MEF-reflection-loaded Features packages are the ONLY compile-unused
        // entries - and the reason the schema tells callers to verify first.
        var unused = (src["unusedPackages"] as JArray)!.Select(u => u["name"]!.Value<string>()).ToList();
        unused.Should().BeEquivalentTo(
            "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features");

        // The Microsoft.Build packages are PrivateAssets=all but keep their compile assets
        // (only runtime is excluded): they are judged, and compile-used. SourceLink is a
        // build-only package with no compile assets — the one entry that cannot be judged.
        var skipped = (src["skipped"] as JArray)!;
        skipped.Select(s => $"{s["name"]}: {s["reason"]}").Should().Equal(
            "Microsoft.SourceLink.GitHub: Contributes no compile-time assemblies (analyzer, source generator, or build asset)");
    }

    [Fact]
    public async Task UnknownProject_ReturnsInvalidParameterListingProjects()
    {
        var result = await Service.FindUnusedDependenciesAsync("NoSuchProject");

        // An unknown project name is an invalid argument, routed through the shared
        // ProjectNotFoundError like every other tool — not FileNotFound.
        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Hint"]!.Value<string>().Should().Contain("SharpLensMcp");
    }
}
