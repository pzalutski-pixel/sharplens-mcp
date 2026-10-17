using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;

namespace SharpLensMcp.Tests;

// Shared plumbing for the edit-tool tests that pin exact resulting source text: an
// in-memory solution loaded into a RoslynService (unit level) or behind an McpServer
// (dispatcher level), the current text of a document, and the compile errors of every
// project after an edit.
internal static class RefactoringTestKit
{
    // One document, loaded into a fresh RoslynService.
    public static (RoslynService Service, AdhocWorkspace Workspace, Document Document) Load(
        string code, string fileName = "Test.cs")
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, fileName);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, workspace, document);
    }

    // Several documents of one project, loaded into a fresh RoslynService.
    public static (RoslynService Service, AdhocWorkspace Workspace, Document[] Documents) LoadMany(
        params (string Code, string FileName)[] files)
    {
        var (workspace, documents) = TestHelpers.CreateWorkspaceWithMultipleDocuments(files);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, workspace, documents);
    }

    // Projects of one in-memory solution, each project (name, names of the projects it
    // references, files) under <root>/<name>/<name>.csproj with its files beside it, every
    // project compiled with the given output kind and preprocessor symbols. Projects are
    // added in the order given.
    public static (RoslynService Service, AdhocWorkspace Workspace, string Root) LoadProjects(
        OutputKind outputKind, string[] preprocessorSymbols,
        params (string Name, string[] References, (string Code, string FileName)[] Files)[] projects)
    {
        var root = Path.Combine(Path.GetTempPath(), $"adhoc-{Guid.NewGuid():N}");
        var workspace = new AdhocWorkspace();
        var ids = projects.ToDictionary(p => p.Name, _ => ProjectId.CreateNewId());
        var solution = workspace.CurrentSolution;
        foreach (var (name, references, files) in projects)
        {
            solution = solution.AddProject(ProjectInfo.Create(
                ids[name], VersionStamp.Create(), name, name, LanguageNames.CSharp,
                filePath: Path.Combine(root, name, $"{name}.csproj"),
                compilationOptions: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(outputKind)
                    .WithNullableContextOptions(NullableContextOptions.Enable),
                parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                    Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest, preprocessorSymbols: preprocessorSymbols),
                metadataReferences: TestHelpers.CoreReferenceSet,
                projectReferences: references.Select(r => new ProjectReference(ids[r]))));
            foreach (var (code, fileName) in files)
            {
                solution = solution.AddDocument(DocumentInfo.Create(
                    DocumentId.CreateNewId(ids[name]), fileName,
                    loader: TextLoader.From(TextAndVersion.Create(
                        Microsoft.CodeAnalysis.Text.SourceText.From(code), VersionStamp.Create())),
                    filePath: Path.Combine(root, name, fileName)));
            }
        }
        workspace.TryApplyChanges(solution);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, workspace, root);
    }

    // The single document with the given file name in the workspace's current solution.
    public static Document DocumentNamed(Workspace workspace, string fileName) =>
        workspace.CurrentSolution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == fileName);

    // The same workspace behind the MCP dispatcher.
    public static McpHarnessOps Wire(Workspace workspace)
    {
        var server = new McpServer();
        server.LoadFromWorkspaceForTesting(workspace);
        return new McpHarnessOps(server);
    }

    // The document's text in the workspace's current solution (after an apply).
    public static async Task<string> TextOf(Workspace workspace, DocumentId documentId) =>
        (await workspace.CurrentSolution.GetDocument(documentId)!.GetTextAsync()).ToString();

    // Every compile error of every project in the workspace's current solution, as
    // "<file name> CSxxxx: message".
    public static async Task<List<string>> ErrorsOf(Workspace workspace)
    {
        var errors = new List<string>();
        foreach (var project in workspace.CurrentSolution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            errors.AddRange(compilation!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"{Path.GetFileName(d.Location.SourceTree?.FilePath)} {d.Id}: {d.GetMessage()}"));
        }
        return errors;
    }

    // The fixture text with each (from, to) edit applied in order. Every `from` must
    // occur in the text, so an expectation can never silently equal the unedited
    // fixture because an edit pattern failed to match.
    public static string Edited(string source, params (string From, string To)[] edits)
    {
        foreach (var (from, to) in edits)
        {
            source.Should().Contain(from, "an expected-text edit must apply to the fixture it describes");
            source = source.Replace(from, to);
        }
        return source;
    }

    // The in-process envelope of a tool result.
    public static JObject Json(object result) => JObject.FromObject(result);

    // A path as the server reports it for an in-memory document (no solution file:
    // absolute, forward slashes).
    public static string Reported(string path) => path.Replace('\\', '/');
}
