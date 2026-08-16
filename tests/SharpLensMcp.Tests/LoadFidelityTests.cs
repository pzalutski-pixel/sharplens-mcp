using System.Diagnostics;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// load_solution / health_check must report a partial load, never silently drop projects
// (issue #16). Uses real temp solutions loaded through MSBuildWorkspace — the AdhocWorkspace
// seam can't reproduce MSBuild's drop-on-unresolvable-reference behavior.
public class LoadFidelityTests : IDisposable
{
    private readonly string _tempDir;

    public LoadFidelityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"loadfidelity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private const string MinimalCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";

    // A .sln listing the given projects (name -> relative path). Each gets a distinct GUID.
    private static string SolutionText(params (string name, string path)[] projects)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\n");
        var guids = new List<string>();
        for (var i = 0; i < projects.Length; i++)
        {
            var g = $"{(i + 1):D8}-1111-1111-1111-111111111111".ToUpperInvariant();
            guids.Add(g);
            sb.Append($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projects[i].name}\", \"{projects[i].path}\", \"{{{g}}}\"\nEndProject\n");
        }
        sb.Append("Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\tEndGlobalSection\nEndGlobal\n");
        return sb.ToString();
    }

    private static void Restore(string csproj)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{csproj}\" --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(180_000).Should().BeTrue("dotnet restore must finish");
    }

    private static (int projectCount, int? declared, bool partial, JArray failures) Read(JObject json)
    {
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        return (
            data["projectCount"]!.Value<int>(),
            data["declaredProjectCount"]?.Value<int?>(),
            data["partial"]!.Value<bool>(),
            (JArray)data["loadFailures"]!);
    }

    [Fact]
    public async Task DeclaredProjectDropped_ReportsPartial_WithLowerLoadedCount()
    {
        // The .sln declares two projects, but Ghost.csproj doesn't exist on disk, so MSBuild
        // drops it. declared(2) > loaded — a partial load that must be visible.
        File.WriteAllText(Path.Combine(_tempDir, "Lib.csproj"), MinimalCsproj);
        var sln = Path.Combine(_tempDir, "App.sln");
        File.WriteAllText(sln, SolutionText(("Lib", "Lib.csproj"), ("Ghost", "Ghost.csproj")));

        var service = new RoslynService();
        var result = Read(JObject.FromObject(await service.LoadSolutionAsync(sln)));

        result.partial.Should().BeTrue("a declared project was dropped");
        result.declared.Should().Be(2, "the .sln declares two projects");
        result.projectCount.Should().BeLessThan(2, "Ghost.csproj is missing so it can't load");
        result.failures.Should().NotBeEmpty("the dropped project must surface as a load failure");
    }

    [Fact]
    public async Task UnresolvableProjectReference_SurfacesLoadFailure()
    {
        // The one declared project loads, but its ProjectReference points at a project that
        // doesn't exist — declared==loaded, so only the workspace failure catches it.
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <ProjectReference Include=\"..\\DoesNotExist\\DoesNotExist.csproj\" />\n  </ItemGroup>\n</Project>\n");
        var sln = Path.Combine(_tempDir, "App.sln");
        File.WriteAllText(sln, SolutionText(("App", "App.csproj")));

        var service = new RoslynService();
        var result = Read(JObject.FromObject(await service.LoadSolutionAsync(sln)));

        result.partial.Should().BeTrue("an unresolvable ProjectReference is a load failure");
        result.failures.Select(f => f.Value<string>()!)
            .Should().Contain(m => m.Contains("DoesNotExist"),
                "the failure must name the missing project so the caller can act on it");
    }

    [Fact]
    public async Task FullyResolvableSolution_IsNotPartial()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Lib.csproj"), MinimalCsproj);
        var sln = Path.Combine(_tempDir, "Lib.sln");
        File.WriteAllText(sln, SolutionText(("Lib", "Lib.csproj")));
        Restore(Path.Combine(_tempDir, "Lib.csproj"));

        var service = new RoslynService();
        var result = Read(JObject.FromObject(await service.LoadSolutionAsync(sln)));

        result.partial.Should().BeFalse("every declared project resolved");
        result.failures.Should().BeEmpty();
        result.declared.Should().Be(result.projectCount, "declared and loaded counts agree on a clean load");
    }

    [Fact]
    public void CountDeclaredProjects_Sln_ExcludesSolutionFolders()
    {
        var sln = Path.Combine(_tempDir, "Counted.sln");
        File.WriteAllText(sln,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"A\", \"A.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n" +
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Folder\", \"Folder\", \"{22222222-2222-2222-2222-222222222222}\"\nEndProject\n" +
            "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"B\", \"B.csproj\", \"{33333333-3333-3333-3333-333333333333}\"\nEndProject\n");

        RoslynService.CountDeclaredProjects(sln).Should().Be(2, "two real projects; the solution folder is excluded");
    }

    [Fact]
    public void CountDeclaredProjects_Slnx_CountsProjectElements()
    {
        var slnx = Path.Combine(_tempDir, "Counted.slnx");
        File.WriteAllText(slnx,
            "<Solution>\n  <Project Path=\"A/A.csproj\" />\n  <Project Path=\"B/B.csproj\" />\n  <Project Path=\"C/C.csproj\" />\n</Solution>\n");

        RoslynService.CountDeclaredProjects(slnx).Should().Be(3);
    }

    [Fact]
    public async Task HealthCheck_ReportsPartial_WithoutReloading()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Lib.csproj"), MinimalCsproj);
        var sln = Path.Combine(_tempDir, "App.sln");
        File.WriteAllText(sln, SolutionText(("Lib", "Lib.csproj"), ("Ghost", "Ghost.csproj")));

        var service = new RoslynService();
        await service.LoadSolutionAsync(sln);

        var health = JObject.FromObject(await service.GetHealthCheckAsync());
        var solution = health["data"]!["solution"]!;
        solution["partial"]!.Value<bool>().Should().BeTrue(
            "health_check must expose the partial state without a reload");
        solution["declaredProjects"]!.Value<int>().Should().Be(2);
        ((JArray)solution["loadFailures"]!).Should().NotBeEmpty();
    }

    [Fact]
    public async Task PartialLoad_OverTheWire_ReportsPartial()
    {
        // Fresh McpServer (not the shared fixture) so loading a broken solution can't clobber
        // the collection's loaded solution.
        File.WriteAllText(Path.Combine(_tempDir, "Lib.csproj"), MinimalCsproj);
        var sln = Path.Combine(_tempDir, "App.sln");
        File.WriteAllText(sln, SolutionText(("Lib", "Lib.csproj"), ("Ghost", "Ghost.csproj")));

        var server = new McpServer();
        var request = System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "tools/call",
            @params = new { name = "load_solution", arguments = new { solutionPath = sln } }
        });
        var response = await server.HandleRequestAsync(request);
        var wireOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new RequestId.Converter() }
        };
        var envelope = JObject.Parse(System.Text.Json.JsonSerializer.Serialize(response, wireOptions));
        var inner = JObject.Parse(envelope["result"]!["content"]![0]!["text"]!.Value<string>()!);

        inner["success"]!.Value<bool>().Should().BeTrue();
        inner["data"]!["partial"]!.Value<bool>().Should().BeTrue("the partial flag must round-trip over the wire");
        ((JArray)inner["data"]!["loadFailures"]!).Should().NotBeEmpty();
    }
}
