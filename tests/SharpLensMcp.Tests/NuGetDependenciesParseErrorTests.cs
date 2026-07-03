using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_nuget_dependencies surfaces a malformed props/csproj as a parseError rather
// than swallowing it in the inner XML parse. Uses real temp files because the
// failure is keyed off the project's on-disk FilePath.
public class NuGetDependenciesParseErrorTests : IDisposable
{
    private readonly string _tempDir;

    public NuGetDependenciesParseErrorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "slm_nuget_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task MalformedDirectoryBuildProps_SurfacesParseError()
    {
        var csprojPath = Path.Combine(_tempDir, "App.csproj");
        File.WriteAllText(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        // Unclosed elements — XDocument.Parse throws on this.
        File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"),
            "<Project><ItemGroup><PackageReference Include=\"X\" Version=\"1.0\"></Project>");

        var workspace = new AdhocWorkspace();
        workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "App", "App",
            LanguageNames.CSharp, filePath: csprojPath));

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.GetNuGetDependenciesAsync());
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var project = (json["data"]!["projects"] as JArray)!.Single();
        project["parseError"]!.Type.Should().NotBe(JTokenType.Null,
            "a malformed Directory.Build.props must surface a parse error, not be silently swallowed");
        project["parseError"]!.Value<string>()!.Should().Contain("Directory.Build.props",
            "the surfaced error must name the file that failed to parse");
    }
}
