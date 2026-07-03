using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// The apply path's introduced-error check uses the generator-aware compilation: an edit
// that newly references a generated member resolves against the augmented compilation, so
// it must NOT report a phantom CS error as a postApplyError. (Regression guard: in this
// AnalyzerFileReference harness the forked base compilation already contains the generated
// trees, so the base-vs-augmented difference this fix targets in a real MSBuildWorkspace
// can't be reproduced here — the test pins that the augmented path stays clean.)
public class GeneratedMemberApplyTests
{
    private sealed class DirectLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
    }

    private const string Code = @"
namespace Demo
{
    public class Target { public int Hit() => 1; }
}";

    [Fact]
    public async Task EditReferencingGeneratedMember_ReportsNoPostApplyErrors()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Target.cs");
        var dllPath = typeof(CallerSourceGenerator).Assembly.Location;
        var fileRef = new AnalyzerFileReference(dllPath, new DirectLoader());
        workspace.TryApplyChanges(
            workspace.CurrentSolution.AddAnalyzerReference(document.Project.Id, fileRef));

        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Edit Target.cs to reference the generated Demo.GenCaller type (emitted by
        // CallerSourceGenerator). The augmented compilation resolves it; the base
        // (pre-generator) one would raise a phantom CS0246/CS0103.
        var solution = workspace.CurrentSolution;
        var doc = solution.GetDocument(document.Id)!;
        var text = (await doc.GetTextAsync()).ToString();
        var edited = text.Replace(
            "public class Target { public int Hit() => 1; }",
            "public class Target { public int Hit() => 1; public int UseGen() => GenCaller.Invoke(); }");
        edited.Should().NotBe(text, "the fixture text must contain the replaced declaration");
        var newSolution = solution.WithDocumentText(doc.Id, SourceText.From(edited));

        var (ok, _, _, introduced) = await service.ApplyAndReportAsync(newSolution);

        ok.Should().BeTrue();
        introduced.Should().BeEmpty(
            "GenCaller is generated; the generator-aware compilation resolves it, so the edit is clean: "
            + string.Join(" | ", introduced));
    }
}
