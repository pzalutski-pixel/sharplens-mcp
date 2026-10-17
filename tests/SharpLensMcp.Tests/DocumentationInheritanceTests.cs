using System.Text;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// get_documentation's rendering and <inheritdoc/> resolution:
//   - inherited <param>/<typeparam> entries map to the inheriting member's parameters by
//     position, under the inheriting member's own names;
//   - <para> blocks stay separate paragraphs, and <code> keeps its line breaks (with the
//     comment's indentation removed);
//   - a bare <inheritdoc/> on a constructor inherits from the base class constructor with
//     the same parameter types;
//   - a metadata symbol's cref resolves in a compilation that references its assembly,
//     not in whichever project happens to be first.
public class DocumentationInheritanceTests
{
    internal const string Code = """
        namespace Lib
        {
            public interface IDoc
            {
                /// <summary>Base summary.</summary>
                /// <param name="value">The value.</param>
                /// <typeparam name="T">The type.</typeparam>
                void Do<T>(int value);
            }

            public class DocImpl : IDoc
            {
                /// <inheritdoc/>
                public void Do<TItem>(int renamed) { }

                /// <summary><para>One</para><para>Two</para></summary>
                public void Paras() { }

                /// <summary>Sample.</summary>
                /// <remarks>Code:<code>
                /// var x = 1;
                /// var y = 2;
                /// </code></remarks>
                public void Sample() { }
            }

            public class BaseCtor
            {
                /// <summary>Creates a base.</summary>
                /// <param name="size">The size.</param>
                public BaseCtor(int size) { }
            }

            public class DerivedCtor : BaseCtor
            {
                /// <inheritdoc/>
                public DerivedCtor(int size) : base(size) { }
            }
        }
        """;

    private static RoslynService Load()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Docs.cs");
        return ToolResult.Load(workspace);
    }

    private static List<string> Entries(JToken docs, string section) =>
        ((JArray)docs[section]!).Select(e => $"{e["name"]}={e["text"]}").ToList();

    [Fact]
    public async Task InheritedParams_MapByPosition_ToTheInheritingNames()
    {
        var data = ToolResult.Data(await Load().GetDocumentationAsync("Lib.DocImpl", "Do"));

        var docs = data["documentation"]!;
        docs["summary"]!.Value<string>().Should().Be("Base summary.");
        Entries(docs, "params").Should().Equal("renamed=The value.");
        Entries(docs, "typeParams").Should().Equal("TItem=The type.");
        data["inheritedFrom"]!.Value<string>().Should().Be("Lib.IDoc.Do<T>(int)");
    }

    [Fact]
    public async Task Paragraphs_And_CodeBlocks_KeepTheirStructure()
    {
        var service = Load();

        var paras = ToolResult.Data(await service.GetDocumentationAsync("Lib.DocImpl", "Paras"));
        var sample = ToolResult.Data(await service.GetDocumentationAsync("Lib.DocImpl", "Sample"));

        paras["documentation"]!["summary"]!.Value<string>().Should().Be("One\n\nTwo");
        sample["documentation"]!["remarks"]!.Value<string>().Should().Be("Code:\n\nvar x = 1;\nvar y = 2;");
    }

    [Fact]
    public async Task BareInheritdocOnAConstructor_InheritsFromTheMatchingBaseConstructor()
    {
        var data = ToolResult.Data(await Load().GetDocumentationAsync("Lib.DerivedCtor", "DerivedCtor"));

        data["documentation"]!["summary"]!.Value<string>().Should().Be("Creates a base.");
        Entries(data["documentation"]!, "params").Should().Equal("size=The size.");
        data["inheritedFrom"]!.Value<string>().Should().Be("Lib.BaseCtor.BaseCtor(int)");
    }

    // DocLib, compiled to metadata with its XML documentation: Copy.Mirror inherits its
    // docs from Source.Origin by cref. Only the second project references DocLib.
    private const string DocLibSource = """
        namespace DocLib
        {
            public class Source
            {
                /// <summary>Source summary.</summary>
                public void Origin() { }
            }

            public class Copy
            {
                /// <inheritdoc cref="Source.Origin"/>
                public void Mirror() { }
            }
        }
        """;

    internal static AdhocWorkspace CreateMetadataWorkspace()
    {
        var core = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var runtime = MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location);
        var docLib = CSharpCompilation.Create("DocLib",
            new[] { CSharpSyntaxTree.ParseText(DocLibSource, new CSharpParseOptions(documentationMode: DocumentationMode.Diagnose)) },
            new[] { core, runtime }, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        using var xml = new MemoryStream();
        var emit = docLib.Emit(image, xmlDocumentationStream: xml);
        emit.Success.Should().BeTrue(string.Join("; ", emit.Diagnostics));
        var docLibReference = MetadataReference.CreateFromImage(image.ToArray(),
            documentation: XmlDocumentationProvider.CreateFromBytes(xml.ToArray()));

        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var (name, references) in new[]
                 {
                     ("First", new[] { core, runtime }),
                     ("Second", new[] { core, runtime, docLibReference })
                 })
        {
            var projectId = ProjectId.CreateNewId();
            solution = solution
                .AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: references))
                .AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(projectId), $"{name}.cs",
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From($"namespace {name} {{ public class Marker {{ }} }}", Encoding.UTF8), VersionStamp.Create())),
                    filePath: Path.Combine(Path.GetTempPath(), $"adhoc-{Guid.NewGuid():N}", $"{name}.cs")));
        }
        workspace.TryApplyChanges(solution);
        return workspace;
    }

    [Fact]
    public async Task MetadataCref_ResolvesInAProjectThatReferencesTheAssembly()
    {
        var service = ToolResult.Load(CreateMetadataWorkspace());

        var data = ToolResult.Data(await service.GetDocumentationAsync("DocLib.Copy", "Mirror"));

        data["documentation"]!["summary"]!.Value<string>().Should().Be("Source summary.");
        data["inheritedFrom"]!.Value<string>().Should().Be("DocLib.Source.Origin()");
        data["unresolved"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task Inheritance_ViaMcp()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Docs.cs");
        var host = new InMemoryToolHost(workspace);

        var doc = await host.DataAsync("get_documentation", new { typeName = "Lib.DocImpl", memberName = "Do" });
        var paras = await host.DataAsync("get_documentation", new { typeName = "Lib.DocImpl", memberName = "Paras" });
        var metadata = await new InMemoryToolHost(CreateMetadataWorkspace())
            .DataAsync("get_documentation", new { typeName = "DocLib.Copy", memberName = "Mirror" });

        Entries(doc["documentation"]!, "params").Should().Equal("renamed=The value.");
        paras["documentation"]!["summary"]!.Value<string>().Should().Be("One\n\nTwo");
        metadata["documentation"]!["summary"]!.Value<string>().Should().Be("Source summary.");
    }
}
