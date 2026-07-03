using FluentAssertions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// change_signature must preserve parameter modifiers (ref/out/in/params/this) through
// the declaration rewrite, or the result is uncompilable. The CS1737 guard must also
// exempt a params tail, which legally follows optional parameters.
public class ChangeSignatureModifiersTests
{
    [Fact]
    public async Task AddingOptionalBeforeParams_PreservesRefOutInParams_OutputCompiles()
    {
        const string code = @"
namespace Demo
{
    public class Calc
    {
        public int Combine(ref int acc, out int count, in int seed, params int[] rest)
        {
            count = rest.Length;
            return acc + seed + count;
        }
    }

    public class Caller
    {
        public int Use()
        {
            var c = new Calc();
            int acc = 1, cnt; int seed = 5;
            return c.Combine(ref acc, out cnt, in seed, 1, 2, 3);
        }
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Calc.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Insert an optional `int flag = 0` just before the params tail.
        var changes = new List<SignatureChange>
        {
            new() { Action = "add", Name = "flag", Type = "int", DefaultValue = "0", Position = 3 }
        };
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null, changes, preview: false,
            typeName: "Calc", memberName: "Combine");
        JObject.FromObject(result)["success"]!.Value<bool>().Should().BeTrue(JObject.FromObject(result).ToString());

        // The applied declaration must still compile: ref/out/in/params survived, and an
        // optional parameter legally precedes the params tail (no CS1737 false-positive).
        var compilation = await workspace.CurrentSolution.Projects.Single().GetCompilationAsync();
        var errors = compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .ToList();
        errors.Should().BeEmpty("the rewritten declaration must keep ref/out/in/params and compile");
    }

    [Fact]
    public async Task Preview_ExtensionMethod_RendersThisModifierInNewSignature()
    {
        const string code = @"
namespace Demo
{
    public class Box { public int Value; }
    public static class BoxExtensions
    {
        public static int Doubled(this Box box, int factor) => box.Value * factor;
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Box.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var changes = new List<SignatureChange>
        {
            new() { Action = "add", Name = "offset", Type = "int", DefaultValue = "0" }
        };
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null, changes, preview: true,
            typeName: "BoxExtensions", memberName: "Doubled");
        var data = JObject.FromObject(result)["data"]!;

        data["newSignature"]!.Value<string>().Should().Contain("this Box box",
            "the extension-method receiver's `this` modifier must survive the rewrite");
    }

    [Fact]
    public async Task AddingTrailingOptional_PreservesRefReadonly_OutputCompiles()
    {
        const string code = @"
namespace Demo
{
    public class Reader
    {
        public int Read(ref readonly int x) => x;
    }

    public class Caller
    {
        public int Use()
        {
            int v = 5;
            return new Reader().Read(in v);
        }
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Reader.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var changes = new List<SignatureChange>
        {
            new() { Action = "add", Name = "y", Type = "int", DefaultValue = "0" }
        };
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null, changes, preview: false,
            typeName: "Reader", memberName: "Read");
        JObject.FromObject(result)["success"]!.Value<bool>().Should().BeTrue(JObject.FromObject(result).ToString());

        var applied = (await workspace.CurrentSolution.Projects.Single().Documents.Single().GetTextAsync()).ToString();
        applied.Should().Contain("ref readonly int x",
            "the `ref readonly` modifier must survive the declaration rewrite");

        var compilation = await workspace.CurrentSolution.Projects.Single().GetCompilationAsync();
        compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .Should().BeEmpty("the rewritten declaration must keep `ref readonly` and compile");
    }

    [Fact]
    public async Task AddingParameterAfterParams_IsRejected()
    {
        const string code = @"
namespace Demo
{
    public class C
    {
        public int Sum(params int[] nums) => 0;
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Position 1 places the new parameter AFTER the params tail.
        var changes = new List<SignatureChange>
        {
            new() { Action = "add", Name = "extra", Type = "int", Position = 1 }
        };
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null, changes, preview: false,
            typeName: "C", memberName: "Sum");
        var json = JObject.FromObject(result);

        json["success"]!.Value<bool>().Should().BeFalse("a params array must remain the final parameter");
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Contain("CS0231",
            "the rejection must cite the params-must-be-last rule");
    }

    [Fact]
    public async Task AddingParameterBeforeExtensionThis_IsRejected()
    {
        const string code = @"
namespace Demo
{
    public class Box { }
    public static class BoxExtensions
    {
        public static int M(this Box box) => 0;
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Box.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Position 0 places the new parameter BEFORE the extension `this` receiver.
        var changes = new List<SignatureChange>
        {
            new() { Action = "add", Name = "extra", Type = "int", Position = 0 }
        };
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null, changes, preview: false,
            typeName: "BoxExtensions", memberName: "M");
        var json = JObject.FromObject(result);

        json["success"]!.Value<bool>().Should().BeFalse("the extension `this` receiver must remain first");
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Contain("CS1100",
            "the rejection must cite the this-must-be-first rule");
    }
}
