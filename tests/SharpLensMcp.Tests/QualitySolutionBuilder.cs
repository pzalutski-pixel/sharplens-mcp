using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp.Tests;

// Builds an in-memory solution WITH a solution file path, so tools that resolve
// solution-relative paths (get_diagnostics projectPath, FormatPath output) see the shape a
// loaded .sln gives them. Every project gets a project file path "<dir>/<name>/<name>.csproj";
// AddMultiTargeted adds one logical project as two per-framework projects
// ("<name>(net8.0)", "<name>(net10.0)") sharing the project file and every document path,
// each compiled with its framework's preprocessor symbols (the MultiTargetSolutionFixture
// shape). Documents get absolute paths "<dir>/<project>/<file>". Nothing is written to disk
// unless a test writes it; Dispose removes the directory.
internal sealed class QualitySolutionBuilder : IDisposable
{
    public static readonly MetadataReference[] CoreReferences =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.IO.File).Assembly.Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Threading").Location),
    };

    private readonly AdhocWorkspace _workspace = new();
    private readonly List<ProjectInfo> _projects = new();
    private readonly List<DocumentInfo> _documents = new();

    public QualitySolutionBuilder()
    {
        Directory = Path.Combine(Path.GetTempPath(), $"quality-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }
    public string SolutionPath => Path.Combine(Directory, "Quality.sln");

    public string ProjectFilePath(string name) => Path.Combine(Directory, name, $"{name}.csproj");
    public string FilePath(string project, string fileName) => Path.Combine(Directory, project, fileName);

    // A single-target project; returns its id. Options default to a nullable-enabled
    // library at the latest language version over CoreReferences.
    public ProjectId AddProject(
        string name, IReadOnlyList<(string FileName, string Source)> documents,
        IEnumerable<AnalyzerReference>? analyzers = null, IEnumerable<ProjectId>? references = null,
        IReadOnlyList<string>? preprocessorSymbols = null, CSharpCompilationOptions? compilationOptions = null,
        LanguageVersion languageVersion = LanguageVersion.Latest, IEnumerable<MetadataReference>? extraReferences = null)
    {
        var id = ProjectId.CreateNewId(name);
        var project = Project(id, name, name, preprocessorSymbols ?? MultiTargetSolutionFixture.Net8Symbols,
            analyzers, references);
        if (compilationOptions != null) project = project.WithCompilationOptions(compilationOptions);
        project = project
            .WithParseOptions(((CSharpParseOptions)project.ParseOptions!).WithLanguageVersion(languageVersion))
            .WithMetadataReferences(CoreReferences.Concat(extraReferences ?? Enumerable.Empty<MetadataReference>()));
        _projects.Add(project);
        foreach (var (fileName, source) in documents) _documents.Add(Document(id, FilePath(name, fileName), source));
        return id;
    }

    // One logical project loaded per framework: returns the (net8.0, net10.0) project ids.
    public (ProjectId Net8, ProjectId Net10) AddMultiTargeted(
        string name, IReadOnlyList<(string FileName, string Source)> documents,
        IEnumerable<AnalyzerReference>? analyzers = null)
    {
        var net8 = ProjectId.CreateNewId($"{name}(net8.0)");
        var net10 = ProjectId.CreateNewId($"{name}(net10.0)");
        _projects.Add(Project(net8, $"{name}(net8.0)", name, MultiTargetSolutionFixture.Net8Symbols, analyzers, null));
        _projects.Add(Project(net10, $"{name}(net10.0)", name, MultiTargetSolutionFixture.Net10Symbols, analyzers, null));
        foreach (var (fileName, source) in documents)
        {
            _documents.Add(Document(net8, FilePath(name, fileName), source));
            _documents.Add(Document(net10, FilePath(name, fileName), source));
        }
        return (net8, net10);
    }

    public AdhocWorkspace Build()
    {
        var solution = _workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), SolutionPath, _projects));
        foreach (var document in _documents) solution = solution.AddDocument(document);
        if (!_workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("The in-memory solution could not be applied to the workspace");
        return _workspace;
    }

    private ProjectInfo Project(
        ProjectId id, string name, string logicalName, IReadOnlyList<string> preprocessorSymbols,
        IEnumerable<AnalyzerReference>? analyzers, IEnumerable<ProjectId>? references) =>
        ProjectInfo.Create(
            id, VersionStamp.Create(), name, logicalName, LanguageNames.CSharp,
            filePath: ProjectFilePath(logicalName),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: preprocessorSymbols),
            metadataReferences: CoreReferences,
            projectReferences: references?.Select(r => new ProjectReference(r)),
            analyzerReferences: analyzers);

    private static DocumentInfo Document(ProjectId projectId, string filePath, string source) =>
        DocumentInfo.Create(
            DocumentId.CreateNewId(projectId), Path.GetFileName(filePath),
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
            filePath: filePath);

    public void Dispose()
    {
        _workspace.Dispose();
        // An analyzer assembly a test loaded from the directory stays locked for the process;
        // the directory is then left under the temp root for the OS to reclaim.
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
