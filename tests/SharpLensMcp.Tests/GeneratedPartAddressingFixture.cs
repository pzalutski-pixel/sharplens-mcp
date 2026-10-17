using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SharpLensMcp.Tests.TestAnalyzers;

namespace SharpLensMcp.Tests;

// An in-memory solution whose partial class Demo.GenPartialHost is declared in a
// user file rx.cs and completed by PartialImplementationSourceGenerator, which emits
// the second class declaration and the body of the partial method Compute. Generated
// files land under <dir>\obj\gen — the shape an SDK project has — so the generated
// path sorts BEFORE ...\rx.cs, and only rx.cs is a solution document.
internal static class GeneratedPartAddressingFixture
{
    public const string UserCode = @"namespace Demo
{
    public partial class GenPartialHost
    {
        public partial int Compute(int x);

        public int Twice(int x) => Compute(x) * 2;
    }
}
";

    private sealed class DirectLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    public static (AdhocWorkspace workspace, string userFilePath) Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"adhoc-{Guid.NewGuid():N}");
        var userFilePath = Path.Combine(directory, "rx.cs");
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location)
        };
        var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Create(), "GenHost", "GenHost", LanguageNames.CSharp,
                filePath: Path.Combine(directory, "GenHost.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                metadataReferences: references)
            .WithCompilationOutputInfo(default(CompilationOutputInfo)
                .WithAssemblyPath(Path.Combine(directory, "bin", "GenHost.dll"))
                .WithGeneratedFilesOutputDirectory(Path.Combine(directory, "obj", "gen")));
        var documentInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(projectId), "rx.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(UserCode), VersionStamp.Create())),
            filePath: userFilePath);
        var generator = new AnalyzerFileReference(
            typeof(PartialImplementationSourceGenerator).Assembly.Location, new DirectLoader());
        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(documentInfo)
            .AddAnalyzerReference(projectId, generator);
        if (!workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("the fixture workspace rejected its solution");
        return (workspace, userFilePath);
    }
}
