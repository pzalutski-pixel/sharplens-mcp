using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// BuildPreviewChangeDetailsAsync drives the apply_code_fix / apply_code_action_by_title
// previews. Every entry must carry the SAME key set regardless of changeType so a
// consumer reads one shape — Added/Modified/Removed previously emitted three different
// key sets.
public class PreviewChangeDetailShapeTests
{
    [Fact]
    public async Task AddedModifiedRemoved_AllShareTheUniformKeySet()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docAId = DocumentId.CreateNewId(projectId);
        var docBId = DocumentId.CreateNewId(projectId);
        var tempDir = Path.Combine(Path.GetTempPath(), $"preview-{Guid.NewGuid():N}");

        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "P", "P", LanguageNames.CSharp);
        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(docAId, "A.cs", "class A { }", filePath: Path.Combine(tempDir, "A.cs"))
            .AddDocument(docBId, "B.cs", "class B { }", filePath: Path.Combine(tempDir, "B.cs"));
        workspace.TryApplyChanges(solution);

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var current = workspace.CurrentSolution;
        var newDocId = DocumentId.CreateNewId(projectId);
        var newSolution = current
            .WithDocumentText(docAId, SourceText.From("class A { int x; }")) // Modified
            .RemoveDocument(docBId)                                          // Removed
            .AddDocument(newDocId, "C.cs", "class C { }", filePath: Path.Combine(tempDir, "C.cs")); // Added

        var details = await service.BuildPreviewChangeDetailsAsync(newSolution);
        details.Should().HaveCount(3);

        var byType = details
            .Select(d => JObject.FromObject(d))
            .ToDictionary(j => j["changeType"]!.Value<string>()!);
        byType.Keys.Should().BeEquivalentTo(new[] { "Added", "Modified", "Removed" });

        var expectedKeys = new[]
        {
            "filePath", "fileName", "isNewFile", "changeType", "changeCount", "changes", "newText"
        };
        foreach (var (type, j) in byType)
        {
            j.Properties().Select(p => p.Name).Should().BeEquivalentTo(expectedKeys,
                $"the {type} preview entry must carry the uniform key set");
        }

        // Per-type values that legitimately differ within the uniform shape.
        byType["Added"]["isNewFile"]!.Value<bool>().Should().BeTrue();
        byType["Added"]["newText"]!.Value<string>().Should().Contain("class C");
        byType["Added"]["changeCount"]!.Value<int>().Should().Be(0);
        byType["Modified"]["changeCount"]!.Value<int>().Should().BeGreaterThan(0);
        byType["Removed"]["newText"]!.Type.Should().Be(JTokenType.Null,
            "a removed file has no new text");
    }
}
