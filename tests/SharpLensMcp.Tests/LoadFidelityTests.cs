using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// load_solution / health_check must report a partial load, never silently drop projects
// (issue #16), and must not call a complete load partial. Real temp solutions loaded
// through MSBuildWorkspace — the AdhocWorkspace seam can't reproduce MSBuild's behavior.
// Every case is offline: no package downloads, no NuGet advisory database.
public class LoadFidelityTests : IDisposable
{
    private readonly TempSolutionDirectory _dir = new("loadfidelity");

    public void Dispose() => _dir.Dispose();

    private const string MinimalCsproj = TempSolutionDirectory.MinimalCsproj;

    private sealed record LoadResult(int ProjectCount, int? Declared, bool Partial, List<string> Failures, List<string> Warnings);

    private static LoadResult Read(JObject json)
    {
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        var warnings = ((JArray)data["loadWarnings"]!).Select(w => w.Value<string>()!).ToList();
        data["loadWarningCount"]!.Value<int>().Should().Be(warnings.Count, "the count matches the listed warnings");
        return new LoadResult(
            data["projectCount"]!.Value<int>(),
            data["declaredProjectCount"]!.Type == JTokenType.Null ? null : data["declaredProjectCount"]!.Value<int>(),
            data["partial"]!.Value<bool>(),
            ((JArray)data["loadFailures"]!).Select(f => f.Value<string>()!).ToList(),
            warnings);
    }

    private async Task<LoadResult> LoadAsync(string sln) =>
        Read(JObject.FromObject(await new RoslynService().LoadSolutionAsync(sln)));

