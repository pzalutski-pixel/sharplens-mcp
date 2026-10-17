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
    public async Task GetSourceGenerators_ReportsElapsedMsPerGenerator()
    {
        // "Which generator makes my build slow": every generator entry carries
        // the wall-clock its run cost, from GeneratorDriver.GetTimingInfo on a
        // dedicated timing run with the same driver configuration the server
        // compiles with.
        var result = await Service.GetSourceGeneratorsAsync(projectName: TestProjectName);
        AssertSuccess(result);
        var projects = (GetData(result)["projects"] as JArray)!;
        var testProject = projects.First(p => p["projectName"]!.Value<string>() == TestProjectName);

        var generators = (testProject["generators"] as JArray)!;
        generators.Should().NotBeEmpty("the [JsonSerializable] fixture activates the JSON source generator");
        foreach (var generator in generators)
        {
            generator["elapsedMs"]!.Type.Should().NotBe(JTokenType.Null,
                $"every generator must report its timing; '{generator["typeName"]}' did not");
            generator["elapsedMs"]!.Value<double>().Should().BeGreaterThanOrEqualTo(0);
        }
        generators.Select(g => g["typeName"]!.Value<string>()).Should().Contain(
            t => t!.Contains("Json"), "the JSON source generator must be among the timed entries");
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
    public async Task CompilationCache_KeptAfterSyncingAnUnchangedFile()
    {
        var projectId = GetTestProject().Id;
        var cached = await Service.GetProjectCompilationAsync(GetTestProject());
        Service._compilationCache.ContainsKey(projectId).Should().BeTrue("compilation should have been cached");
        // The consumer file on disk equals the loaded text, so the sync changes nothing and
        // the generator-augmented compilation stays valid. (A sync that does change a file
        // clears the cache: SyncDocumentsDiskTests.ChangedFile_IsReportedUpdated_WithTheDiskText.)
        var consumerPath = GetConsumerFilePath();
        await Service.SyncDocumentsAsync(new List<string> { consumerPath });
        Service._compilationCache[projectId].Should().BeSameAs(cached,
            "a sync that finds no change must not discard the cached compilation");
    }

    [Fact]
    public async Task GetTypeOverview_OnFixtureRecord_ClassifiesAsRecord()
    {
        // get_type_overview emits the type's kind as typeKind.
        var result = await Service.GetTypeOverviewAsync("FixtureRecord");
        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"].Should().NotBeNull();
        data["typeName"]!.Value<string>().Should().Contain("FixtureRecord");
        // GetTypeKindString classifies records as Record (not Class).
        data["typeKind"].Should().NotBeNull();
        data["typeKind"]!.Value<string>().Should().Be("Record", "positional records must be classified as Record, not Class");
    }

    [Fact]
    public async Task SearchSymbols_FindsFixtureRecord_LabeledRecord()
    {
        // Companion to the GetTypeOverview test above: confirms search_symbols ITSELF
        // can find a record by simple name, labeled with the type kind (Record) its kind
        // filter accepts.
        var searchResult = await Service.SearchSymbolsAsync("FixtureRecord", kind: null, maxResults: 10);
        AssertSuccess(searchResult);
        var results = GetData(searchResult)["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
        var match = results!.FirstOrDefault(r => r["fullyQualifiedName"]!.Value<string>() == "SharpLensMcp.Tests.Fixtures.FixtureRecord");
        match.Should().NotBeNull("FixtureRecord must be locatable by simple name");
        match!["kind"].Should().NotBeNull();
        match["kind"]!.Value<string>().Should().Be("Record", "search_symbols labels a type by its kind, a positional record as Record");
    }
}