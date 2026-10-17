using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// Reading NuGet's restore log from project.assets.json. The files are written by the test
// in the format NuGet produces (verified against real restores: a NU1903 audit warning
// carries code/level/warningLevel/message/libraryId/targetGraphs; a failed NU1101 restore
// carries code/level/message/libraryId/targetGraphs) — no network, no real restore.
public class ProjectAssetsReaderTests : IDisposable
{
    private readonly TempSolutionDirectory _dir = new("assets");

    public void Dispose() => _dir.Dispose();

    private static string AssetsJson(string projectPath, string logsJson) =>
        $$"""
        {
          "version": 3,
          "targets": { "net8.0": {} },
          "libraries": {},
          "project": { "version": "1.0.0", "restore": { "projectPath": {{JsonSerializer.Serialize(projectPath)}}, "projectName": "Lib" } },
          "logs": {{logsJson}}
        }
        """;

    [Fact]
    public void DefaultObjFolder_EntriesAreReadWithCodeLevelAndMessage()
    {
        var project = _dir.Write("Lib/Lib.csproj", "");
        _dir.Write("Lib/obj/project.assets.json", AssetsJson(project, """
            [
              { "code": "NU1903", "level": "Warning", "warningLevel": 1, "message": "Package 'Newtonsoft.Json' 12.0.3 has a known high severity vulnerability", "libraryId": "Newtonsoft.Json", "targetGraphs": [ "net8.0" ] },
              { "code": "NU1101", "level": "Error", "message": "Unable to find package Missing.Pkg.", "libraryId": "Missing.Pkg", "targetGraphs": [ "net8.0" ] }
            ]
            """));

        var (entries, warning) = ProjectAssetsReader.Read(project, intermediateAssemblyPath: null);

        warning.Should().BeNull();
        entries.Should().Equal(
            new RestoreLogEntry(project, "NU1903", "Warning", "Package 'Newtonsoft.Json' 12.0.3 has a known high severity vulnerability"),
            new RestoreLogEntry(project, "NU1101", "Error", "Unable to find package Missing.Pkg."));
    }

    [Fact]
    public void RedirectedIntermediateOutput_FindsTheAssetsFileAboveIt()
    {
        // Artifacts-style layout: obj output under artifacts/obj/Lib/debug/, the assets
        // file under artifacts/obj/Lib/.
        var project = _dir.Write("src/Lib/Lib.csproj", "");
        _dir.Write("artifacts/obj/Lib/project.assets.json", AssetsJson(project,
            """[ { "code": "NU1603", "level": "Warning", "message": "resolved a higher version" } ]"""));

        var (entries, _) = ProjectAssetsReader.Read(project, _dir.PathOf("artifacts/obj/Lib/debug/Lib.dll"));

        entries.Should().Equal(new RestoreLogEntry(project, "NU1603", "Warning", "resolved a higher version"));
    }

    [Fact]
    public void AssetsFileOfAnotherProject_IsIgnored()
    {
        var project = _dir.Write("Lib/Lib.csproj", "");
        _dir.Write("Lib/obj/project.assets.json", AssetsJson(_dir.PathOf("Other/Other.csproj"),
            """[ { "code": "NU1101", "level": "Error", "message": "not mine" } ]"""));

        ProjectAssetsReader.Read(project, null).Entries.Should().BeEmpty();
    }

    [Fact]
    public void NoAssetsFile_YieldsNoEntriesAndNoWarning()
    {
        var project = _dir.Write("Lib/Lib.csproj", "");

        var (entries, warning) = ProjectAssetsReader.Read(project, null);

        entries.Should().BeEmpty();
        warning.Should().BeNull();
    }

    [Fact]
    public void MalformedAssetsFile_YieldsAWarningNamingIt()
    {
        var project = _dir.Write("Lib/Lib.csproj", "");
        var assets = _dir.Write("Lib/obj/project.assets.json", "{ not json");

        var (entries, warning) = ProjectAssetsReader.Read(project, null);

        entries.Should().BeEmpty();
        warning.Should().StartWith($"Could not read the restore log '{assets}' of '{project}': ");
    }
}