    [Fact]
    public async Task DeclaredProjectDropped_ReportsPartial_NamingTheMissingProject()
    {
        // The .sln declares two projects, but Ghost.csproj doesn't exist on disk, so MSBuild
        // drops it — a partial load that must be visible.
        _dir.Write("Lib/Lib.csproj", MinimalCsproj);
        var sln = _dir.WriteSolution("App.sln", ("Lib", "Lib\\Lib.csproj"), ("Ghost", "Ghost\\Ghost.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("Lib/Lib.csproj"));

        var result = await LoadAsync(sln);

        result.Partial.Should().BeTrue("a declared project was dropped");
        result.Declared.Should().Be(2, "the .sln declares two projects");
        result.ProjectCount.Should().Be(1, "Ghost.csproj is missing so it can't load");
        result.Failures.Should().Contain(
            $"Project '{_dir.PathOf("Ghost/Ghost.csproj")}' is declared in the solution but did not load.");
    }

    [Fact]
    public async Task UnresolvableProjectReference_SurfacesLoadFailure()
    {
        // The one declared project loads, but its ProjectReference points at a project that
        // doesn't exist — the workspace's "project file not found" stays a failure.
        _dir.Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <ProjectReference Include=\"..\\DoesNotExist\\DoesNotExist.csproj\" />\n  </ItemGroup>\n</Project>\n");
        var sln = _dir.WriteSolution("App.sln", ("App", "App\\App.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("App/App.csproj"));

        var result = await LoadAsync(sln);

        result.Partial.Should().BeTrue("an unresolvable ProjectReference is a load failure");
        result.Failures.Should().Contain(m => m.Contains("DoesNotExist.csproj") && !m.StartsWith("Msbuild failed"),
            "the workspace-originated failure must name the missing project");
    }

    [Fact]
    public async Task FullyResolvableSolution_IsNotPartial()
    {
        _dir.Write("Lib/Lib.csproj", MinimalCsproj);
        var sln = _dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("Lib/Lib.csproj"));

        var result = await LoadAsync(sln);

        result.Partial.Should().BeFalse("every declared project resolved");
        result.Failures.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
        result.Declared.Should().Be(1);
        result.ProjectCount.Should().Be(1);
    }

    [Theory]
    [InlineData("Warning")]
    [InlineData("Error")]
    public async Task MSBuildMessageDuringTheDesignTimeBuild_OfACompletelyLoadedProject_IsAWarning(string task)
    {
        // MSBuildWorkspace relays every MSBuild message from a project's design-time
        // build as a Failure. The project still loads completely (the build runs with
        // ContinueOnError), so the message is a load warning, not a partial load.
        _dir.Write("Lib/Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n" +
            $"  <Target Name=\"Probe\" BeforeTargets=\"CoreCompile\">\n    <{task} Text=\"SL-PROBE-{task}\" />\n  </Target>\n</Project>\n");
        _dir.Write("Lib/Widget.cs", "namespace Lib;\npublic class Widget { }\n");
        var sln = _dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("Lib/Lib.csproj"));

        var result = await LoadAsync(sln);

        result.Partial.Should().BeFalse($"a design-time <{task}> does not stop the project loading");
        result.Failures.Should().BeEmpty();
        result.Warnings.Should().ContainSingle(w => w.Contains($"SL-PROBE-{task}") && w.Contains(_dir.PathOf("Lib/Lib.csproj")));
    }

    [Fact]
    public async Task ProjectWithAMissingSdk_IsAPlaceholder_AndThePartialLoadNamesIt()
    {
        _dir.Write("Lib/Lib.csproj",
            "<Project Sdk=\"SharpLens.Does.Not.Exist.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        var sln = _dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));

        var result = await LoadAsync(sln);

        result.Partial.Should().BeTrue();
        result.Failures.Should().Contain(
            $"Project '{_dir.PathOf("Lib/Lib.csproj")}' did not load: MSBuild could not evaluate it, so it is an empty placeholder with no documents.");
        result.Failures.Should().Contain(m => m.Contains("SharpLens.Does.Not.Exist.Sdk"),
            "the relayed MSBuild error explaining why is kept");
    }

    [Fact]
    public async Task SolutionWithASharedProject_IsNotPartial()
    {
        // A .shproj is declared but compiles into the projects that import it; the
        // workspace cannot open it and does not need to.
        _dir.Write("Shared/Shared.shproj",
            "<Project ToolsVersion=\"Current\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
            "  <Import Project=\"Shared.projitems\" Label=\"Shared\" />\n</Project>\n");
        _dir.Write("Shared/Shared.projitems",
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <ItemGroup>\n" +
            "    <Compile Include=\"$(MSBuildThisFileDirectory)SharedThing.cs\" />\n  </ItemGroup>\n</Project>\n");
        _dir.Write("Shared/SharedThing.cs", "namespace Shared;\npublic class SharedThing { }\n");
        _dir.Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n" +
            "  <Import Project=\"..\\Shared\\Shared.projitems\" Label=\"Shared\" />\n</Project>\n");
        var sln = _dir.WriteSolution("App.sln", new[]
        {
            ("Shared", "Shared\\Shared.shproj", "D954291E-2A0B-460D-934E-DC6B0785DB48"),
            ("App", "App\\App.csproj", "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC"),
        });
        TempSolutionDirectory.Restore(_dir.PathOf("App/App.csproj"));

        var result = await LoadAsync(sln);

        result.Partial.Should().BeFalse("the shared project's code is loaded through App");
        result.Failures.Should().BeEmpty();
        result.Declared.Should().Be(1, "only App compiles on its own");
        result.Warnings.Should().ContainSingle(w => w.Contains(_dir.PathOf("Shared/Shared.shproj")));
    }

    [Fact]
    public async Task UnrestoredProject_IsPartial_WithARestoreFailure()
    {
        // Without restore the project has no project.assets.json. Whether its framework
        // references still resolve depends on the SDK installation, so the missing restore
        // output itself is the failure, the same on every machine.
        _dir.Write("Lib/Lib.csproj", MinimalCsproj);
        _dir.Write("Lib/Widget.cs", "namespace Lib;\npublic class Widget { }\n");
        var sln = _dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));

        var result = await LoadAsync(sln);

