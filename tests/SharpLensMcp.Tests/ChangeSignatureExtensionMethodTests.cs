using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// change_signature must keep reduced-form extension call sites (x.Ext(args)) aligned:
// the receiver is not in the argument list, so positional indices are offset by one
// from the parameter list (which includes the `this` parameter).
public class ChangeSignatureExtensionMethodTests
{
    [Fact]
    public async Task AddParameter_OnReducedFormExtensionCall_KeepsArgumentsAligned()
    {
        const string code = @"
namespace Demo
{
    public static class Ext
    {
        public static int Add(this int self, int a, int b) => self + a + b;
    }

    public class Caller
    {
        public int Use() => 5.Add(1, 2);
    }
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "Ext.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Append a parameter. The reduced-form call 5.Add(1, 2) must become
        // 5.Add(1, 2, default) — the existing args stay aligned with a/b and one new
        // argument is appended — NOT 5.Add(1, 2, default, default) (the off-by-one that
        // treats the receiver as an argument and appends one too many).
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "add", Name = "c", Type = "int" } },
            preview: false, typeName: "Ext", memberName: "Add");
        JObject.FromObject(result)["success"]!.Value<bool>().Should().BeTrue(JObject.FromObject(result).ToString());

        var applied = (await workspace.CurrentSolution.GetDocument(document.Id)!.GetTextAsync()).ToString();
        applied.Should().Be(code
            .Replace("Add(this int self, int a, int b)", "Add(this int self, int a, int b, int c)")
            .Replace("5.Add(1, 2)", "5.Add(1, 2, default)"),
            "the two existing args stay aligned; one new arg is appended");

        var compilation = await workspace.CurrentSolution.Projects.Single().GetCompilationAsync();
        compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .Should().BeEmpty("the rewritten reduced-form call site must compile (3 args for self/a/b/c minus receiver)");
    }
}
