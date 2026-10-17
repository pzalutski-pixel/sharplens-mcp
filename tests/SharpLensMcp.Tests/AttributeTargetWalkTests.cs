using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_attributes and find_attribute_usages share one walk over every attribute
// application, so they cover the same targets — assembly, accessor, parameter, return
// value, type parameter, [field:] on an auto-property, [property:] on a record parameter,
// type, method, and each field of a multi-variable declaration — each reported with its C#
// attribute target and where the attribute is written, in that order. get_attributes'
// file: scope resolves the path like every file-addressed tool (absolute, solution-
// relative, or a unique file name); a partial name never matches, an ambiguous name and
// an unknown scope or project are errors.
public class AttributeTargetWalkTests
{
    internal const string Attrs = """
        [assembly: Lib.Marker]

        namespace Lib
        {
            [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true)]
            public sealed class MarkerAttribute : System.Attribute { }

            public class AttrHost
            {
                public int P { [Marker] get; set; }
                public void M([Marker] int x) { }
                [return: Marker] public int R() => 0;
                public void G<[Marker] T>() { }
                [field: Marker] public int Backed { get; set; }
            }

            public record Rec([property: Marker] int Value);

            [Marker]
            public class OtherAttrHost
            {
                [Marker] public void Tagged() { }
                [Marker] public int A, B;
            }
        }
        """;

    private static readonly string[] Expected =
    {
        "assembly|Assembly|Nav",
        "method|Accessor|get_P",
        "param|Parameter|x",
        "return|Method|R",
        "typevar|TypeParameter|T",
        "field|Property|Backed",
        "property|Property|Value",
        "type|Class|OtherAttrHost",
        "method|Method|Tagged",
        "field|Field|A",
        "field|Field|B",
    };

    // A solution file at <dir>/Nav.sln with Lib/Attrs.cs, Lib/Plain.cs, A/Dup.cs and
    // B/Dup.cs in project Nav, so FormatPath reports solution-relative paths.
    private static (AdhocWorkspace workspace, string directory) Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"attrwalk-{Guid.NewGuid():N}");
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(),
            filePath: Path.Combine(directory, "Nav.sln")));
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), "Nav", "Nav",
            LanguageNames.CSharp,
            filePath: Path.Combine(directory, "Nav.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
                    .First(a => a.GetName().Name == "System.Runtime").Location)
            }));
        foreach (var (relative, source) in new[]
                 {
                     ("Lib/Attrs.cs", Attrs),
                     ("Lib/Plain.cs", "namespace Lib { public class Plain { } }"),
                     ("A/Dup.cs", "namespace A { public class Dup { } }"),
                     ("B/Dup.cs", "namespace B { public class Dup { } }")
                 })
        {
            solution = solution.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(projectId), Path.GetFileName(relative),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Create())),
                filePath: Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar))));
        }
        workspace.TryApplyChanges(solution);
        return (workspace, directory);
    }

    private static List<string> Applications(JToken data, string arrayName, string nameField) =>
        ((JArray)data[arrayName]!).Select(a => $"{a["target"]}|{a["symbolKind"]}|{a[nameField]}").ToList();

    [Fact]
    public async Task GetAttributes_CoversEveryTarget_InAttributeOrder()
    {
        var (workspace, _) = Create();
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.GetAttributesAsync("Marker"));

        Applications(data, "symbols", "symbolName").Should().Equal(Expected);
        var first = ((JArray)data["symbols"]!)[0];
        first["attributeLocation"]!["filePath"]!.Value<string>().Should().Be("Lib/Attrs.cs");
        first["attributeLocation"]!["line"]!.Value<int>().Should().Be(0);
        first["attributeLocation"]!["column"]!.Value<int>().Should().Be(11);
    }

    [Fact]
    public async Task FindAttributeUsages_CoversTheSameTargets_AtTheSameLocations()
    {
        var (workspace, _) = Create();
        var service = ToolResult.Load(workspace);

        var attributes = (JArray)ToolResult.Data(await service.GetAttributesAsync("Marker"))["symbols"]!;
        var usages = (JArray)ToolResult.Data(await service.FindAttributeUsagesAsync("Marker"))["usages"]!;

        usages.Select(u => $"{u["target"]}|{u["symbolKind"]}").Should().Equal(Expected.Select(e => e[..e.LastIndexOf('|')]));
        usages.Select(u => u["attributeLocation"]!.ToString()).Should().Equal(attributes.Select(a => a["attributeLocation"]!.ToString()));
    }

    [Theory]
    [InlineData("file:Lib/Attrs.cs")]
    [InlineData("file:Attrs.cs")]
    public async Task FileScope_ResolvesTheDocument(string scope)
    {
        var (workspace, _) = Create();
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.GetAttributesAsync("Marker", scope));

        ((JArray)data["symbols"]!).Count.Should().Be(Expected.Length);
    }

    [Fact]
    public async Task FileScope_AbsolutePath_ResolvesTheDocument()
    {
        var (workspace, directory) = Create();
        var service = ToolResult.Load(workspace);

        var data = ToolResult.Data(await service.GetAttributesAsync("Marker", $"file:{Path.Combine(directory, "Lib", "Attrs.cs")}"));

        ((JArray)data["symbols"]!).Count.Should().Be(Expected.Length);
    }

    [Theory]
    [InlineData("file:rs.cs", ErrorCodes.FileNotInSolution)]
    [InlineData("file:Dup.cs", ErrorCodes.AmbiguousMatch)]
    [InlineData("project:Nope", ErrorCodes.InvalidParameter)]
    [InlineData("everywhere", ErrorCodes.InvalidParameter)]
    public async Task UnresolvableScopes_AreErrors(string scope, string code)
    {
        var (workspace, _) = Create();
        var service = ToolResult.Load(workspace);

        ToolResult.Error(await service.GetAttributesAsync("Marker", scope), code);
    }

    [Fact]
    public async Task MaxResults_OutOfDomain_IsInvalidParameter_ForBothTools()
    {
        var (workspace, _) = Create();
        var service = ToolResult.Load(workspace);

        ToolResult.Error(await service.GetAttributesAsync("Marker", maxResults: -1), ErrorCodes.InvalidParameter)
            ["Context"]!["parameter"]!.Value<string>().Should().Be("maxResults");
        ToolResult.Error(await service.FindAttributeUsagesAsync("Marker", maxResults: 0), ErrorCodes.InvalidParameter)
            ["Context"]!["parameter"]!.Value<string>().Should().Be("maxResults");
    }

    [Fact]
    public async Task Walk_ViaMcp()
    {
        var (workspace, _) = Create();
        var host = new InMemoryToolHost(workspace);

        var attributes = await host.DataAsync("get_attributes", new { attributeName = "Marker", scope = "file:Lib/Attrs.cs" });
        var usages = await host.DataAsync("find_attribute_usages", new { attributeName = "Marker" });
        await host.ErrorAsync("get_attributes", new { attributeName = "Marker", scope = "file:rs.cs" }, ErrorCodes.FileNotInSolution);

        Applications(attributes, "symbols", "symbolName").Should().Equal(Expected);
        ((JArray)usages["usages"]!).Count.Should().Be(Expected.Length);
    }
}
