using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// sync_documents must not crash when a file path maps to multiple documents — a
// multi-targeted project (one Project per TFM over the same .cs files) or a linked/shared
// file appears in several projects. The old ToDictionary(path) threw ArgumentException on
// the first duplicate key, before the per-file try/catch, so the whole call escaped. The
// fix groups by path and syncs the changed text into every document that shares it.
public class SyncDocumentsDuplicatePathTests : IDisposable
{
    private readonly string _tempDir;

    public SyncDocumentsDuplicatePathTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"syncdup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task SharedFileAcrossTwoProjects_SyncsAllProjects_WithoutCrashing()
    {
        var csFile = Path.Combine(_tempDir, "Shared.cs");
        File.WriteAllText(csFile, "namespace Demo { public class C { public int N() => 1; } }");

        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };

        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        // Two projects, each including the SAME file at the SAME path (multi-TFM / linked).
        foreach (var name in new[] { "P_net8", "P_net9" })
        {
            var projectId = ProjectId.CreateNewId();
            var docId = DocumentId.CreateNewId(projectId);
            solution = solution
                .AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: refs))
                .AddDocument(DocumentInfo.Create(docId, "Shared.cs",
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(File.ReadAllText(csFile)), VersionStamp.Create())),
                    filePath: csFile));
        }
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Change the file on disk, then sync — must NOT throw on the duplicate path.
        var diskText = "namespace Demo { public class C { public int N() => 2; } }";
        File.WriteAllText(csFile, diskText);

        var result = JObject.FromObject(await service.SyncDocumentsAsync(new List<string> { csFile }));
        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        result["data"]!["updated"]!.Value<int>().Should().Be(1, "one path is synced once");

        // The change must land in BOTH projects' documents that share the path.
        var synced = service.GetSolutionForTesting()!;
        var docTexts = new List<string>();
        foreach (var project in synced.Projects)
        {
            var doc = project.Documents.Single(d => d.FilePath == csFile);
            docTexts.Add((await doc.GetTextAsync()).ToString());
        }
        docTexts.Should().HaveCount(2);
        docTexts.Should().OnlyContain(t => t.Contains("=> 2"),
            "the changed text must sync into every project that includes the shared file");
    }
}
