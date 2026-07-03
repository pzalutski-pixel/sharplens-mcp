using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_dependencies: unit coverage for the path->packageId mapper and
// an integration run against this repo's own src project. The src project is
// a deliberately interesting subject: the two *.Features packages are loaded
// ONLY via MEF reflection (GetRoslynMefAssemblies), so the compile-time
// verdict truthfully reports them unused - exactly the caveat the schema
// warns about - while the PrivateAssets entries land in skipped.
public class DependencyAuditTests : RoslynServiceTestBase
{
    [Theory]
    [InlineData(@"C:\Users\x\.nuget\packages\newtonsoft.json\13.0.3\lib\net6.0\Newtonsoft.Json.dll", "newtonsoft.json")]
    [InlineData("/home/user/.nuget/packages/xunit.core/2.6.4/lib/netstandard1.1/xunit.core.dll", "xunit.core")]
    [InlineData(@"C:\Users\x\.NuGet\Packages\FluentAssertions\6.12.0\lib\net6.0\FluentAssertions.dll", "fluentassertions")]
    public void PackageIdFromReferencePath_ExtractsLoweredId(string path, string expected)
    {
        RoslynService.PackageIdFromReferencePath(path).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\8.0.0\ref\net8.0\System.Runtime.dll")]
    [InlineData(@"C:\repo\src\bin\Release\net8.0\MyProject.dll")]
    [InlineData("")]
    [InlineData(null)]
    public void PackageIdFromReferencePath_NonPackagePaths_ReturnNull(string? path)
    {
        RoslynService.PackageIdFromReferencePath(path).Should().BeNull();
    }

    [Fact]
    public async Task SrcProject_ReportsReflectionLoadedFeaturePackages_AndSkipsPrivateAssets()
    {
        var result = await Service.FindUnusedDependenciesAsync("SharpLensMcp");

        var data = GetData(result);
        var projects = (data["projects"] as JArray)!;
        projects.Count.Should().Be(1);
        var src = projects[0];
        src["projectName"]!.Value<string>().Should().Be("SharpLensMcp");
        src["packagesChecked"]!.Value<int>().Should().Be(7,
            "the src csproj declares exactly seven PackageReference entries");
        src["projectReferencesChecked"]!.Value<int>().Should().Be(0);
        (src["unusedProjectReferences"] as JArray)!.Should().BeEmpty();

        // The MEF-reflection-loaded Features packages are the ONLY compile-unused
        // entries - and the reason the schema tells callers to verify first.
        var unused = (src["unusedPackages"] as JArray)!.Select(u => u["name"]!.Value<string>()).ToList();
        unused.Should().BeEquivalentTo(
            "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features");

        var skipped = (src["skipped"] as JArray)!;
        skipped.Select(s => s["name"]!.Value<string>()).Should().BeEquivalentTo(
            "Microsoft.Build.Framework", "Microsoft.SourceLink.GitHub");
        skipped.Select(s => s["reason"]!.Value<string>()).Should().OnlyContain(
            r => r.Contains("PrivateAssets"));
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
