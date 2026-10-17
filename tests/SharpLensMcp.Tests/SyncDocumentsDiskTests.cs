using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// sync_documents against a real MSBuild-loaded solution on disk: it detects real
// changes (an untouched file is "unchanged", not "updated"), decodes each file with
// its actual encoding, adds only files a project's compile items include, assigns a
// new file to the project that compiles it, and rejects malformed or unknown paths.
public class SyncDocumentsDiskTests
{
    private static async Task<RoslynService> LoadAsync(DiskSolution solution)
    {
        var service = new RoslynService();
        var load = JObject.FromObject(await service.LoadSolutionAsync(solution.SolutionPath));
        load["success"]!.Value<bool>().Should().BeTrue(load.ToString());
        return service;
    }

    private static async Task<JToken> SyncDataAsync(RoslynService service, List<string>? paths)
    {
        var result = JObject.FromObject(await service.SyncDocumentsAsync(paths));
        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        return result["data"]!;
    }

    private static string[] Strings(JToken token) => ((JArray)token).Select(t => t.Value<string>()!).ToArray();

    [Fact]
    public async Task UnchangedFile_IsReportedUnchanged_NotUpdated()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);
        var before = service.GetSolutionForTesting();

        var data = await SyncDataAsync(service, new List<string> { "App/Other.cs" });

        data["updated"]!.Value<int>().Should().Be(0, "the file on disk equals the loaded text");
        data["unchanged"]!.Value<int>().Should().Be(1);
        data["totalSynced"]!.Value<int>().Should().Be(0);
        Strings(data["updatedFiles"]!).Should().BeEmpty();
        service.GetSolutionForTesting().Should().BeSameAs(before,
            "a sync that finds no change must leave the solution snapshot untouched");
    }

    [Fact]
    public async Task ChangedFile_IsReportedUpdated_WithTheDiskText()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);
        var app = service.GetSolutionForTesting()!.Projects.Single(p => p.Name == "App");
        (await service.GetProjectCompilationAsync(app)).Should().NotBeNull();
        service._compilationCache.ContainsKey(app.Id).Should().BeTrue();
        solution.WriteText("App/Other.cs", ApplySyncFixture.Other + "// edited\n");

        var data = await SyncDataAsync(service, new List<string> { "App/Other.cs" });

        service._compilationCache.Should().BeEmpty("a sync that changes a document invalidates every cached compilation");

        data["updated"]!.Value<int>().Should().Be(1);
        data["unchanged"]!.Value<int>().Should().Be(0);
        Strings(data["updatedFiles"]!).Should().Equal("App/Other.cs");
        var document = service.GetSolutionForTesting()!.Projects
            .SelectMany(p => p.Documents).Single(d => d.FilePath == solution.PathOf("App/Other.cs"));
        (await document.GetTextAsync()).ToString().Should().Be(ApplySyncFixture.Other + "// edited\n");
    }

    [Fact]
    public async Task EmptyPathList_IsInvalidParameter()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.SyncDocumentsAsync(new List<string>()));

        result["success"]!.Value<bool>().Should().BeFalse("an empty list names no file; omit filePaths to sync all");
        result["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task BlankPathEntry_IsInvalidParameter()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.SyncDocumentsAsync(new List<string> { "" }));

        result["success"]!.Value<bool>().Should().BeFalse("a blank path names no file");
        result["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task PathNeitherOnDiskNorInSolution_IsReportedAsAnError()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);

        var data = await SyncDataAsync(service, new List<string> { "App/Typo.cs" });

        data["totalSynced"]!.Value<int>().Should().Be(0);
        var errors = (JArray)data["errors"]!;
        errors.Should().HaveCount(1);
        errors[0]["path"]!.Value<string>().Should().Be("App/Typo.cs");
        errors[0]["error"]!.Value<string>().Should().Be("File not found on disk and not in the solution");
    }

    [Fact]
    public async Task Windows1252File_IsDecodedWithItsEncoding_AndIsUnchanged()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteBytes("App/Latin.cs", ApplySyncFixture.Windows1252.GetBytes(ApplySyncFixture.Latin));
        solution.Restore();
        var service = await LoadAsync(solution);

        var data = await SyncDataAsync(service, null);

        data["updated"]!.Value<int>().Should().Be(0, "no file changed on disk since the load");
        Strings(data["updatedFiles"]!).Should().BeEmpty();
        var document = service.GetSolutionForTesting()!.Projects
            .SelectMany(p => p.Documents).Single(d => d.FilePath == solution.PathOf("App/Latin.cs"));
        (await document.GetTextAsync()).ToString().Should().Be(ApplySyncFixture.Latin,
            "the windows-1252 'é' must decode as 'é', not U+FFFD");
    }

    [Fact]
    public async Task FullSync_AddsANewFileTheCompileGlobsInclude()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);
        solution.WriteText("App/Models/NewOne.cs", "namespace App.Models;\n\npublic class NewOne\n{\n}\n");

        var data = await SyncDataAsync(service, null);

        data["added"]!.Value<int>().Should().Be(1);
        Strings(data["addedFiles"]!).Should().Equal("App/Models/NewOne.cs");
        data["updated"]!.Value<int>().Should().Be(0);
        data["removed"]!.Value<int>().Should().Be(0);
        var added = service.GetSolutionForTesting()!.Projects
            .SelectMany(p => p.Documents).Single(d => d.FilePath == solution.PathOf("App/Models/NewOne.cs"));
        added.Project.Name.Should().Be("App");
        added.Folders.Should().Equal("Models");
    }

    [Fact]
    public async Task FilesOutsideTheCompileItems_AreNeverAdded()
    {
        using var solution = ApplySyncFixture.Create(
            extraAppXml: "\n  <ItemGroup>\n    <Compile Remove=\"Excluded.cs\" />\n  </ItemGroup>\n");
        solution.Restore();
        var service = await LoadAsync(solution);
        solution.WriteText("App/README.md", "# Readme\nThis is not C#.\n");
        solution.WriteText("App/obj/Stray.cs", "namespace App; public class Stray { }\n");
        solution.WriteText("App/Excluded.cs", "namespace App; public class Excluded { }\n");

        var full = await SyncDataAsync(service, null);
        full["added"]!.Value<int>().Should().Be(0, "none of the three files is a compile item of any project");

        var explicitSync = await SyncDataAsync(service,
            new List<string> { "App/README.md", "App/obj/Stray.cs", "App/Excluded.cs" });
        explicitSync["added"]!.Value<int>().Should().Be(0);
        ((JArray)explicitSync["errors"]!).Select(e => (e["path"]!.Value<string>(), e["error"]!.Value<string>()))
            .Should().Equal(
                ("App/README.md", "Not a C# source file (.cs)"),
                ("App/obj/Stray.cs", "No project in the solution includes this file in its compile items"),
                ("App/Excluded.cs", "No project in the solution includes this file in its compile items"));
        service.GetSolutionForTesting()!.Projects.SelectMany(p => p.Documents)
            .Select(d => Path.GetFileName(d.FilePath)).Should().NotContain(new[] { "README.md", "Stray.cs", "Excluded.cs" });
    }

    [Fact]
    public async Task NewFileInASiblingDirectorySharingAPrefix_IsNotAssignedToThePrefixProject()
    {
        // AppExtra/ has no project: App's directory is a string prefix of AppExtra's, but
        // App's compile items do not include the file, so no project takes it.
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);
        solution.WriteText("AppExtra/Stray.cs", "namespace Stray;\n\npublic class Stray\n{\n}\n");

        var data = await SyncDataAsync(service, new List<string> { "AppExtra/Stray.cs" });

        data["added"]!.Value<int>().Should().Be(0);
        ((JArray)data["errors"]!).Select(e => (e["path"]!.Value<string>(), e["error"]!.Value<string>()))
            .Should().Equal(("AppExtra/Stray.cs", "No project in the solution includes this file in its compile items"));
        service.GetSolutionForTesting()!.Projects.SelectMany(p => p.Documents)
            .Select(d => Path.GetFileName(d.FilePath)).Should().NotContain("Stray.cs");
    }

    [Fact]
    public async Task NewFileInASiblingProjectSharingAPrefix_GoesToTheProjectThatCompilesIt()
    {
        using var solution = new DiskSolution("App/App.csproj", "AppExtra/AppExtra.csproj");
        solution.WriteText("App/App.csproj", DiskSolution.SdkProject("net8.0"));
        solution.WriteText("AppExtra/AppExtra.csproj", DiskSolution.SdkProject("net8.0"));
        solution.WriteText("App/Other.cs", ApplySyncFixture.Other);
        solution.WriteText("AppExtra/Seed.cs", "namespace AppExtra;\n\npublic class Seed\n{\n}\n");
        solution.Restore();
        var service = await LoadAsync(solution);
        solution.WriteText("AppExtra/Stray.cs", "namespace AppExtra;\n\npublic class Stray\n{\n}\n");

        var data = await SyncDataAsync(service, new List<string> { "AppExtra/Stray.cs" });

        Strings(data["addedFiles"]!).Should().Equal("AppExtra/Stray.cs");
        var added = service.GetSolutionForTesting()!.Projects
            .SelectMany(p => p.Documents).Where(d => d.FilePath == solution.PathOf("AppExtra/Stray.cs")).ToList();
        added.Select(d => d.Project.Name).Should().Equal("AppExtra");
        added[0].Folders.Should().BeEmpty("the file sits directly in the AppExtra project directory");
    }
}
