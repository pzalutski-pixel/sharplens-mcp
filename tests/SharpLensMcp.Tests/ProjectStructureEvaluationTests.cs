using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_project_structure reports the EVALUATED target framework (set in Directory.Build.props,
// or through a property such as $(MyTfm)), lists a multi-targeted project once with its
// frameworks, counts truncation against the projects the pattern matched (a filter is not
// a truncation), reports the solution path the way every path is reported, and rejects a
// non-positive maxProjects.
public class ProjectStructureEvaluationTests
{
    private const string DirectoryBuildProps = """
        <Project>
          <PropertyGroup>
            <MyTfm>net8.0</MyTfm>
          </PropertyGroup>
          <PropertyGroup Condition="'$(MSBuildProjectName)' == 'T'">
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        File.WriteAllText(Path.Combine(builder.Directory, "Directory.Build.props"), DirectoryBuildProps);
        builder.AddProject("T", new[] { ("C.cs", "namespace T { public class C { } }") });
        builder.AddProject("U", new[] { ("C.cs", "namespace U { public class C { } }") });
        builder.AddMultiTargeted("Multi", new[] { ("M.cs", "namespace Multi { public class M { } }") });
        Directory.CreateDirectory(Path.GetDirectoryName(builder.ProjectFilePath("T"))!);
        File.WriteAllText(builder.ProjectFilePath("T"), "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
        Directory.CreateDirectory(Path.GetDirectoryName(builder.ProjectFilePath("U"))!);
        File.WriteAllText(builder.ProjectFilePath("U"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>$(MyTfm)</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    private static List<string> Rows(JToken data) =>
        ((JArray)data["projects"]!).Select(p =>
            $"{p["name"]} {p["filePath"]} {p["targetFramework"]} [{string.Join(",", (p["targetFrameworks"] as JArray)?.Values<string>() ?? Array.Empty<string>())}]").ToList();

    [Fact]
    public async Task TargetFramework_IsTheEvaluatedValue_AndAMultiTargetedProjectIsListedOnce()
    {
        using var builder = Build(out var service, out _);

        var json = JObject.FromObject(await service.GetProjectStructureAsync(includeReferences: false, includeDocuments: false));
        var data = json["data"]!;

        Rows(data).Should().Equal(
            "Multi Multi/Multi.csproj  [net10.0,net8.0]",
            "T T/T.csproj net8.0 []",
            "U U/U.csproj net8.0 []");
        data["solutionPath"]!.Value<string>().Should().Be("Quality.sln");
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(3);
        json["meta"]!["Truncated"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task NamePattern_IsAFilter_NotATruncation_ButMaxProjectsIs()
    {
        using var builder = Build(out var service, out _);

        var filtered = JObject.FromObject(await service.GetProjectStructureAsync(false, false, projectNamePattern: "T"));
        Rows(filtered["data"]!).Should().Equal("T T/T.csproj net8.0 []");
        filtered["meta"]!["TotalCount"]!.Value<int>().Should().Be(1);
        filtered["meta"]!["Truncated"]!.Value<bool>().Should().BeFalse();

        var capped = JObject.FromObject(await service.GetProjectStructureAsync(false, false, maxProjects: 1));
        Rows(capped["data"]!).Should().Equal("Multi Multi/Multi.csproj  [net10.0,net8.0]");
        capped["meta"]!["TotalCount"]!.Value<int>().Should().Be(3);
        capped["meta"]!["ReturnedCount"]!.Value<int>().Should().Be(1);
        capped["meta"]!["Truncated"]!.Value<bool>().Should().BeTrue();

        var summary = JObject.FromObject(await service.GetProjectStructureAsync(false, false, summaryOnly: true));
        ((JArray)summary["data"]!["projects"]!).Select(p => p["name"]!.Value<string>()).Should().Equal("Multi", "T", "U");
    }

    [Fact]
    public async Task NonPositiveMaxProjects_IsAnInvalidParameter()
    {
        using var builder = Build(out var service, out _);

        var json = JObject.FromObject(await service.GetProjectStructureAsync(false, false, maxProjects: 0));

        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be("maxProjects must be between 1 and 10000 (got 0)");
    }

    [Fact]
    public async Task ViaDispatcher_EvaluatedFrameworks_PatternCounts_AndInvalidMaxProjects()
    {
        using var builder = Build(out _, out var dispatcher);

        var all = await dispatcher.DataAsync("get_project_structure", new { includeReferences = false });
        Rows(all).Should().Equal(
            "Multi Multi/Multi.csproj  [net10.0,net8.0]",
            "T T/T.csproj net8.0 []",
            "U U/U.csproj net8.0 []");

        var filtered = await dispatcher.CallAsync("get_project_structure", new { includeReferences = false, projectNamePattern = "U" });
        filtered["meta"]!["truncated"]!.Value<bool>().Should().BeFalse();
        filtered["meta"]!["totalCount"]!.Value<int>().Should().Be(1);

        var error = await dispatcher.ErrorAsync("get_project_structure", new { maxProjects = -1 }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("maxProjects must be between 1 and 10000 (got -1)");
    }
}
