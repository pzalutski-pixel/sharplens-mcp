using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Generated code must fully-qualify framework and nested types so it compiles with NO
// using System / using System.Threading.Tasks and NO implicit usings — the shape a
// generated file lands in when ImplicitUsings is disabled. The existing CodegenValidationTests
// wrap snippets with `using System;`, so they never caught the unqualified references; these
// compile the output in a compilation with no such usings.
public class CodegenQualificationTests
{
    // CS0246 (type/namespace not found) and CS0103 (name not found) in a compilation that
    // references the runtime assemblies but declares NO usings. The references are the
    // runtime's own trusted platform assemblies (those in the runtime directory), which are
    // always on disk, not whatever other tests have loaded into this process from
    // temporary folders they delete.
    private static IReadOnlyList<string> UnresolvedNameErrors(string source)
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => string.Equals(Path.GetDirectoryName(path), runtimeDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        var compilation = CSharpCompilation.Create("QualTest",
            new[] { CSharpSyntaxTree.ParseText(source) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics()
            .Where(d => d.Id is "CS0246" or "CS0103")
            .Select(d => d.ToString())
            .ToList();
    }

    [Fact]
    public async Task GeneratedEqualityMembers_CompileWithNoSystemUsing()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            "public struct Money { public int Amount; public string Currency; }", "Money.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var generated = JObject.FromObject(await service.GenerateEqualityMembersAsync(
            null, null, null, includeOperators: true, typeName: "Money"))["data"]!["generatedCode"]!.Value<string>()!;

        // No `using System;`: the generated `new HashCode()` must be fully qualified.
        var source = "public struct Money { public int Amount; public string Currency;\n" + generated + "\n}";
        UnresolvedNameErrors(source).Should().BeEmpty(
            "generated equality members must fully-qualify System.HashCode to compile with no using System");
    }

    [Fact]
    public async Task GeneratedNullChecks_CompileWithNoSystemUsing()
    {
        const string code = @"public class C
{
    public void M(string x)
    {
    }
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var lines = code.Replace("\r\n", "\n").Split('\n');
        var methodLine = Array.FindIndex(lines, l => l.Contains("public void M"));
        var col = lines[methodLine].IndexOf("M(", StringComparison.Ordinal);

        var generated = JObject.FromObject(await service.AddNullChecksAsync(
            document.FilePath!, methodLine, col, preview: true))["data"]!["generatedCode"]!.Value<string>()!;

        // No `using System;`: the generated ArgumentNullException.ThrowIfNull must be qualified.
        var source = "public class C { public void M(string x) {\n" + generated + "\n} }";
        UnresolvedNameErrors(source).Should().BeEmpty(
            "generated null-check guards must fully-qualify System.ArgumentNullException");
    }

    [Fact]
    public async Task GeneratedTestStub_ForNestedTarget_FullyQualifiesTheNestedType()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            "namespace Ns { public class Outer { public class Inner { public int Go() => 1; } } }", "N.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var code = JObject.FromObject(await service.GenerateTestStubAsync("Inner", "Go"))["data"]!["testCode"]!.Value<string>()!;

        code.Should().Contain("new global::Ns.Outer.Inner(",
            "a nested SUT is not brought into scope by a using of its namespace, so it must be fully qualified");
        code.Should().NotContain("new Inner(", "the unqualified nested name would not resolve (CS0246)");
    }
}
