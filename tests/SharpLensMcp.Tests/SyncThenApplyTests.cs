using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Invariant guard for the sync→apply interaction: after sync_documents reads changed
// files from disk, a subsequent apply must succeed AND sync must never write back to
// disk (the historical regression was sync adding <Compile Include> entries via
// TryApplyChanges). NOTE: the MSBuildWorkspace-specific TryApplyChanges rejection that
// motivates a re-root fix is NOT reproducible here — AdhocWorkspace is lenient about
// solution lineage — so this test pins the no-disk-write half and the AdhocWorkspace
// apply path, not the MSBuild rejection.
public class SyncThenApplyTests : IDisposable
{
    private readonly string _tempDir;

    public SyncThenApplyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"syncapply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task SyncThenChangeSignature_AppliesSuccessfully()
    {
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location),
        };
        var csFile = Path.Combine(_tempDir, "C.cs");
        File.WriteAllText(csFile, "namespace Demo { public class C { public int Add(int amount) => amount + 1; } }");

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

        // Change the file on disk, then sync it into the in-memory solution.
        var diskText = "namespace Demo { public class C { public int Add(int amount) => amount + 2; } }";
        File.WriteAllText(csFile, diskText);
        var sync = JObject.FromObject(await service.SyncDocumentsAsync(new List<string> { csFile }));
        sync["success"]!.Value<bool>().Should().BeTrue(sync.ToString());

        // A subsequent apply must succeed despite the sync having advanced the solution.
        var apply = JObject.FromObject(await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "add", Name = "extra", Type = "int", DefaultValue = "0" } },
            preview: false, typeName: "C", memberName: "Add"));
        apply["success"]!.Value<bool>().Should().BeTrue(
            "an apply after sync_documents must not be rejected by the workspace: " + apply);

        // Sync must not have written back to disk: the file still holds the disk text.
        File.ReadAllText(csFile).Should().Be(diskText, "sync reads FROM disk; it must not write back");
    }
}
