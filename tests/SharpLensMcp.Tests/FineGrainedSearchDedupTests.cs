using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// A multi-targeted project surfaces once per TFM, each compiling the same shared
// source file. The fine-grained searches must report a hit in that file once, not
// once per target framework.
public class FineGrainedSearchDedupTests
{
    [Fact]
    public async Task FindTypeInstantiations_DedupsHitsInAFileSharedAcrossProjects()
    {
        const string code = @"
namespace Shared
{
    public class Target { }
    public class User
    {
        public Target Make() => new Target();
    }
}";
        var sharedPath = Path.Combine(Path.GetTempPath(), "MultiTfmShared.cs");

        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var tfm in new[] { "net8.0", "netstandard2.0" })
        {
            var projectId = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), $"Lib({tfm})", "Lib", LanguageNames.CSharp,
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
                }));
            var docId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(docId, "MultiTfmShared.cs", SourceText.From(code), filePath: sharedPath);
        }
        workspace.TryApplyChanges(solution);

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.FindTypeInstantiationsAsync("Target"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        (json["data"]!["matches"] as JArray)!.Should().HaveCount(1,
            "the single `new Target()` lives in a file shared by both target frameworks and must be reported once");
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(1,
            "the deduped count, not once per TFM");
    }
}
