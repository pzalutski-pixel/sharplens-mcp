using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_dependencies reports a ProjectReference the compiler never needs.
// The App project references Lib but uses no Lib type, so Lib must surface as an
// unused project reference (the positive case — only the empty case was covered).
public class UnusedProjectReferenceTests : IDisposable
{
    private readonly string _tempDir;

    public UnusedProjectReferenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"unusedproj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task FindUnusedDependencies_FlagsAnUnusedProjectReference()
    {
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
        var workspace = new AdhocWorkspace();
        var libId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();

        // find_unused_dependencies gates on the project's csproj existing on disk
        // (it also parses it for PackageReferences); a minimal valid one suffices.
        var appCsproj = Path.Combine(_tempDir, "App.csproj");
        File.WriteAllText(appCsproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup /></Project>");

        var lib = ProjectInfo.Create(libId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs);
        var app = ProjectInfo.Create(appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs,
            projectReferences: new[] { new ProjectReference(libId) })
            .WithFilePath(appCsproj);

        var solution = workspace.CurrentSolution
            .AddProject(lib)
            .AddDocument(DocumentId.CreateNewId(libId), "Lib.cs",
                SourceText.From("namespace Lib { public class Unused { } }"))
            .AddProject(app)
            .AddDocument(DocumentId.CreateNewId(appId), "App.cs",
                SourceText.From("namespace App { public class Service { public int N() => 1; } }"));
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.FindUnusedDependenciesAsync("App"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var appResult = (json["data"]!["projects"] as JArray)!
            .First(p => p["projectName"]!.Value<string>() == "App");
        appResult["projectReferencesChecked"]!.Value<int>().Should().Be(1);
        var unusedRefs = (appResult["unusedProjectReferences"] as JArray)!
            .Select(r => r["name"]!.Value<string>())
            .ToList();
        unusedRefs.Should().Contain("Lib",
            "App references Lib but uses no Lib type, so the reference is unused");
    }

    [Fact]
    public async Task FindUnusedDependencies_DoesNotMatchProjectRefByPackageNameCollision()
    {
        // Lib's OUTPUT assembly name collides with System.Private.CoreLib — a PE reference
        // the compilation genuinely uses (every int/object does). Matching the project
        // reference by assembly NAME would see that used PE name and call Lib "used"; the
        // reference must instead be judged by assembly-symbol identity.
        var coreLib = typeof(object).Assembly.Location; // System.Private.CoreLib.dll
        var refs = new[]
        {
            MetadataReference.CreateFromFile(coreLib),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
        var collidingName = Path.GetFileNameWithoutExtension(coreLib); // System.Private.CoreLib

        var workspace = new AdhocWorkspace();
        var libId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();

        var appCsproj = Path.Combine(_tempDir, "App.csproj");
        File.WriteAllText(appCsproj, "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup /></Project>");

        // Project name "Lib" but ASSEMBLY name collides with the used PE reference.
        var lib = ProjectInfo.Create(libId, VersionStamp.Create(), "Lib", collidingName, LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs);
        var app = ProjectInfo.Create(appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs,
            projectReferences: new[] { new ProjectReference(libId) })
            .WithFilePath(appCsproj);

        var solution = workspace.CurrentSolution
            .AddProject(lib)
            .AddDocument(DocumentId.CreateNewId(libId), "Lib.cs",
                SourceText.From("namespace Lib { public class Unused { } }"))
            .AddProject(app)
            .AddDocument(DocumentId.CreateNewId(appId), "App.cs",
                SourceText.From("namespace App { public class Service { public int N() => 1; } }"));
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.FindUnusedDependenciesAsync("App"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var appResult = (json["data"]!["projects"] as JArray)!
            .First(p => p["projectName"]!.Value<string>() == "App");
        (appResult["unusedProjectReferences"] as JArray)!
            .Select(r => r["name"]!.Value<string>())
            .Should().Contain("Lib",
                "the project reference is unused despite its assembly name colliding with a used package");
    }
}