        result.Partial.Should().BeTrue();
        result.Failures.Should().Equal(
            $"Project '{_dir.PathOf("Lib/Lib.csproj")}' has not been restored: it has no project.assets.json, so its package references are missing; run 'dotnet restore' and reload.");
    }

    [Fact]
    public async Task FailedRestore_OfAProjectThatOtherwiseLoads_IsPartial_WithTheRestoreError()
    {
        // A PackageReference restore that failed (NU1101) offline: the only source is an
        // empty local folder. The project still loads (framework references come from the
        // SDK); the assets file records the Error the relayed diagnostic lost.
        Directory.CreateDirectory(_dir.PathOf("emptyfeed"));
        _dir.Write("Lib/Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n" +
            "    <RestoreSources>$(MSBuildProjectDirectory)/../emptyfeed</RestoreSources>\n    <NuGetAudit>false</NuGetAudit>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <PackageReference Include=\"SharpLens.Definitely.Missing.Pkg\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");
        _dir.Write("Lib/Widget.cs", "namespace Lib;\npublic class Widget { }\n");
        var sln = _dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));
        var (exitCode, output) = TempSolutionDirectory.RunDotnet(_dir.Root, "restore", _dir.PathOf("Lib/Lib.csproj"), "--nologo");
        exitCode.Should().NotBe(0, $"the restore must fail for this test to mean anything; output: {output}");
        output.Should().Contain("NU1101");

        var result = await LoadAsync(sln);

        result.Partial.Should().BeTrue();
        result.Failures.Should().ContainSingle(f =>
            f.StartsWith($"Restore NU1101 in '{_dir.PathOf("Lib/Lib.csproj")}': ") && f.Contains("SharpLens.Definitely.Missing.Pkg"));
        result.Warnings.Should().NotContain(w => w.Contains("SharpLens.Definitely.Missing.Pkg"),
            "the relayed copy of the restore error is superseded, not repeated as a warning");
    }

    [Fact]
    public async Task MultiTargetedProject_LoadsPerFramework_AndIsComplete()
    {
        // One declared project, two workspace projects (one per target framework).
        _dir.Write("Lib/Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>\n  </PropertyGroup>\n</Project>\n");
        _dir.Write("Lib/Widget.cs", "namespace Lib { public class Widget { } }\n");
        var sln = _dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("Lib/Lib.csproj"));

        var result = await LoadAsync(sln);

        result.ProjectCount.Should().Be(2);
        result.Declared.Should().Be(1);
        result.Partial.Should().BeFalse();
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task HealthCheck_ReportsPartialAndWarnings_WithoutReloading()
    {
        _dir.Write("Lib/Lib.csproj", MinimalCsproj);
        var sln = _dir.WriteSolution("App.sln", ("Lib", "Lib\\Lib.csproj"), ("Ghost", "Ghost\\Ghost.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("Lib/Lib.csproj"));

        var service = new RoslynService();
        var load = Read(JObject.FromObject(await service.LoadSolutionAsync(sln)));

        var health = JObject.FromObject(await service.GetHealthCheckAsync());
        var solution = health["data"]!["solution"]!;
        solution["partial"]!.Value<bool>().Should().BeTrue(
            "health_check must expose the partial state without a reload");
        solution["declaredProjects"]!.Value<int>().Should().Be(2);
        ((JArray)solution["loadFailures"]!).Select(f => f.Value<string>()).Should().Equal(load.Failures);
        ((JArray)solution["loadWarnings"]!).Select(w => w.Value<string>()).Should().Equal(load.Warnings);
    }

    [Fact]
    public async Task CompleteLoadWithWarnings_OverTheWire_ReportsLoadWarnings_AndIsNotPartial()
    {
        // Fresh McpServer (not the shared fixture) so this load can't clobber the
        // collection's loaded solution.
        _dir.Write("Lib/Lib.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n" +
            "  <Target Name=\"Probe\" BeforeTargets=\"CoreCompile\">\n    <Warning Text=\"SL-PROBE-WIRE\" />\n  </Target>\n</Project>\n");
        var sln = _dir.WriteSolution("Lib.sln", ("Lib", "Lib\\Lib.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("Lib/Lib.csproj"));
        var harness = new Mcp.McpHarnessOps(new McpServer());

        var inner = await harness.CallToolAsync("load_solution", new { solutionPath = sln });

        inner["success"]!.Value<bool>().Should().BeTrue();
        inner["data"]!["partial"]!.Value<bool>().Should().BeFalse("a design-time warning is not a dropped project");
        ((JArray)inner["data"]!["loadFailures"]!).Should().BeEmpty();
        ((JArray)inner["data"]!["loadWarnings"]!).Select(w => w.Value<string>()!)
            .Should().ContainSingle(w => w.Contains("SL-PROBE-WIRE"));
        inner["data"]!["loadWarningCount"]!.Value<int>().Should().Be(1);

        var health = await harness.CallToolAsync("health_check");
        ((JArray)health["data"]!["solution"]!["loadWarnings"]!).Select(w => w.Value<string>()!)
            .Should().ContainSingle(w => w.Contains("SL-PROBE-WIRE"));
    }

    [Fact]
    public async Task PartialLoad_OverTheWire_ReportsPartial()
    {
        _dir.Write("Lib/Lib.csproj", MinimalCsproj);
        var sln = _dir.WriteSolution("App.sln", ("Lib", "Lib\\Lib.csproj"), ("Ghost", "Ghost\\Ghost.csproj"));
        TempSolutionDirectory.Restore(_dir.PathOf("Lib/Lib.csproj"));
        var harness = new Mcp.McpHarnessOps(new McpServer());

        var inner = await harness.CallToolAsync("load_solution", new { solutionPath = sln });

        inner["success"]!.Value<bool>().Should().BeTrue();
        inner["data"]!["partial"]!.Value<bool>().Should().BeTrue("the partial flag must round-trip over the wire");
        ((JArray)inner["data"]!["loadFailures"]!).Select(f => f.Value<string>()!).Should().Contain(
            $"Project '{_dir.PathOf("Ghost/Ghost.csproj")}' is declared in the solution but did not load.");
    }
}
