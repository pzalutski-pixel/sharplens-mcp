using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp.Tests;

/// <summary>
/// Helper class for creating in-memory workspaces for testing RoslynService.
/// </summary>
public static class TestHelpers
{
    private static readonly MetadataReference[] CoreReferences = new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(IAsyncEnumerable<>).Assembly.Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location)
    };

    /// <summary>
    /// Creates an AdhocWorkspace with a single document containing the provided code.
    /// </summary>
    public static (AdhocWorkspace workspace, Document document) CreateWorkspaceWithCode(
        string code,
        string fileName = "Test.cs",
        string projectName = "TestProject")
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            projectName,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: CoreReferences
        );

        // Give the document a real-looking FilePath so RoslynService.TryFindDocument
        // can resolve it (the production path matches by FilePath, not Name).
        var virtualPath = Path.Combine(Path.GetTempPath(), $"adhoc-{Guid.NewGuid():N}", fileName);
        var documentInfo = DocumentInfo.Create(
            documentId,
            name: fileName,
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(code), VersionStamp.Create())),
            filePath: virtualPath);

        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(documentInfo);

        workspace.TryApplyChanges(solution);
        var document = workspace.CurrentSolution.GetDocument(documentId)!;

        return (workspace, document);
    }

    /// <summary>
    /// Creates a workspace with multiple documents.
    /// </summary>
    public static (AdhocWorkspace workspace, Document[] documents) CreateWorkspaceWithMultipleDocuments(
        params (string code, string fileName)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: CoreReferences
        );

        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        // Give each doc a real-looking FilePath under a per-test temp dir so
        // RoslynService.TryFindDocument can resolve them — the production path
        // matches by FilePath, not by Name.
        var tempDir = Path.Combine(Path.GetTempPath(), $"adhoc-{Guid.NewGuid():N}");
        var documentIds = new List<DocumentId>();
        foreach (var (code, fileName) in files)
        {
            var documentId = DocumentId.CreateNewId(projectId);
            documentIds.Add(documentId);
            var virtualPath = Path.Combine(tempDir, fileName);
            var documentInfo = DocumentInfo.Create(
                documentId,
                name: fileName,
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(code), VersionStamp.Create())),
                filePath: virtualPath);
            solution = solution.AddDocument(documentInfo);
        }

        workspace.TryApplyChanges(solution);

        var documents = documentIds
            .Select(id => workspace.CurrentSolution.GetDocument(id)!)
            .ToArray();

        return (workspace, documents);
    }

    /// <summary>
    /// Finds the position of a specific text in the code.
    /// </summary>
    public static int FindTextPosition(string code, string text)
    {
        var index = code.IndexOf(text, StringComparison.Ordinal);
        if (index == -1)
            throw new ArgumentException($"Text '{text}' not found in code");
        return index;
    }
}
