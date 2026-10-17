using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp.Tests;

// An in-memory solution in the shape MSBuildWorkspace gives a MULTI-TARGETED project:
// one project file (Multi/Multi.csproj, <TargetFrameworks>net8.0;net10.0</TargetFrameworks>)
// loaded as two Roslyn projects, "Multi(net8.0)" and "Multi(net10.0)", that share the
// project FilePath and the assembly name "Multi" and each hold their own document for
// every source file at the SAME file path (linked documents). Each project compiles with
// its framework's preprocessor symbols, so #if NET10_0_OR_GREATER code exists in the
// net10.0 compilation only. A single-target "Consumer" project (net10.0 symbols)
// references Multi(net10.0), as MSBuild picks the nearest framework.
//
// Every Multi declaration is therefore bound once per framework, the duplicate a
// per-project scan reports twice; tests use the fixture to pin de-duplication and
// project-name addressing deterministically. Documents get absolute paths under a
// per-instance temp directory (tools map symbols back to documents by path); Dispose
// removes the directory.
internal sealed class MultiTargetSolutionFixture : IDisposable
{
    public const string MultiName = "Multi";
    public const string Net8 = "net8.0";
    public const string Net10 = "net10.0";
    public const string Net8ProjectName = "Multi(net8.0)";
    public const string Net10ProjectName = "Multi(net10.0)";
    public const string ConsumerProjectName = "Consumer";

    public static readonly IReadOnlyList<string> Net8Symbols =
        new[] { "NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER" };

    public static readonly IReadOnlyList<string> Net10Symbols =
        new[] { "NET", "NETCOREAPP", "NET10_0", "NET8_0_OR_GREATER", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER" };

    private static readonly MetadataReference[] CoreRefs =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location)
    };

    public AdhocWorkspace Workspace { get; }
    public string Directory { get; }
    public ProjectId Net8ProjectId { get; }
    public ProjectId Net10ProjectId { get; }
    public ProjectId ConsumerProjectId { get; }

    // The project file both Multi projects share, and Consumer's.
    public string MultiProjectFilePath => Path.Combine(Directory, MultiName, $"{MultiName}.csproj");
    public string ConsumerProjectFilePath => Path.Combine(Directory, ConsumerProjectName, $"{ConsumerProjectName}.csproj");

    // Absolute path of a Multi source file (shared by both framework projects) and of a
    // Consumer source file.
    public string MultiFilePath(string fileName) => Path.Combine(Directory, MultiName, fileName);
    public string ConsumerFilePath(string fileName) => Path.Combine(Directory, ConsumerProjectName, fileName);

    // The default sources: MultiTargetSources.Multi and MultiTargetSources.Consumer.
    public MultiTargetSolutionFixture()
        : this(MultiTargetSources.Multi, MultiTargetSources.Consumer)
    {
    }

    // One Multi file (MultiTargetSources.MultiFileName) and one Consumer file
    // (MultiTargetSources.ConsumerFileName) with the given sources.
    public MultiTargetSolutionFixture(string multiSource, string consumerSource)
        : this(new[] { (MultiTargetSources.MultiFileName, multiSource) },
               new[] { (MultiTargetSources.ConsumerFileName, consumerSource) })
    {
    }

    public MultiTargetSolutionFixture(
        IReadOnlyList<(string FileName, string Source)> multiDocuments,
        IReadOnlyList<(string FileName, string Source)> consumerDocuments)
    {
        Directory = Path.Combine(Path.GetTempPath(), $"multitarget-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);

        Workspace = new AdhocWorkspace();
        Net8ProjectId = ProjectId.CreateNewId();
        Net10ProjectId = ProjectId.CreateNewId();
        ConsumerProjectId = ProjectId.CreateNewId();

        var solution = Workspace.CurrentSolution
            .AddProject(Project(Net8ProjectId, Net8ProjectName, MultiName, MultiProjectFilePath, Net8Symbols))
            .AddProject(Project(Net10ProjectId, Net10ProjectName, MultiName, MultiProjectFilePath, Net10Symbols))
            .AddProject(Project(ConsumerProjectId, ConsumerProjectName, ConsumerProjectName, ConsumerProjectFilePath,
                Net10Symbols, new ProjectReference(Net10ProjectId)));

        foreach (var (fileName, source) in multiDocuments)
        {
            solution = solution
                .AddDocument(Document(Net8ProjectId, MultiFilePath(fileName), source))
                .AddDocument(Document(Net10ProjectId, MultiFilePath(fileName), source));
        }
        foreach (var (fileName, source) in consumerDocuments)
        {
            solution = solution.AddDocument(Document(ConsumerProjectId, ConsumerFilePath(fileName), source));
        }

        Workspace.TryApplyChanges(solution);
    }

    private static ProjectInfo Project(
        ProjectId id, string name, string assemblyName, string filePath, IReadOnlyList<string> preprocessorSymbols,
        params ProjectReference[] projectReferences) =>
        ProjectInfo.Create(
            id, VersionStamp.Create(), name, assemblyName, LanguageNames.CSharp,
            filePath: filePath,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: preprocessorSymbols),
            metadataReferences: CoreRefs,
            projectReferences: projectReferences);

    private static DocumentInfo Document(ProjectId projectId, string filePath, string source) =>
        DocumentInfo.Create(
            DocumentId.CreateNewId(projectId), Path.GetFileName(filePath),
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
            filePath: filePath);

    public void Dispose()
    {
        Workspace.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
