using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// remove_unused_code must not leave a typed declaration with no variables (`int ;`)
// when every declarator of a multi-declarator field is removed, and must keep the
// surviving declarators on a partial removal.
public class RemoveUnusedCodeMultiDeclaratorTests
{
    private const string Code = @"
namespace Demo
{
    public class Holder
    {
        private int a, b;
        private int c, d;
        public int UseD() => d;
    }
}";

    [Fact]
    public async Task RemovingAllDeclarators_RemovesWholeDeclaration_KeepsSurvivors()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Holder.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.RemoveUnusedCodeAsync(
            projectName: "TestProject",
            symbolNames: new List<string> { "a", "b", "c" });
        var data = JObject.FromObject(result)["data"]!;

        var content = (data["files"] as JArray)!.Single()["updatedContent"]!.Value<string>()!;

        content.Should().NotContain("int ;",
            "removing both declarators of `int a, b;` must remove the whole declaration, not leave `int ;`");
        content.Should().NotContain("int  ;");
        content.Should().Contain("int d",
            "d is still used, so its declarator survives the partial removal of `int c, d;`");

        // The strongest check: the rewritten file parses with no syntax errors.
        var errors = CSharpSyntaxTree.ParseText(content).GetDiagnostics()
            .Where(diag => diag.Severity == DiagnosticSeverity.Error)
            .Select(diag => diag.GetMessage())
            .ToList();
        errors.Should().BeEmpty("the rewritten document must be syntactically valid");
    }
}
