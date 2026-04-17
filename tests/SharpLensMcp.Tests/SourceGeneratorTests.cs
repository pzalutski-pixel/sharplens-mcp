using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests that verify source generators are run and their output is visible to all tools.
/// Covers GitHub issue #7 where MSBuildWorkspace's default compilation excluded generator output.
///
/// The fixture in <c>SourceGeneratorFixture.cs</c> uses <c>[JsonSerializable]</c> which triggers
/// the in-box System.Text.Json source generator at build time. The consumer references a
/// generator-produced member (<c>FixtureJsonContext.Default.FixtureRecord</c>) — without the
/// fix, MSBuildWorkspace reports a phantom CS0117.
/// </summary>
public class SourceGeneratorTests : RoslynServiceTestBase
{
    private const string TestProjectName = "SharpLensMcp.Tests";
    private const string MainProjectName = "SharpLensMcp";
    private const string ConsumerFileName = "SourceGeneratorFixture.cs";

    private string GetConsumerFilePath()
    {
        var solutionDir = Path.GetDirectoryName(SolutionPath)!;
        return Path.Combine(solutionDir, "tests", "SharpLensMcp.Tests", ConsumerFileName);
    }

    private Project GetTestProject() =>
        Service.GetSolutionForTesting()!.Projects.First(p => p.Name == TestProjectName);

    private Project GetMainProject() =>
        Service.GetSolutionForTesting()!.Projects.First(p => p.Name == MainProjectName);

    [Fact]
    public async Task GetDiagnostics_DoesNotReportPhantomErrors_ForGeneratedSymbols()
    {
        // Core regression test for issue #7: without the fix, referencing a generator-produced
        // symbol triggers CS0117 because the pre-generator compilation doesn't know about it.
        var consumerPath = GetConsumerFilePath();
        var result = await Service.GetDiagnosticsAsync(
            filePath: consumerPath,
            projectPath: null,
            severity: "Error",
            includeHidden: false);

        AssertSuccess(result);
        var data = GetData(result);
        var diagnostics = data["diagnostics"] as JArray ?? new JArray();

        diagnostics.Where(d => d["id"]?.Value<string>() == "CS0117")
            .Should().BeEmpty("generator output should resolve symbol references in consumer code");
    }

    [Fact]
    public async Task GetSourceGenerators_ReturnsActualGeneratedFiles()
    {
        var result = await Service.GetSourceGeneratorsAsync(projectName: TestProjectName);
        AssertSuccess(result);
        var data = GetData(result);
        var projects = data["projects"] as JArray;

        projects.Should().NotBeNullOrEmpty();
        var testProject = projects!.FirstOrDefault(p => p["projectName"]?.Value<string>() == TestProjectName);
        testProject.Should().NotBeNull("test project should be listed because it uses [JsonSerializable]");

        var generatedFiles = testProject!["generatedFiles"] as JArray;
        generatedFiles.Should().NotBeNullOrEmpty("JSON source generator should have produced files");
    }

    [Fact]
    public async Task GetGeneratedCode_ReturnsSourceForGeneratedFile()
    {
        var genResult = await Service.GetSourceGeneratorsAsync(projectName: TestProjectName);
        var genData = GetData(genResult);
        var generatedFiles = genData["projects"]?[0]?["generatedFiles"] as JArray;
        generatedFiles.Should().NotBeNullOrEmpty();

        var anyFileName = generatedFiles![0]!["fileName"]?.Value<string>();
        anyFileName.Should().NotBeNullOrEmpty();

        var result = await Service.GetGeneratedCodeAsync(TestProjectName, anyFileName!);
        AssertSuccess(result);
        var data = GetData(result);

        data["sourceCode"]?.Value<string>().Should().NotBeNullOrEmpty();
        data["lineCount"]?.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetGeneratedCode_WithNonExistentFile_ReturnsError()
    {
        var result = await Service.GetGeneratedCodeAsync(TestProjectName, "DoesNotExist.g.cs");
        AssertError(result);
    }

    [Fact]
    public async Task GetProjectCompilationAsync_CachesPerProject()
    {
        var project = GetTestProject();
        var first = await Service.GetProjectCompilationAsync(project);
        var second = await Service.GetProjectCompilationAsync(project);

        first.Should().NotBeNull();
        second.Should().BeSameAs(first, "repeated calls must return the cached compilation");
    }

    [Fact]
    public async Task GetProjectCompilationAsync_IncludesGeneratedTrees()
    {
        var project = GetTestProject();
        var compilation = await Service.GetProjectCompilationAsync(project);
        compilation.Should().NotBeNull();

        var projectDocPaths = project.Documents
            .Where(d => d.FilePath != null)
            .Select(d => d.FilePath!)
            .ToHashSet();

        var generatedTrees = compilation!.SyntaxTrees
            .Where(t => !projectDocPaths.Contains(t.FilePath))
            .ToList();

        generatedTrees.Should().NotBeEmpty("JSON source generator output must appear in compilation");
    }

    [Fact]
    public async Task GetProjectCompilationAsync_ProjectWithoutGenerators_ReturnsBaseCompilation()
    {
        // The main SharpLensMcp project has no source generators.
        // Verify helper doesn't fail and returns a valid compilation.
        var compilation = await Service.GetProjectCompilationAsync(GetMainProject());
        compilation.Should().NotBeNull();
    }

    [Fact]
    public async Task CompilationCache_InvalidatedAfterSyncDocuments()
    {
        var projectId = GetTestProject().Id;
        _ = await Service.GetProjectCompilationAsync(GetTestProject());

        Service._compilationCache.ContainsKey(projectId)
            .Should().BeTrue("compilation should have been cached");

        // Use Write tool semantics: sync_documents with a real file triggers cache clear.
        var consumerPath = GetConsumerFilePath();
        await Service.SyncDocumentsAsync(new List<string> { consumerPath });

        Service._compilationCache.ContainsKey(projectId)
            .Should().BeFalse("cache should be cleared after sync_documents when changes applied");
    }

    [Fact]
    public async Task SearchSymbols_FindsFixtureRecord()
    {
        // Confirms symbol resolution works for hand-written code in a project with source generators.
        var searchResult = await Service.SearchSymbolsAsync("FixtureRecord", kind: null, maxResults: 10);
        AssertSuccess(searchResult);
        var results = GetData(searchResult)["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
    }
}
