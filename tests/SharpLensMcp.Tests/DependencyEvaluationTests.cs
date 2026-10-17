using System.Text.Json;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// find_unused_dependencies and get_nuget_dependencies read a project's package references
// the way MSBuild evaluates them — an XML-namespaced project file, only the NEAREST
// Directory.Build.props, Conditions, $(Property) versions, VersionOverride and Update= —
// and map compile references to packages through the restore assets (so a custom
// RestorePackagesPath works). PrivateAssets=all does not hide a package: only a package
// whose compile assets are excluded is skipped. A multi-targeted project is one entry.
//
// On-disk layout (no restore, no network): the project file and props are real files the
// in-process MSBuild evaluates; the "restored" packages are assemblies emitted into a
// custom package folder that obj/project.assets.json names; the in-memory project
// references them as a restored build would.
public class DependencyEvaluationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"depeval-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <{0}>{1}</{0}>
            <UseConditional>false</UseConditional>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Pkg.Used" Version="$(PkgVersion)" />
            <PackageReference Include="Pkg.Unused" Version="1.0.0" PrivateAssets="all" />
            <PackageReference Include="Pkg.Conditional" Version="1.0.0" Condition="'$(UseConditional)' == 'true'" />
            <PackageReference Include="Pkg.Overridden" VersionOverride="3.0.0" />
            <PackageReference Update="Pkg.Used" PrivateAssets="all" />
            <PackageReference Include="Pkg.NoCompile" Version="1.0.0" ExcludeAssets="compile" />
          </ItemGroup>
        </Project>
        """;

    private const string AppSource = """
        namespace App
        {
            public class Consumer
            {
                public int Run() => PkgUsed.Api.Value + PropsUsed.Api.Value;
            }
        }
        """;

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string EmitPackage(string id, string version, string ns)
    {
        var path = Path.Combine(_root, "pkgs", id.ToLowerInvariant(), version, "lib", "net8.0", id + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var compilation = CSharpCompilation.Create(id,
            new[] { CSharpSyntaxTree.ParseText($"namespace {ns} {{ public static class Api {{ public static int Value => 1; }} }}") },
            QualitySolutionBuilder.CoreReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.Emit(path).Success.Should().BeTrue();
        return path;
    }

    // Writes the layout and returns a workspace whose project(s) for App.csproj reference
    // the emitted packages; multiTargeted loads it as App(net8.0) and App(net10.0).
    private AdhocWorkspace Setup(bool multiTargeted)
    {
        Write("Directory.Build.props", """
            <Project>
              <ItemGroup>
                <PackageReference Include="Outer.NotImported" Version="9.9.9" />
              </ItemGroup>
            </Project>
            """);
        Write(Path.Combine("sub", "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <PkgVersion>2.0.0</PkgVersion>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Props.Used" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        var csproj = Write(Path.Combine("sub", "App", "App.csproj"), string.Format(ProjectXml,
            multiTargeted ? "TargetFrameworks" : "TargetFramework", multiTargeted ? "net8.0;net10.0" : "net8.0"));

        var packages = new[]
        {
            EmitPackage("Pkg.Used", "2.0.0", "PkgUsed"),
            EmitPackage("Pkg.Unused", "1.0.0", "PkgUnused"),
            EmitPackage("Pkg.Overridden", "3.0.0", "PkgOverridden"),
            EmitPackage("Props.Used", "1.0.0", "PropsUsed"),
        };
        var packageFolder = Path.Combine(_root, "pkgs").Replace('\\', '/') + "/";
        var assets = new Dictionary<string, object>
        {
            ["version"] = 3,
            ["targets"] = new Dictionary<string, object> { ["net8.0"] = new Dictionary<string, object>() },
            ["libraries"] = new Dictionary<string, object>
            {
                ["Pkg.Used/2.0.0"] = new { type = "package", path = "pkg.used/2.0.0" },
                ["Pkg.Unused/1.0.0"] = new { type = "package", path = "pkg.unused/1.0.0" },
                ["Pkg.Overridden/3.0.0"] = new { type = "package", path = "pkg.overridden/3.0.0" },
                ["Props.Used/1.0.0"] = new { type = "package", path = "props.used/1.0.0" },
                ["Pkg.NoCompile/1.0.0"] = new { type = "package", path = "pkg.nocompile/1.0.0" },
            },
            ["packageFolders"] = new Dictionary<string, object> { [packageFolder] = new { } },
            ["project"] = new { version = "1.0.0", restore = new { projectPath = csproj } },
        };
        Write(Path.Combine("sub", "App", "obj", "project.assets.json"), JsonSerializer.Serialize(assets));

        var workspace = new AdhocWorkspace();
        var names = multiTargeted ? new[] { "App(net8.0)", "App(net10.0)" } : new[] { "App" };
        var projects = names.Select(name => ProjectInfo.Create(
            ProjectId.CreateNewId(name), VersionStamp.Create(), name, "App", LanguageNames.CSharp,
            filePath: csproj,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: QualitySolutionBuilder.CoreReferences.Concat(packages.Select(p => MetadataReference.CreateFromFile(p))))).ToList();
        var solution = workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(),
            Path.Combine(_root, "sub", "D.sln"), projects));
        foreach (var project in projects)
        {
            solution = solution.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "Consumer.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(AppSource), VersionStamp.Create())),
                filePath: Path.Combine(_root, "sub", "App", "Consumer.cs")));
        }
        workspace.TryApplyChanges(solution).Should().BeTrue();
        return workspace;
    }

    private static void AssertAudit(JToken project, string targetFrameworks)
    {
        project["projectName"]!.Value<string>().Should().Be("App");
        string.Join(",", (project["targetFrameworks"] as JArray)?.Values<string>() ?? Array.Empty<string>()).Should().Be(targetFrameworks);
        project["packagesChecked"]!.Value<int>().Should().Be(5,
            "Props.Used (nearest props), Pkg.Used, Pkg.Unused, Pkg.Overridden, Pkg.NoCompile; the false Condition and the outer props are not evaluated in");
        ((JArray)project["unusedPackages"]!).Select(p => $"{p["name"]} {p["version"]}")
            .Should().Equal("Pkg.Unused 1.0.0", "Pkg.Overridden 3.0.0");
        ((JArray)project["skipped"]!).Select(s => $"{s["name"]}: {s["reason"]}")
            .Should().Equal("Pkg.NoCompile: ExcludeAssets excludes its compile assets");
        project["parseError"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task UnusedDependencies_FollowMSBuildEvaluation_AndTheRestoreAssets()
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(Setup(multiTargeted: false));

        var data = JObject.FromObject(await service.FindUnusedDependenciesAsync("App"))["data"]!;

        AssertAudit(((JArray)data["projects"]!).Single(), "");
    }

    [Fact]
    public async Task UnusedDependencies_MultiTargetedProject_IsOneEntry()
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(Setup(multiTargeted: true));

        var data = JObject.FromObject(await service.FindUnusedDependenciesAsync())["data"]!;

        AssertAudit(((JArray)data["projects"]!).Single(), "net10.0,net8.0");
    }

    [Fact]
    public async Task NuGetDependencies_ListTheEvaluatedReferences()
    {
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(Setup(multiTargeted: false));

        var data = JObject.FromObject(await service.GetNuGetDependenciesAsync("App"))["data"]!;

        ((JArray)((JArray)data["projects"]!).Single()["packages"]!)
            .Select(p => $"{p["packageName"]} {p["version"]} {p["privateAssets"]} {p["excludeAssets"]}")
            .Should().Equal(
                "Props.Used 1.0.0  ",
                "Pkg.Used 2.0.0 all ",
                "Pkg.Unused 1.0.0 all ",
                "Pkg.Overridden 3.0.0  ",
                "Pkg.NoCompile 1.0.0  compile");
    }

    [Fact]
    public async Task ViaDispatcher_UnusedDependencies_AndUnknownProject()
    {
        var dispatcher = new InMemoryDispatcher(Setup(multiTargeted: true));

        var data = await dispatcher.DataAsync("find_unused_dependencies", new { projectName = "App" });
        AssertAudit(((JArray)data["projects"]!).Single(), "net10.0,net8.0");

        var error = await dispatcher.ErrorAsync("find_unused_dependencies", new { projectName = "Nope" }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("Project not found: Nope");
    }
}
