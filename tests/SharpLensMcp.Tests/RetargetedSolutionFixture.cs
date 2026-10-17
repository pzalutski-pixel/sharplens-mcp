using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp.Tests;

// A two-project in-memory solution in which the consuming project sees the library
// through a RETARGETING assembly symbol — the shape a real solution takes whenever a
// test project's references differ from the production project's (a package version
// skew, or a newer target framework). "Lib" compiles against Dep 1.0.0.0 and
// "Lib.Tests" against Dep 2.0.0.0, so Lib.Tests' compilation cannot reuse Lib's own
// assembly symbol: every Lib symbol bound inside a Lib.Tests body is a distinct
// instance from the same declaration in Lib's compilation. With retarget: false both
// projects reference the same Dep, which is the control shape (shared symbols).
//
// libAssemblyName sets Lib's output assembly name, which defaults to the project
// name; a distinct value models a project whose <AssemblyName> differs from its
// project name, so a tool that confuses the two is observable.
//
// Documents get absolute paths under a per-instance temp directory, because tools map
// a symbol back to its project by document path. Dispose removes the directory.
internal sealed class RetargetedSolutionFixture : IDisposable
{
    public const string LibProjectName = "Lib";
    public const string TestProjectName = "Lib.Tests";

    private static readonly MetadataReference[] CoreRefs =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location)
    };

    public AdhocWorkspace Workspace { get; }
    public string Directory { get; }
    public ProjectId LibProjectId { get; }
    public ProjectId TestProjectId { get; }

    // Absolute paths of the two documents, for position-addressed tools.
    public string LibFilePath => Path.Combine(Directory, LibProjectName, "Lib.cs");
    public string TestFilePath => Path.Combine(Directory, TestProjectName, "Tests.cs");

    public RetargetedSolutionFixture(
        string libSource, string testSource, bool retarget = true, string libAssemblyName = LibProjectName)
    {
        Directory = Path.Combine(Path.GetTempPath(), $"retargeted-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);

        var depV1 = EmitDep("1.0.0.0");
        var depForTests = retarget ? EmitDep("2.0.0.0") : depV1;

        Workspace = new AdhocWorkspace();
        LibProjectId = ProjectId.CreateNewId();
        TestProjectId = ProjectId.CreateNewId();

        var solution = Workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                LibProjectId, VersionStamp.Create(), LibProjectName, libAssemblyName, LanguageNames.CSharp,
                filePath: Path.Combine(Directory, LibProjectName, $"{LibProjectName}.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: CoreRefs.Append(depV1)))
            .AddDocument(Document(LibProjectId, LibFilePath, libSource))
            .AddProject(ProjectInfo.Create(
                TestProjectId, VersionStamp.Create(), TestProjectName, TestProjectName, LanguageNames.CSharp,
                filePath: Path.Combine(Directory, TestProjectName, $"{TestProjectName}.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: CoreRefs.Append(depForTests),
                projectReferences: new[] { new ProjectReference(LibProjectId) }))
            .AddDocument(Document(TestProjectId, TestFilePath, testSource));

        Workspace.TryApplyChanges(solution);
    }

    private static DocumentInfo Document(ProjectId projectId, string filePath, string source) =>
        DocumentInfo.Create(
            DocumentId.CreateNewId(projectId), Path.GetFileName(filePath),
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
            filePath: filePath);

    // A one-type library whose only difference between calls is its assembly version.
    // Lib never has to use it: a reference-set difference alone forces retargeting.
    private static MetadataReference EmitDep(string version)
    {
        var compilation = CSharpCompilation.Create(
            "Dep",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] " +
                    "namespace Dep { public static class Marker { public static int Value => 1; } }")
            },
            CoreRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "Dep failed to compile: " + string.Join("; ", emit.Diagnostics));
        }
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    public void Dispose()
    {
        Workspace.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
