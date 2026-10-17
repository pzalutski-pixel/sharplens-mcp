using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// validate_code compiles a snippet the way the code would compile in the project:
//   - context mode: a member snippet ("public void Foo() {}", the schema's own example) is
//     validated as a member, a statement snippet as statements, and a complete file
//     REPLACES the context file instead of being added beside it;
//   - standalone and no-context modes compile as a new file of ONE reference project (the
//     one with the most references): its language version, compilation options (nullable
//     context, unsafe code) and global usings, reported as contextProject.
public class ValidateCodeProjectContextTests
{
    private const string ContextSource = """
        namespace Lib
        {
            public class C
            {
                public int X => 1;
            }
        }
        """;

    private const string GlobalUsings = """
        global using System;
        global using System.Threading.Tasks;
        """;

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        builder.AddProject("Old", new[] { ("O.cs", "namespace Old { public class O { } }") },
            languageVersion: LanguageVersion.CSharp7_3);
        builder.AddProject("Lib", new[] { ("C.cs", ContextSource), ("GlobalUsings.g.cs", GlobalUsings) },
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithAllowUnsafe(true),
            extraReferences: new[] { MetadataReference.CreateFromFile(typeof(System.Text.RegularExpressions.Regex).Assembly.Location) });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    private static string Summary(JToken data) =>
        $"compiles={data["compiles"]} errors=[{string.Join(",", ((JArray)data["errors"]!).Select(e => e["id"]))}] warnings=[{string.Join(",", ((JArray)data["warnings"]!).Select(e => e["id"]))}]";

    [Fact]
    public async Task ContextMode_MemberSnippet_IsValidatedAsAMember()
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.ValidateCodeAsync("public void Foo() {}", builder.FilePath("Lib", "C.cs")))["data"]!;

        Summary(data).Should().Be("compiles=True errors=[] warnings=[]");
    }

    [Fact]
    public async Task ContextMode_StatementSnippet_IsStillValidatedAsStatements()
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.ValidateCodeAsync("var n = new C().X + 1;", builder.FilePath("Lib", "C.cs")))["data"]!;

        Summary(data).Should().Be("compiles=True errors=[] warnings=[]");
    }

    [Fact]
    public async Task ContextMode_CompleteFile_ReplacesTheContextFile()
    {
        using var builder = Build(out var service, out _);
        const string newVersion = "namespace Lib { public class C { public int X => 2; public int Y => X; } }";

        var data = JObject.FromObject(await service.ValidateCodeAsync(newVersion, builder.FilePath("Lib", "C.cs")))["data"]!;

        Summary(data).Should().Be("compiles=True errors=[] warnings=[]");
    }

    // A complete file that declares none of the context file's types is a new file of the
    // context's project: it compiles beside the context file and sees its types (here a
    // test class using Lib.C), instead of replacing the file and losing C.
    [Fact]
    public async Task ContextMode_CompleteFileOfNewTypes_IsAddedBesideTheContextFile()
    {
        using var builder = Build(out var service, out var dispatcher);
        const string newFile = "namespace Lib.Tests { public class CTests { public int Uses() => new Lib.C().X; } }";

        var data = JObject.FromObject(await service.ValidateCodeAsync(newFile, builder.FilePath("Lib", "C.cs")))["data"]!;
        Summary(data).Should().Be("compiles=True errors=[] warnings=[]");

        var viaWire = await dispatcher.DataAsync("validate_code", new { code = newFile, contextFilePath = builder.FilePath("Lib", "C.cs") });
        Summary(viaWire).Should().Be("compiles=True errors=[] warnings=[]");
    }

    // A rewrite of the context file whose new version drops a member another file of the
    // project still uses is caught: the replacement is what compiles, not both versions.
    [Fact]
    public async Task ContextMode_CompleteFileRedeclaringAContextType_ReplacesIt()
    {
        using var builder = Build(out var service, out _);
        const string withoutX = "namespace Lib { public class C { public int Y => 1; } public class D { public int Z => new C().X; } }";

        var data = JObject.FromObject(await service.ValidateCodeAsync(withoutX, builder.FilePath("Lib", "C.cs")))["data"]!;

        Summary(data).Should().Be("compiles=False errors=[CS1061] warnings=[]");
    }

    [Fact]
    public async Task NoContextMode_SeesTheReferenceProjectsGlobalUsings()
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.ValidateCodeAsync("var t = Task.FromResult(1);"))["data"]!;

        Summary(data).Should().Be("compiles=True errors=[] warnings=[]");
        data["contextProject"]!.Value<string>().Should().Be("Lib");
    }

    [Fact]
    public async Task StandaloneMode_UsesTheReferenceProjectsLanguageVersionAndOptions()
    {
        using var builder = Build(out var service, out _);
        const string file = "namespace N { public record R(int X); public class X2 { public string? S { get; set; } unsafe void P(int* p) { } } }";

        var data = JObject.FromObject(await service.ValidateCodeAsync(file, standalone: true))["data"]!;

        Summary(data).Should().Be("compiles=True errors=[] warnings=[]");
        data["contextProject"]!.Value<string>().Should().Be("Lib");
    }

    [Fact]
    public async Task ViaDispatcher_SchemaExampleInContext_AndStandaloneProjectOptions()
    {
        using var builder = Build(out _, out var dispatcher);

        var member = await dispatcher.DataAsync("validate_code", new { code = "public void Foo() {}", contextFilePath = builder.FilePath("Lib", "C.cs") });
        Summary(member).Should().Be("compiles=True errors=[] warnings=[]");

        var standalone = await dispatcher.DataAsync("validate_code", new
        {
            code = "namespace N { public record R(int X); public class X2 { public string? S { get; set; } } }",
            standalone = true
        });
        Summary(standalone).Should().Be("compiles=True errors=[] warnings=[]");
        standalone["contextProject"]!.Value<string>().Should().Be("Lib");

        var error = await dispatcher.ErrorAsync("validate_code", new { code = "int x;", contextFilePath = "Nope.cs" }, ErrorCodes.FileNotInSolution);
        error["message"]!.Value<string>().Should().Be("Context file not found: Nope.cs");
    }
}
