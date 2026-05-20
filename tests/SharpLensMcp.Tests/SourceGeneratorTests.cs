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

    private Project GetTestProject() => Service.GetSolutionForTesting()!.Projects.First(p => p.Name == TestProjectName);
    private Project GetMainProject() => Service.GetSolutionForTesting()!.Projects.First(p => p.Name == MainProjectName);
    [Fact]
    public async Task GetDiagnostics_DoesNotReportPhantomErrors_ForGeneratedSymbols()
    {
        // Core regression test for issue #7: without the fix, referencing a generator-produced
        // symbol triggers CS0117 because the pre-generator compilation doesn't know about it.
        var consumerPath = GetConsumerFilePath();
        var result = await Service.GetDiagnosticsAsync(filePath: consumerPath, projectPath: null, severity: "Error", includeHidden: false);
        AssertSuccess(result);
        var data = GetData(result);
        // The diagnostics field must always exist on success; if it's null the response
        // shape is broken and the test should fail loudly rather than passing on absence.
        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull("get_diagnostics success response must include a diagnostics array");
        diagnostics!.Where(d => d["id"]?.Value<string>() == "CS0117").Should().BeEmpty("generator output should resolve symbol references in consumer code");
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
    public async Task GetSourceGenerators_GeneratorsArray_LocksJsonSourceGeneratorTypeName()
    {
        // The fixture's [JsonSerializable] attribute on FixtureJsonContext is what
        // triggers the in-box System.Text.Json source generator. The generators[]
        // shape must include both the .NET type name and the assembly. Locking the
        // typeName protects against silent swaps (e.g., to a stub or a different
        // generator that emits unrelated output).
        var result = await Service.GetSourceGeneratorsAsync(projectName: TestProjectName);
        AssertSuccess(result);
        var data = GetData(result);
        var testProject = (data["projects"] as JArray)!.First(p => p["projectName"]!.Value<string>() == TestProjectName);
        var generators = testProject["generators"] as JArray;
        generators.Should().NotBeNullOrEmpty("the test project must report at least one generator — [JsonSerializable] runs the System.Text.Json source generator");
        var typeNames = generators!.Select(g => g["typeName"]!.Value<string>()!).ToList();
        typeNames.Should().Contain("System.Text.Json.SourceGeneration.JsonSourceGenerator", "the in-box JSON source generator's typeName must round-trip exactly through g.GetType().FullName");
        var assemblyNames = generators!.Select(g => g["assemblyName"]!.Value<string>()!).ToList();
        assemblyNames.Should().Contain(a => a.StartsWith("System.Text.Json"), "the JsonSourceGenerator must report its hosting assembly (some form of System.Text.Json.*)");
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
        var contextFile = generatedFiles!.Select(f => f["fileName"]?.Value<string>()).FirstOrDefault(name => name != null && name.Contains("FixtureJsonContext"));
        contextFile.Should().NotBeNullOrEmpty("the JSON source generator must emit at least one file mentioning FixtureJsonContext");
        var result = await Service.GetGeneratedCodeAsync(TestProjectName, contextFile!);
        AssertSuccess(result);
        var data = GetData(result);
        var source = data["sourceCode"]?.Value<string>();
        source.Should().NotBeNullOrEmpty("generator output must include actual source text");
        source!.Should().Contain("FixtureJsonContext", "the generator output for the FixtureJsonContext file must reference its partial class");
        // lineCount field must exist (locked NotBeNull first to defeat the null-conditional
        // silent-pass pattern) and exceed 5 — the generator emits at least the class shell
        // plus an accessor, so the file is multi-line. A regression that emitted a stub
        // would now fail.
        data["lineCount"].Should().NotBeNull();
        data["lineCount"]!.Value<int>().Should().BeGreaterThan(5);
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
        var projectDocPaths = project.Documents.Where(d => d.FilePath != null).Select(d => d.FilePath!).ToHashSet();
        var generatedTrees = compilation!.SyntaxTrees.Where(t => !projectDocPaths.Contains(t.FilePath)).ToList();
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
        Service._compilationCache.ContainsKey(projectId).Should().BeTrue("compilation should have been cached");
        // Use Write tool semantics: sync_documents with a real file triggers cache clear.
        var consumerPath = GetConsumerFilePath();
        await Service.SyncDocumentsAsync(new List<string> { consumerPath });
        Service._compilationCache.ContainsKey(projectId).Should().BeFalse("cache should be cleared after sync_documents when changes applied");
    }

    [Fact]
    public async Task GetTypeOverview_OnFixtureRecord_ClassifiesAsRecord()
    {
        // The previous version of this test asserted match["typeKind"] == "Record" on
        // a search_symbols result, but the search_symbols entry shape (Navigation.cs:661-674)
        // doesn't include a typeKind field — only `kind` ("NamedType" for all types).
        // The `?.Value<string>().Should().Be(...)` chain silently passed because the
        // null-conditional short-circuits when the field is missing. Switched to
        // get_type_overview which DOES emit typeKind (Compound.cs:313).
        var result = await Service.GetTypeOverviewAsync("FixtureRecord");
        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"].Should().NotBeNull();
        data["typeName"]!.Value<string>().Should().Contain("FixtureRecord");
        // GetTypeKindString in RoslynService.cs:59 classifies records as Record (not Class).
        data["typeKind"].Should().NotBeNull();
        data["typeKind"]!.Value<string>().Should().Be("Record", "positional records must be classified as Record, not Class");
    }

    [Fact]
    public async Task SearchSymbols_FindsFixtureRecord_AsNamedTypeWithRecordContainingTypeShape()
    {
        // Companion to the GetTypeOverview test above: confirms search_symbols ITSELF
        // can find a record by simple name. The search entry's `kind` is "NamedType"
        // (the symbol kind, not the type kind) — distinguishing Record vs Class
        // requires a follow-up get_type_overview call (locked above).
        var searchResult = await Service.SearchSymbolsAsync("FixtureRecord", kind: null, maxResults: 10);
        AssertSuccess(searchResult);
        var results = GetData(searchResult)["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
        var match = results!.FirstOrDefault(r => r["name"]?.Value<string>() == "FixtureRecord");
        match.Should().NotBeNull("FixtureRecord must be locatable by simple name");
        match!["kind"].Should().NotBeNull();
        match["kind"]!.Value<string>().Should().Be("NamedType", "search_symbols reports the symbol Kind ('NamedType' for any type), not typeKind");
    }
}