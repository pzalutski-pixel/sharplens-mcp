using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// validate_code in context mode must report only diagnostics located in the snippet's
// own tree. A project-wide diagnostic with no location must not be attributed to the
// snippet.
public class ValidateCodeContextModeTests
{
    [Fact]
    public async Task ContextMode_LocationLessProjectDiagnostic_NotReportedForCleanSnippet()
    {
        var ctxPath = Path.Combine(Path.GetTempPath(), $"vc-{Guid.NewGuid():N}.cs");
        var refs = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "System.Runtime").Location)
        };
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        // A console app whose declared entry-point type does not exist yields a
        // location-less error (CS1555), independent of any source file.
        var projectInfo = ProjectInfo.Create(
            projectId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication)
                .WithMainTypeName("App.DoesNotExist"),
            metadataReferences: refs);

        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution.AddProject(projectInfo)
            .AddDocument(docId, "Thing.cs",
                "namespace App { public class Thing { public int Get() => 1; } }", filePath: ctxPath);
        workspace.TryApplyChanges(solution);

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Sanity: the project carries a location-less error, so the filter is exercised.
        var comp = await workspace.CurrentSolution.Projects.Single().GetCompilationAsync();
        comp!.GetDiagnostics()
            .Any(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == null)
            .Should().BeTrue("the setup must induce a location-less project error");

        var result = await service.ValidateCodeAsync("int x = 1 + 2;", contextFilePath: ctxPath);
        var data = JObject.FromObject(result)["data"]!;
        data["errorCount"]!.Value<int>().Should().Be(0,
            "a project-wide location-less diagnostic is not about the snippet and must not be reported");
    }
}
