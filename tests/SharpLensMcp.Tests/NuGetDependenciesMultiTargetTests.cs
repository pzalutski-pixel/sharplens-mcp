using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_nuget_dependencies reports one entry per project file: a multi-targeted project
// (MultiTargetSolutionFixture's Multi, loaded as Multi(net8.0) and Multi(net10.0)) is one
// entry, evaluated for each target framework, its identical references merged and each
// carrying the frameworks it applies to; a framework-qualified projectName scopes to that
// framework. The project files are real files the in-process MSBuild evaluates (no SDK
// import, so no restore or SDK resolution is involved).
public class NuGetDependenciesMultiTargetTests
{
    private const string MultiProject = """
        <Project>
          <PropertyGroup>
            <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Shared.Package" Version="1.0.0" />
          </ItemGroup>
          <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
            <PackageReference Include="Legacy.Package" Version="2.0.0" />
          </ItemGroup>
        </Project>
        """;

    private const string ConsumerProject = """
        <Project>
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Consumer.Package" Version="3.0.0" />
          </ItemGroup>
        </Project>
        """;

    private static MultiTargetSolutionFixture Create()
    {
        var fixture = new MultiTargetSolutionFixture();
        foreach (var (path, content) in new[] { (fixture.MultiProjectFilePath, MultiProject), (fixture.ConsumerProjectFilePath, ConsumerProject) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        return fixture;
    }

    private static List<string> Projects(JToken data) =>
        ((JArray)data["projects"]!).Select(p =>
            $"{p["projectName"]} [{string.Join(",", p["targetFrameworks"]?.Values<string>() ?? Enumerable.Empty<string>())}]: " +
            string.Join("; ", ((JArray)p["packages"]!).Select(package =>
                $"{package["packageName"]} {package["version"]} [{string.Join(",", package["targetFrameworks"]?.Values<string>() ?? Enumerable.Empty<string>())}]")))
        .ToList();

    [Fact]
    public async Task MultiTargetedProject_IsOneEntry_WithEachPackageMergedAcrossFrameworks()
    {
        using var fixture = Create();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);

        var json = JObject.FromObject(await service.GetNuGetDependenciesAsync());

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        Projects(json["data"]!).Should().Equal(
            "Consumer []: Consumer.Package 3.0.0 []",
            "Multi [net10.0,net8.0]: Shared.Package 1.0.0 [net10.0,net8.0]; Legacy.Package 2.0.0 [net8.0]");
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(2);
    }

    [Fact]
    public async Task FrameworkQualifiedName_ScopesToThatFramework()
    {
        using var fixture = Create();
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);

        var json = JObject.FromObject(await service.GetNuGetDependenciesAsync(MultiTargetSolutionFixture.Net10ProjectName));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        Projects(json["data"]!).Should().Equal("Multi [net10.0]: Shared.Package 1.0.0 [net10.0]");
    }

    [Fact]
    public async Task MultiTargetedProject_IsOneEntry_ViaMcp()
    {
        using var fixture = Create();
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(fixture.Workspace);
        var ops = new McpHarnessOps(server);

        var inner = await ops.CallToolAsync("get_nuget_dependencies", new { projectName = "multi" });
        var unknown = await ops.CallToolAsync("get_nuget_dependencies", new { projectName = "Nope" });

        inner["success"]!.Value<bool>().Should().BeTrue(inner.ToString());
        Projects(inner["data"]!).Should().Equal(
            "Multi [net10.0,net8.0]: Shared.Package 1.0.0 [net10.0,net8.0]; Legacy.Package 2.0.0 [net8.0]");
        unknown["success"]!.Value<bool>().Should().BeFalse();
        unknown["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        unknown["error"]!["message"]!.Value<string>().Should().Be("Project not found: Nope");
    }
}
