using System.Diagnostics;
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

// diff_api_surface rebuilds the baseline from the files the project COMPILED at the
// baseline ref — its Compile items evaluated from the project file at that ref, so a
// `Compile Remove` folder stays out (no duplicate-type crash, no phantom members) and a
// linked `Compile Include` outside the project folder stays in; a project at the git root
// sees its deleted files — and runs the project's source generators on the baseline too,
// so generated API is neither a phantom addition nor invisible when removed.
//
// Each test builds a throwaway git repository (git init/add/commit, no remote) and loads
// the working tree as an in-memory solution whose project file path is the real one.
public class ApiSurfaceBaselineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"apidiff-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void Git(params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.invalid" }.Concat(arguments))
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)}: {output}");
    }

    private void Commit()
    {
        Git("init", "-q");
        Git("add", "-A");
        Git("commit", "-q", "-m", "baseline");
    }

    // An in-memory solution at <root>/A.sln with one project at projectFile whose documents
    // are the given working-tree files (absolute paths), optionally with the test generator.
    private static AdhocWorkspace Load(string solutionPath, string projectFile, IEnumerable<string> documents, bool withGenerator)
    {
        var workspace = new AdhocWorkspace();
        var id = ProjectId.CreateNewId("Api");
        var analyzers = withGenerator
            ? new AnalyzerReference[] { new AnalyzerFileReference(typeof(CallerSourceGenerator).Assembly.Location, new LoadFromPathAnalyzerLoader()) }
            : Array.Empty<AnalyzerReference>();
        var project = ProjectInfo.Create(id, VersionStamp.Create(), "Api", "Api", LanguageNames.CSharp,
            filePath: projectFile,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: QualitySolutionBuilder.CoreReferences,
            analyzerReferences: analyzers);
        var solution = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), solutionPath, new[] { project }));
        foreach (var path in documents)
        {
            solution = solution.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(id), Path.GetFileName(path),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(File.ReadAllText(path)), VersionStamp.Create())),
                filePath: path));
        }
        workspace.TryApplyChanges(solution).Should().BeTrue();
        return workspace;
    }

    private const string ApiProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <Compile Remove="Legacy/**" />
            <Compile Include="../Shared/Linked.cs" />
          </ItemGroup>
        </Project>
        """;

    private AdhocWorkspace SetupCompileItemsRepository()
    {
        var project = Write(Path.Combine("Api", "Api.csproj"), ApiProject);
        var foo = Write(Path.Combine("Api", "Foo.cs"), "namespace Api { public class Foo { public void Keep() { } public void Dropped() { } } }");
        // A stale copy the project removes from compilation; compiled in, its Keep() would
        // duplicate Foo.Keep's documentation-comment id.
        Write(Path.Combine("Api", "Legacy", "Foo.cs"), "namespace Api { public class Foo { public void Keep() { } public void LegacyOnly() { } } }");
        var linked = Write(Path.Combine("Shared", "Linked.cs"), "namespace Api { public class Linked { public void Stay() { } } }");
        var target = Write(Path.Combine("Api", "Target.cs"), "namespace Demo { public class Target { public int Hit() => 1; } }");
        Commit();
        Write(Path.Combine("Api", "Foo.cs"), "namespace Api { public class Foo { public void Keep() { } public void Added() { } } }");
        return Load(Path.Combine(_root, "A.sln"), project, new[] { foo, linked, target }, withGenerator: true);
    }

    private static List<string> Breaking(JToken data) =>
        ((JArray)data["breaking"]!).Select(b => $"{b["changeKind"]}: {b["signature"]}").ToList();

    private static List<string> Added(JToken data) =>
        ((JArray)data["added"]!).Select(a => a["signature"]!.Value<string>()!).ToList();

    [Fact]
    public async Task Baseline_IsTheCompileItemsAtTheRef_PlusTheProjectsGenerators()
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(SetupCompileItemsRepository());

        var json = JObject.FromObject(await service.DiffApiSurfaceAsync("HEAD"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;

        Breaking(data).Should().Equal("removed: public void Api.Foo.Dropped()");
        Added(data).Should().Equal("public void Api.Foo.Added()");
        ((JArray)data["projectsCompared"]!).Single()["baselineFileCount"]!.Value<int>().Should().Be(3,
            "Api/Foo.cs, Api/Target.cs and the linked Shared/Linked.cs - not the removed Legacy/Foo.cs");
    }

    [Fact]
    public async Task ProjectAtTheGitRoot_SeesItsDeletedFiles()
    {
        var project = Write("Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        var a = Write("A.cs", "namespace R { public class A { } }");
        Write("B.cs", "namespace R { public class B { } }");
        Commit();
        File.Delete(Path.Combine(_root, "B.cs"));
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(Load(Path.Combine(_root, "B.sln"), project, new[] { a }, withGenerator: false));

        var data = JObject.FromObject(await service.DiffApiSurfaceAsync("HEAD"))["data"]!;

        Breaking(data).Should().Equal("removed: public class R.B");
        Added(data).Should().BeEmpty();
    }

    [Fact]
    public async Task ViaDispatcher_CompileItemBaseline()
    {
        var dispatcher = new InMemoryDispatcher(SetupCompileItemsRepository());

        var data = await dispatcher.DataAsync("diff_api_surface", new { baselineRef = "HEAD", projectName = "Api" });

        Breaking(data).Should().Equal("removed: public void Api.Foo.Dropped()");
        Added(data).Should().Equal("public void Api.Foo.Added()");
    }
}
