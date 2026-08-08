using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Guards the reverse frontier's cross-project caller resolution. The path App.Service.Entry
// -> App.Service.Mid -> Lib.Target.Sink can only close if, with maxHops=2, the backward
// search finds Mid (in App) as a caller of Sink (in Lib) — a CROSS-project member caller.
// SymbolFinder.FindCallersAsync misses those (Roslyn #41616), so find_path_between resolves
// callers via FindReferencesAsync; if it regressed to FindCallersAsync this test would fail.
public class ReachabilityCrossProjectTests
{
    private static readonly MetadataReference[] CoreRefs =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location)
    };

    [Fact]
    public async Task Path_ClosesOnlyViaCrossProjectCaller()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reach-xproj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;

            // P1 "Lib": the target lives here.
            var libId = ProjectId.CreateNewId();
            var libDocId = DocumentId.CreateNewId(libId);
            var libCode = "namespace Lib { public class Target { public void Sink() { } } }";
            solution = solution
                .AddProject(ProjectInfo.Create(libId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.CSharp,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: CoreRefs))
                .AddDocument(DocumentInfo.Create(libDocId, "Target.cs",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(libCode), VersionStamp.Create())),
                    filePath: Path.Combine(tempDir, "Target.cs")));

            // P2 "App" references Lib. Entry -> Mid -> Lib.Target.Sink; Sink's only caller (Mid)
            // is in THIS project, i.e. cross-project relative to Sink.
            var appId = ProjectId.CreateNewId();
            var appDocId = DocumentId.CreateNewId(appId);
            var appCode = "namespace App { using Lib; public class Service { " +
                          "public void Entry() { Mid(); } public void Mid() { new Target().Sink(); } } }";
            solution = solution
                .AddProject(ProjectInfo.Create(appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: CoreRefs,
                    projectReferences: new[] { new ProjectReference(libId) }))
                .AddDocument(DocumentInfo.Create(appDocId, "Service.cs",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(appCode), VersionStamp.Create())),
                    filePath: Path.Combine(tempDir, "Service.cs")));

            workspace.TryApplyChanges(solution).Should().BeTrue();

            var service = new RoslynService();
            service.LoadFromWorkspaceForTesting(workspace);

            // maxHops=2 forces the meet through the reverse frontier: forward reaches Mid
            // (depth 1), backward must reach Mid from Sink (depth 1) — a cross-project caller.
            var result = await service.FindPathBetweenAsync(
                new List<MemberRef> { new("Service", "Entry") },
                new List<MemberRef> { new("Target", "Sink") },
                null, null, maxHops: 2);

            var json = JObject.FromObject(result);
            json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
            var pair = (JObject)((JArray)json["data"]!["pairs"]!)[0];
            pair["reachable"]!.Value<bool>().Should().BeTrue(
                "the path closes only if the cross-project caller of Sink is found via FindReferences");
            pair["hopCount"]!.Value<int>().Should().Be(3, "Entry -> Mid -> Sink");
            var hops = ((JArray)pair["hops"]!).Select(h => h["symbol"]!.Value<string>()!).ToList();
            hops.First().Should().EndWith("Service.Entry()");
            hops.Last().Should().EndWith("Target.Sink()");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
