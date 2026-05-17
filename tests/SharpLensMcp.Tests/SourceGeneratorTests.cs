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
        // The diagnostics field must always exist on success; if it's null the response
        // shape is broken and the test should fail loudly rather than passing on absence.
        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull(
            "get_diagnostics success response must include a diagnostics array");

        diagnostics!.Where(d => d["id"]?.Value<string>() == "CS0117")
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
    public async Task GetGeneratedCode_ReturnsSourceContainingFixtureJsonContextDeclaration()
    {
        var genResult = await Service.GetSourceGeneratorsAsync(projectName: TestProjectName);
        var genData = GetData(genResult);
        var generatedFiles = genData["projects"]?[0]?["generatedFiles"] as JArray;
        generatedFiles.Should().NotBeNullOrEmpty();

        // The System.Text.Json source generator emits multiple files; pick the one whose
        // name includes "FixtureJsonContext" so we can assert on its known content.
        var contextFile = generatedFiles!
            .Select(f => f["fileName"]?.Value<string>())
            .FirstOrDefault(name => name != null && name.Contains("FixtureJsonContext"));
        contextFile.Should().NotBeNullOrEmpty(
            "the JSON source generator must emit at least one file mentioning FixtureJsonContext");

        var result = await Service.GetGeneratedCodeAsync(TestProjectName, contextFile!);
        AssertSuccess(result);
        var data = GetData(result);

        var source = data["sourceCode"]?.Value<string>();
        source.Should().NotBeNullOrEmpty("generator output must include actual source text");
        source!.Should().Contain("FixtureJsonContext",
            "the generator output for the FixtureJsonContext file must reference its partial class");
        // The generator emits at least the class shell + an accessor, so the file is
        // multi-line; pinning to >5 catches a regression where the generator emits a stub.
        data["lineCount"]?.Value<int>().Should().BeGreaterThan(5);
    }

    [Fact]
    public async Task GetGeneratedCode_WithNonExistentFile_ReturnsFileNotFound()
    {
        var result = await Service.GetGeneratedCodeAsync(TestProjectName, "DoesNotExist.g.cs");
        AssertError(result, ErrorCodes.FileNotFound);
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
    public async Task SearchSymbols_FindsFixtureRecord_AsRecordType()
    {
        // Confirms symbol resolution works for hand-written code in a project with source generators.
        // FixtureRecord is declared as `public record FixtureRecord(string Name, int Age);` in
        // SourceGeneratorFixture.cs — typeKind must report Record, not Class.
        var searchResult = await Service.SearchSymbolsAsync("FixtureRecord", kind: null, maxResults: 10);
        AssertSuccess(searchResult);
        var results = GetData(searchResult)["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
        var match = results!.FirstOrDefault(r => r["name"]?.Value<string>() == "FixtureRecord");
        match.Should().NotBeNull("FixtureRecord must be locatable by simple name");
        match!["typeKind"]?.Value<string>().Should().Be("Record",
            "GetTypeKindString in RoslynService.cs:59 must classify positional records as Record, not Class");
    }
}
