using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// get_generated_code addresses a generated file by its name or a trailing part of its
// generated path (whole path segments, never a substring), reports a name that matches
// several generated files as ambiguous instead of picking one, and lists the generated
// files when nothing matches; every path it and get_source_generators report goes
// through the solution's path formatting, and get_source_generators lists files in path
// order. The project "Gen" runs the TestAnalyzers generators: GenCaller.g.cs,
// GenImpl.g.cs, and two Dup.g.cs under different generator folders.
public class GeneratedCodeLookupTests
{
    private const string UserCode = """
        namespace Demo
        {
            public class Target { public int Hit() => 1; }
            public interface IGenMarker { }
            public class DuplicateHintMarker { }
        }
        """;

    private sealed class DirectLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    private static AdhocWorkspace Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"adhoc-{Guid.NewGuid():N}");
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var project = ProjectInfo.Create(projectId, VersionStamp.Create(), "Gen", "Gen", LanguageNames.CSharp,
                filePath: Path.Combine(directory, "Gen.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                metadataReferences: new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                        .First(a => a.GetName().Name == "System.Runtime").Location)
                })
            .WithCompilationOutputInfo(default(CompilationOutputInfo)
                .WithAssemblyPath(Path.Combine(directory, "bin", "Gen.dll"))
                .WithGeneratedFilesOutputDirectory(Path.Combine(directory, "obj", "gen")));
        var document = DocumentInfo.Create(DocumentId.CreateNewId(projectId), "Demo.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(UserCode), VersionStamp.Create())),
            filePath: Path.Combine(directory, "Demo.cs"));
        var generators = new AnalyzerFileReference(typeof(CallerSourceGenerator).Assembly.Location, new DirectLoader());
        workspace.TryApplyChanges(workspace.CurrentSolution
            .AddProject(project).AddDocument(document).AddAnalyzerReference(projectId, generators));
        return workspace;
    }

    // The generated tree paths, as the tools report them.
    private static async Task<List<string>> GeneratedPathsAsync(AdhocWorkspace workspace)
    {
        var project = workspace.CurrentSolution.Projects.Single();
        var compilation = (await project.GetCompilationAsync())!;
        return compilation.SyntaxTrees
            .Select(t => t.FilePath)
            .Where(p => !p.EndsWith("Demo.cs", StringComparison.Ordinal))
            .Select(ToolResult.Reported)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public async Task ExactFileName_ReturnsThatFile_WithAFormattedPath()
    {
        var workspace = Create();
        var service = ToolResult.Load(workspace);
        var paths = await GeneratedPathsAsync(workspace);

        var data = ToolResult.Data(await service.GetGeneratedCodeAsync("Gen", "GenImpl.g.cs"));

        data["fileName"]!.Value<string>().Should().Be("GenImpl.g.cs");
        data["hintPath"]!.Value<string>().Should().Be(paths.Single(p => p.EndsWith("/GenImpl.g.cs", StringComparison.Ordinal)));
        data["sourceCode"]!.Value<string>()!.Should().Contain("public class GenImpl : IGenMarker");
    }

    [Fact]
    public async Task TrailingPathSegments_SelectAmongSameNamedFiles()
    {
        var service = ToolResult.Load(Create());

        var data = ToolResult.Data(await service.GetGeneratedCodeAsync(
            "Gen", "SharpLensMcp.Tests.TestAnalyzers.DuplicateHintSecondSourceGenerator/Dup.g.cs"));

        data["sourceCode"]!.Value<string>()!.Should().Contain("FromSecond");
    }

    [Fact]
    public async Task SameNamedFiles_AreAmbiguous_ListingEachPath()
    {
        var workspace = Create();
        var service = ToolResult.Load(workspace);
        var paths = await GeneratedPathsAsync(workspace);

        var error = ToolResult.Error(await service.GetGeneratedCodeAsync("Gen", "Dup.g.cs"), ErrorCodes.AmbiguousMatch);

        error["Context"]!["candidates"]!.Values<string>().Should().Equal(paths.Where(p => p.EndsWith("/Dup.g.cs", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("g.cs")]
    [InlineData("GenImpl")]
    [InlineData("obj")]
    public async Task PartialNames_NeverMatch_AndTheErrorListsTheGeneratedFiles(string generatedFileName)
    {
        var service = ToolResult.Load(Create());

        var error = ToolResult.Error(await service.GetGeneratedCodeAsync("Gen", generatedFileName), ErrorCodes.FileNotFound);

        error["Context"]!["availableFiles"]!.Values<string>().Should().Equal("Dup.g.cs", "GenCaller.g.cs", "GenImpl.g.cs");
    }

    [Fact]
    public async Task UnknownProject_IsInvalidParameter()
    {
        var service = ToolResult.Load(Create());

        ToolResult.Error(await service.GetGeneratedCodeAsync("Nope", "GenImpl.g.cs"), ErrorCodes.InvalidParameter)
            ["Message"]!.Value<string>().Should().Be("Project not found: Nope");
        ToolResult.Error(await service.GetSourceGeneratorsAsync("Nope"), ErrorCodes.InvalidParameter)
            ["Message"]!.Value<string>().Should().Be("Project not found: Nope");
    }

    [Fact]
    public async Task GetSourceGenerators_ListsFormattedPaths_InPathOrder()
    {
        var workspace = Create();
        var service = ToolResult.Load(workspace);
        var paths = await GeneratedPathsAsync(workspace);

        var data = ToolResult.Data(await service.GetSourceGeneratorsAsync("Gen"));

        var project = ((JArray)data["projects"]!).Single();
        ((JArray)project["generatedFiles"]!).Select(f => f["hintPath"]!.Value<string>()).Should().Equal(paths);
    }

    [Fact]
    public async Task Lookup_ViaMcp()
    {
        var workspace = Create();
        var host = new InMemoryToolHost(workspace);
        var paths = await GeneratedPathsAsync(workspace);

        var data = await host.DataAsync("get_generated_code", new { projectName = "Gen", generatedFileName = "GenCaller.g.cs" });
        await host.ErrorAsync("get_generated_code", new { projectName = "Gen", generatedFileName = "Dup.g.cs" }, ErrorCodes.AmbiguousMatch);
        await host.ErrorAsync("get_generated_code", new { projectName = "Gen", generatedFileName = "g.cs" }, ErrorCodes.FileNotFound);

        data["hintPath"]!.Value<string>().Should().Be(paths.Single(p => p.EndsWith("/GenCaller.g.cs", StringComparison.Ordinal)));
    }
}
