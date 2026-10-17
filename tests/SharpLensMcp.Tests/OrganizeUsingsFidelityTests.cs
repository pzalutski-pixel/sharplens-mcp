using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// organize_usings keeps the preprocessor directives of the usings it removes and does not
// reorder a using block that preprocessor directives run through (Roslyn's own
// Remove-and-Sort contract — moving a #region/#endregion apart breaks the file with
// CS1028); removes a using only when no target framework of the file needs it; refuses to
// run on a project whose references cannot resolve System.Object (every using would look
// unnecessary there); and the batch tools resolve projectName through the logical project
// model, rejecting an unknown name instead of returning an empty success, and leave
// generated code alone.
public class OrganizeUsingsFidelityTests
{
    private const string RegionSource = """
        using System.Collections.Generic;
        #region Extra
        using System.Text;
        #endregion
        using System.Buffers;

        namespace S
        {
            public class Z { public List<int> B = new(); public ArrayPool<int> P = ArrayPool<int>.Shared; }
        }
        """;

    private const string RegionExpected = """
        using System.Collections.Generic;
        #region Extra
        #endregion
        using System.Buffers;

        namespace S
        {
            public class Z { public List<int> B = new(); public ArrayPool<int> P = ArrayPool<int>.Shared; }
        }
        """;

    private const string MultiSource = """
        using System.Text;
        using System.Buffers;

        namespace M
        {
            public class C
            {
        #if NET10_0_OR_GREATER
                public StringBuilder B = new();
        #endif
                public ArrayPool<int> P = ArrayPool<int>.Shared;
            }
        }
        """;

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        builder.AddProject("S", new[] { ("Z.cs", RegionSource) });
        builder.AddMultiTargeted("Multi", new[] { ("C.cs", MultiSource) });
        var workspace = builder.Build();

        // A project with no metadata references at all — the shape an unrestored project loads in.
        var noRefs = ProjectId.CreateNewId("NoRefs");
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(noRefs, VersionStamp.Create(), "NoRefs", "NoRefs", LanguageNames.CSharp,
                filePath: builder.ProjectFilePath("NoRefs"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
            .AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(noRefs), "N.cs",
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("using System;\nnamespace N { public class C { public Object O; } }\n"), VersionStamp.Create())),
                filePath: builder.FilePath("NoRefs", "N.cs")));
        workspace.TryApplyChanges(solution).Should().BeTrue();

        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    private const string NoReferencesMessage =
        "Cannot organize usings: project 'NoRefs' does not resolve System.Object, so every using would look unnecessary (are its references restored?)";

    [Fact]
    public async Task RemovedUsing_KeepsItsDirectives_AndADirectiveSpannedBlockIsNotReordered()
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.OrganizeUsingsAsync(builder.FilePath("S", "Z.cs")))["data"]!;

        Normalize(data["organizedText"]!.Value<string>()!).Should().Be(Normalize(RegionExpected));
        data["removedUsings"]!.Value<int>().Should().Be(1);
        CSharpSyntaxTree.ParseText(data["organizedText"]!.Value<string>()!).GetDiagnostics()
            .Select(d => d.Id).Should().BeEmpty("the organized file must still parse - no unbalanced #region");
    }

    [Fact]
    public async Task UsingNeededByOneTargetFramework_IsKept()
    {
        using var builder = Build(out var service, out _);

        var data = JObject.FromObject(await service.OrganizeUsingsAsync(builder.FilePath("Multi", "C.cs")))["data"]!;

        data["removedUsings"]!.Value<int>().Should().Be(0, "System.Text is needed by the net10.0 compilation");
        Normalize(data["organizedText"]!.Value<string>()!).Should().StartWith("using System.Buffers;\nusing System.Text;\n");
    }

    [Fact]
    public async Task ProjectWithoutCoreLibrary_IsRefused()
    {
        using var builder = Build(out var service, out _);

        var single = JObject.FromObject(await service.OrganizeUsingsAsync(builder.FilePath("NoRefs", "N.cs")));
        single["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AnalysisFailed);
        single["error"]!["Message"]!.Value<string>().Should().Be(NoReferencesMessage);

        var batch = JObject.FromObject(await service.OrganizeUsingsBatchAsync("NoRefs", null))["data"]!;
        ((JArray)batch["files"]!).Select(f => f["error"]!.Value<string>()).Should().Equal(NoReferencesMessage);
        batch["filesWithChanges"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task Batches_ResolveTheLogicalProject_AndRejectAnUnknownName()
    {
        using var builder = Build(out var service, out _);

        var organize = JObject.FromObject(await service.OrganizeUsingsBatchAsync("Multi", null))["data"]!;
        ((JArray)organize["files"]!).Select(f => $"{f["filePath"]} {f["projectName"]} {f["removedUsings"]}")
            .Should().Equal("Multi/C.cs Multi 0");
        organize["totalFilesScanned"]!.Value<int>().Should().Be(1);

        var unknownOrganize = JObject.FromObject(await service.OrganizeUsingsBatchAsync("nope", null));
        unknownOrganize["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        unknownOrganize["error"]!["Message"]!.Value<string>().Should().Be("Project not found: nope");

        var unknownFormat = JObject.FromObject(await service.FormatDocumentBatchAsync("nope"));
        unknownFormat["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        unknownFormat["error"]!["Message"]!.Value<string>().Should().Be("Project not found: nope");

        var format = JObject.FromObject(await service.FormatDocumentBatchAsync("Multi"))["data"]!;
        format["totalFilesScanned"]!.Value<int>().Should().Be(1, "one file, however many frameworks compile it");
    }

    [Fact]
    public async Task Batches_LeaveGeneratedCodeAlone()
    {
        // Build-generated files (a *.g.cs, a file with an <auto-generated> header) are not
        // the developer's to reorganize or reformat; the batches skip them as generated code.
        using var builder = new QualitySolutionBuilder();
        builder.AddProject("G", new[]
        {
            ("Own.cs", "using System.Text;\nnamespace G { public class Own { } }\n"),
            ("Gen.g.cs", "using System.Text;\nnamespace G { public class Gen { } }\n"),
            ("Header.cs", "// <auto-generated/>\nusing System.Text;\nnamespace G {   public class Header   { } }\n"),
        });
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(builder.Build());

        var organize = JObject.FromObject(await service.OrganizeUsingsBatchAsync("G", null))["data"]!;
        ((JArray)organize["files"]!).Select(f => f["fileName"]!.Value<string>()).Should().Equal("Own.cs");
        organize["totalFilesScanned"]!.Value<int>().Should().Be(1);

        var format = JObject.FromObject(await service.FormatDocumentBatchAsync("G"))["data"]!;
        ((JArray)format["files"]!).Should().BeEmpty("Own.cs is already formatted and the generated files are skipped");
        format["totalFilesScanned"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task ViaDispatcher_DirectivesKept_AndUnknownBatchProject()
    {
        using var builder = Build(out _, out var dispatcher);

        var data = await dispatcher.DataAsync("organize_usings", new { filePath = builder.FilePath("S", "Z.cs") });
        Normalize(data["organizedText"]!.Value<string>()!).Should().Be(Normalize(RegionExpected));

        var error = await dispatcher.ErrorAsync("format_document_batch", new { projectName = "nope" }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("Project not found: nope");

        var refused = await dispatcher.ErrorAsync("organize_usings", new { filePath = builder.FilePath("NoRefs", "N.cs") }, ErrorCodes.AnalysisFailed);
        refused["message"]!.Value<string>().Should().Be(NoReferencesMessage);
    }
}
