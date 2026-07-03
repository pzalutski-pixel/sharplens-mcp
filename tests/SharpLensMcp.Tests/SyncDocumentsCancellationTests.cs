using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace SharpLensMcp.Tests;

// A per-call timeout that fires mid-sync must surface as cancellation, not be folded
// into a per-file error entry and returned as a partial-success envelope. sync_documents
// reads each changed file with File.ReadAllTextAsync(.., token); a pre-cancelled token
// makes that throw OperationCanceledException synchronously, which must propagate (→ the
// dispatcher's TIMEOUT result) rather than be swallowed by the per-file catch.
public class SyncDocumentsCancellationTests : IDisposable
{
    private readonly string _tempDir;

    public SyncDocumentsCancellationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"synccancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task SyncWithPreCancelledToken_PropagatesCancellation_NotPerFileError()
    {
        var csFile = Path.Combine(_tempDir, "C.cs");
        File.WriteAllText(csFile, "namespace Demo { public class C { public int N() => 1; } }");

        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var project = ProjectInfo.Create(projectId, VersionStamp.Create(), "P", "P", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs);
        var solution = workspace.CurrentSolution
            .AddProject(project)
            .AddDocument(DocumentInfo.Create(docId, "C.cs",
                loader: TextLoader.From(TextAndVersion.Create(
                    SourceText.From(File.ReadAllText(csFile)), VersionStamp.Create())),
                filePath: csFile));
        workspace.TryApplyChanges(solution).Should().BeTrue();

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        using var precancelled = new CancellationTokenSource();
        precancelled.Cancel();

        // Pre-fix this returns a success envelope whose errors[] holds the swallowed
        // "operation was canceled" message; post-fix it propagates the cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SyncDocumentsAsync(new List<string> { csFile }, precancelled.Token));
    }
}
