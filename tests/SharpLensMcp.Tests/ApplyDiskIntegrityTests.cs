using System.Text;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The shared apply path against a real MSBuild-loaded solution on disk: an apply writes
// only the documents the edit changed, refuses (STALE_DOCUMENT) when a target file
// changed on disk since it was read, keeps every file's encoding, byte-order mark and
// line-ending style, never adds a Compile item for a file the project's globs already
// include, and checks every target framework of the changed project and every project
// that depends on it for introduced compile errors.
public class ApplyDiskIntegrityTests
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static async Task<RoslynService> LoadAsync(DiskSolution solution)
    {
        var service = new RoslynService();
        var load = JObject.FromObject(await service.LoadSolutionAsync(solution.SolutionPath));
        load["success"]!.Value<bool>().Should().BeTrue(load.ToString());
        return service;
    }

    private static async Task<JObject> RenameSpinAsync(RoslynService service, bool preview = false, string? verbosity = null) =>
        JObject.FromObject(await service.RenameSymbolAsync(
            filePath: null, line: null, column: null, newName: "Spin2", preview: preview,
            verbosity: verbosity, typeName: "Widget", memberName: "Spin"));

    private static string Renamed(string text) => text.Replace("Spin(", "Spin2(");

    [Fact]
    public async Task SyncThenApplyElsewhere_LeavesAnUnsyncedDiskEditAlone()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);
        solution.WriteText("App/Other.cs", ApplySyncFixture.Other + "// edit1\n");
        JObject.FromObject(await service.SyncDocumentsAsync(new List<string> { "App/Other.cs" }))["success"]!
            .Value<bool>().Should().BeTrue();
        solution.WriteText("App/Other.cs", ApplySyncFixture.Other + "// edit1\n// edit2\n");

        var rename = await RenameSpinAsync(service);

        rename["success"]!.Value<bool>().Should().BeTrue(rename.ToString());
        solution.ReadBytes("App/Other.cs").Should().Equal(
            Utf8NoBom.GetBytes(ApplySyncFixture.Other + "// edit1\n// edit2\n"),
            "the rename does not touch Other.cs, so its unsynced edit and its no-BOM encoding must survive");
        ((JArray)rename["data"]!["changes"]!).Select(c => c["filePath"]!.Value<string>())
            .Should().BeEquivalentTo(new[] { "Lib/Widget.cs", "App/Service.cs" });
        solution.ReadText("App/Service.cs").Should().Be(Renamed(ApplySyncFixture.Service));
        solution.ReadText("Lib/Widget.cs").Should().Be(Renamed(ApplySyncFixture.Widget));
    }

    [Fact]
    public async Task ApplyOverAFileEditedOnDiskSinceItWasRead_IsRefusedAsStale()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);
        // The preview reads Service.cs: from here on the loaded text is what the edit is
        // computed from. (A document never read is loaded from disk on first use, so an
        // edit made before that is simply part of what the tool reads.)
        (await RenameSpinAsync(service, preview: true))["success"]!.Value<bool>().Should().BeTrue();
        var userEdit = ApplySyncFixture.Service + "// user work not yet synced\n";
        solution.WriteText("App/Service.cs", userEdit);

        var rename = await RenameSpinAsync(service);

        rename["success"]!.Value<bool>().Should().BeFalse("Service.cs changed on disk after it was read");
        rename["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.StaleDocument);
        rename["error"]!["Hint"]!.Value<string>().Should().Be("run sync_documents, then retry");
        ((JArray)rename["error"]!["Context"]!["staleFiles"]!).Select(f => f.Value<string>())
            .Should().Equal("App/Service.cs");
        solution.ReadText("App/Service.cs").Should().Be(userEdit, "the user's disk edit must not be overwritten");
        solution.ReadText("Lib/Widget.cs").Should().Be(ApplySyncFixture.Widget,
            "a stale target refuses the WHOLE apply, so no other file is written either");
    }

    [Fact]
    public async Task SyncThenApplyInTheSyncedFile_KeepsItWithoutABom()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);
        JObject.FromObject(await service.SyncDocumentsAsync(new List<string> { "Lib/Widget.cs" }))["success"]!
            .Value<bool>().Should().BeTrue();

        (await RenameSpinAsync(service))["success"]!.Value<bool>().Should().BeTrue();

        solution.ReadBytes("Lib/Widget.cs").Should().Equal(Utf8NoBom.GetBytes(Renamed(ApplySyncFixture.Widget)),
            "the file had no BOM before the apply, so it has none after");
    }

    [Fact]
    public async Task ApplyInAFileWithABom_KeepsTheBom()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteBytes("Lib/Widget.cs", Utf8Bom.Concat(Utf8NoBom.GetBytes(ApplySyncFixture.Widget)).ToArray());
        solution.Restore();
        var service = await LoadAsync(solution);
        JObject.FromObject(await service.SyncDocumentsAsync(null))["success"]!.Value<bool>().Should().BeTrue();

        (await RenameSpinAsync(service))["success"]!.Value<bool>().Should().BeTrue();

        solution.ReadBytes("Lib/Widget.cs").Should().Equal(
            Utf8Bom.Concat(Utf8NoBom.GetBytes(Renamed(ApplySyncFixture.Widget))).ToArray());
    }

    [Fact]
    public async Task OrganizeUsingsApply_KeepsANoBomFileWithoutABom()
    {
        using var solution = ApplySyncFixture.Create();
        const string unsorted =
            "using System.Text;\n" +
            "using System;\n" +
            "\n" +
            "namespace App;\n" +
            "\n" +
            "public class Usings\n" +
            "{\n" +
            "    public StringBuilder Make() => new StringBuilder(Environment.NewLine);\n" +
            "}\n";
        solution.WriteText("App/Usings.cs", unsorted);
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.OrganizeUsingsAsync("App/Usings.cs", preview: false));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        solution.ReadBytes("App/Usings.cs").Should().Equal(Utf8NoBom.GetBytes(
            "using System;\n" +
            "using System.Text;\n" +
            "\n" +
            "namespace App;\n" +
            "\n" +
            "public class Usings\n" +
            "{\n" +
            "    public StringBuilder Make() => new StringBuilder(Environment.NewLine);\n" +
            "}\n"));
    }

    [Fact]
    public async Task SyncThenApplyInAWindows1252File_WritesWindows1252Back()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteBytes("App/Latin.cs", ApplySyncFixture.Windows1252.GetBytes(ApplySyncFixture.Latin));
        solution.Restore();
        var service = await LoadAsync(solution);
        JObject.FromObject(await service.SyncDocumentsAsync(null))["success"]!.Value<bool>().Should().BeTrue();

        var rename = JObject.FromObject(await service.RenameSymbolAsync(
            filePath: null, line: null, column: null, newName: "Hello", preview: false,
            typeName: "Latin", memberName: "Greet"));

        rename["success"]!.Value<bool>().Should().BeTrue(rename.ToString());
        solution.ReadBytes("App/Latin.cs").Should().Equal(
            ApplySyncFixture.Windows1252.GetBytes(ApplySyncFixture.Latin.Replace("Greet", "Hello")),
            "the file is windows-1252 without a BOM and must stay that way, with 'é' intact");
    }

    [Fact]
    public async Task SyncedNewFileThenApply_LeavesTheProjectFileUntouched()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);
        var projectBefore = solution.ReadBytes("App/App.csproj");
        solution.WriteText("App/NewOne.cs", "namespace App;\n\npublic class NewOne\n{\n}\n");
        var sync = JObject.FromObject(await service.SyncDocumentsAsync(new List<string> { "App/NewOne.cs" }));
        sync["data"]!["added"]!.Value<int>().Should().Be(1);

        (await RenameSpinAsync(service))["success"]!.Value<bool>().Should().BeTrue();

        solution.ReadBytes("App/App.csproj").Should().Equal(projectBefore,
            "NewOne.cs already exists on disk and the SDK globs compile it: no Compile item may be added");
        solution.ReadText("App/NewOne.cs").Should().Be("namespace App;\n\npublic class NewOne\n{\n}\n");
    }

    [Fact]
    public async Task CodeActionAddingADocument_WritesIt_AndLeavesTheSdkProjectFileUntouched()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteText("App/Pair.cs", "namespace App;\n\npublic class One\n{\n}\n\npublic class Two\n{\n}\n");
        solution.Restore();
        var service = await LoadAsync(solution);
        var projectBefore = solution.ReadBytes("App/App.csproj");

        var result = JObject.FromObject(await service.ApplyCodeActionByTitleAsync(
            "App/Pair.cs", line: 6, column: 13, title: "Move type to Two.cs", preview: false));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        solution.ReadBytes("App/App.csproj").Should().Equal(projectBefore,
            "the SDK globs compile Two.cs, so the project file must not gain a Compile item");
        solution.ReadBytes("App/Two.cs").Should().Equal(Utf8NoBom.GetBytes("namespace App;\n\npublic class Two\n{\n}\n"));
        solution.ReadText("App/Pair.cs").Should().Be("namespace App;\n\npublic class One\n{\n}\n");
        ((JArray)result["data"]!["changedFiles"]!).Select(f => (f["filePath"]!.Value<string>(), f["changeType"]!.Value<string>()))
            .Should().BeEquivalentTo(new[] { ("App/Two.cs", "Added"), ("App/Pair.cs", "Modified") });
    }

    [Fact]
    public async Task CodeActionAddingADocument_ToAProjectWithExplicitCompileItems_AddsItsCompileItem()
    {
        using var solution = new DiskSolution("Explicit/Explicit.csproj");
        const string project =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n" +
            "  </PropertyGroup>\n" +
            "\n" +
            "  <ItemGroup>\n" +
            "    <Compile Include=\"Pair.cs\" />\n" +
            "  </ItemGroup>\n" +
            "\n" +
            "</Project>\n";
        solution.WriteText("Explicit/Explicit.csproj", project);
        solution.WriteText("Explicit/Pair.cs", "namespace Explicit;\n\npublic class One\n{\n}\n\npublic class Two\n{\n}\n");
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.ApplyCodeActionByTitleAsync(
            "Explicit/Pair.cs", line: 6, column: 13, title: "Move type to Two.cs", preview: false));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        solution.ReadBytes("Explicit/Explicit.csproj").Should().Equal(Utf8NoBom.GetBytes(project.Replace(
            "    <Compile Include=\"Pair.cs\" />\n",
            "    <Compile Include=\"Pair.cs\" />\n    <Compile Include=\"Two.cs\" />\n")),
            "without default compile items the new file needs its own Compile item; the rest of the file is kept byte for byte");
    }

    [Fact]
    public async Task MultiTargetedProject_ReportsTheErrorInTheFrameworkTheEditBreaks()
    {
        using var solution = new DiskSolution("Multi/Multi.csproj");
        solution.WriteText("Multi/Multi.csproj", DiskSolution.SdkProject("net8.0;netstandard2.0"));
        const string guard =
            "namespace Multi;\n" +
            "\n" +
            "public class Guard\n" +
            "{\n" +
            "    public void Take(object value)\n" +
            "    {\n" +
            "    }\n" +
            "}\n";
        solution.WriteText("Multi/Guard.cs", guard);
        solution.Restore();
        var service = await LoadAsync(solution);

        // System.HashCode exists on net8.0 and not on netstandard2.0, so the new parameter
        // compiles in one framework only.
        var result = JObject.FromObject(await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "add", Name = "hash", Type = "System.HashCode", DefaultValue = "default" } },
            preview: false, typeName: "Guard", memberName: "Take"));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        ((JArray)result["data"]!["postApplyErrors"]!).Select(e => e.Value<string>()).Should().Equal(
            "Multi/Guard.cs (line 4) CS0234: The type or namespace name 'HashCode' does not exist in the namespace 'System' (are you missing an assembly reference?)");
        var texts = new List<string>();
        foreach (var document in service.GetSolutionForTesting()!.Projects.SelectMany(p => p.Documents)
                     .Where(d => d.FilePath == solution.PathOf("Multi/Guard.cs")))
        {
            texts.Add((await document.GetTextAsync()).ToString());
        }
        texts.Should().HaveCount(2, "one document per target framework");
        texts.Distinct().Should().HaveCount(1, "every target framework's document carries the applied text");
        texts[0].Should().Be(solution.ReadText("Multi/Guard.cs"));
    }

    [Fact]
    public async Task EditBreakingADependentProject_IsReported()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteText("App/Delegates.cs",
            "using System;\n" +
            "using Lib;\n" +
            "\n" +
            "namespace App;\n" +
            "\n" +
            "public class Delegates\n" +
            "{\n" +
            "    public Func<int, int> Make() => new Widget().Spin;\n" +
            "}\n");
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "add", Name = "extra", Type = "int", DefaultValue = "0" } },
            preview: false, typeName: "Widget", memberName: "Spin"));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        ((JArray)result["data"]!["postApplyErrors"]!).Select(e => e.Value<string>()).Should().Equal(
            "App/Delegates.cs (line 7) CS0123: No overload for 'Spin' matches delegate 'Func<int, int>'");
    }

    [Fact]
    public async Task NewErrorIdenticalToAnExistingOneInTheSameFile_IsReported()
    {
        const string code =
            "namespace N\n" +
            "{\n" +
            "    public class A\n" +
            "    {\n" +
            "        public int X() => foo;\n" +
            "    }\n" +
            "}\n";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "A.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var edited = code.Replace(
            "        public int X() => foo;\n",
            "        public int X() => foo;\n        public int Y() => foo;\n");
        var newSolution = workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(edited));

        var (ok, _, _, introduced) = await service.ApplyAndReportAsync(newSolution);

        ok.Should().BeTrue();
        introduced.Should().Equal(
            $"{document.FilePath!.Replace('\\', '/')} (line 5) CS0103: The name 'foo' does not exist in the current context");
    }

    [Fact]
    public async Task FormatterAnnotatedNodes_AreFormatted_AndUntouchedCodeIsNot()
    {
        const string code =
            "namespace N\n" +
            "{\n" +
            "    public class A\n" +
            "    {\n" +
            "        public int X()   =>   1;\n" +
            "    }\n" +
            "}\n";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "A.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync())!;
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        var generated = SyntaxFactory.ParseMemberDeclaration("public int Y(){return 2;}")!
            .WithTrailingTrivia(SyntaxFactory.LineFeed)
            .WithAdditionalAnnotations(Formatter.Annotation);
        var newSolution = workspace.CurrentSolution.WithDocumentSyntaxRoot(
            document.Id, root.InsertNodesAfter(method, new[] { generated }));

        var (ok, _, _, _) = await service.ApplyAndReportAsync(newSolution);

        ok.Should().BeTrue();
        var applied = service.GetSolutionForTesting()!.GetDocument(document.Id)!;
        (await applied.GetTextAsync()).ToString().Should().Be(
            "namespace N\n" +
            "{\n" +
            "    public class A\n" +
            "    {\n" +
            "        public int X()   =>   1;\n" +
            "        public int Y() { return 2; }\n" +
            "    }\n" +
            "}\n",
            "the annotated member is indented and spaced by the formatter; X's own spacing is not touched");
    }

    [Fact]
    public async Task RenamePreview_ReportsEveryEditedSpan()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);

        var preview = await RenameSpinAsync(service, preview: true, verbosity: "compact");

        var changes = (JArray)preview["data"]!["changes"]!;
        changes.Select(c => (
                c["filePath"]!.Value<string>(),
                c["changeCount"]!.Value<int>(),
                string.Join(";", ((JArray)c["changes"]!).Select(l => $"{l["line"]}:{l["column"]}"))))
            .Should().BeEquivalentTo(new[]
            {
                ("Lib/Widget.cs", 1, "4:19"),
                ("App/Service.cs", 2, "6:41;6:64")
            });
    }

    [Fact]
    public async Task ApplyReport_CountsEveryEditedSpan()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "remove", Name = "turns" } },
            preview: false, typeName: "Widget", memberName: "Spin"));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        ((JArray)result["data"]!["changedFiles"]!).Select(f => (f["filePath"]!.Value<string>(), f["changeCount"]!.Value<int>()))
            .Should().BeEquivalentTo(new[] { ("Lib/Widget.cs", 1), ("App/Service.cs", 2) });
    }

    [Fact]
    public async Task FixAll_CountsEveryEditedSpan()
    {
        using var solution = ApplySyncFixture.Create();
        const string unused =
            "namespace App;\n" +
            "\n" +
            "public class Unused\n" +
            "{\n" +
            "    public void M()\n" +
            "    {\n" +
            "        int a = 1;\n" +
            "        System.Console.WriteLine();\n" +
            "        int b = 2;\n" +
            "    }\n" +
            "}\n";
        solution.WriteText("App/Unused.cs", unused);
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.FixAllAsync("CS0219", scope: "file", filePath: "App/Unused.cs"));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        var files = (JArray)result["data"]!["files"]!;
        files.Should().HaveCount(1);
        files[0]["filePath"]!.Value<string>().Should().Be("App/Unused.cs");
        files[0]["changeCount"]!.Value<int>().Should().Be(2, "two separate declarations are removed");
        files[0]["updatedContent"]!.Value<string>().Should().Be(unused
            .Replace("        int a = 1;\n", "")
            .Replace("        int b = 2;\n", ""));
    }

    [Fact]
    public async Task ExtractMethodIntoAnLfFile_WritesOnlyLfLineEndings()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteText("App/Extract.cs",
            "namespace App;\n" +
            "\n" +
            "public class Extract\n" +
            "{\n" +
            "    public int M()\n" +
            "    {\n" +
            "        var x = 1;\n" +
            "        var y = x + 1;\n" +
            "        return y;\n" +
            "    }\n" +
            "}\n");
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.ExtractMethodAsync(
            "App/Extract.cs", startLine: 6, endLine: 7, methodName: "Compute", preview: false));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        var written = solution.ReadText("App/Extract.cs");
        written.Count(c => c == '\r').Should().Be(0, "the file uses LF only, so the inserted method must too");
        written.Should().Contain("Compute()");
    }

    [Fact]
    public async Task AddMissingImportsInACrlfFile_KeepsOnlyCrlfLineEndings()
    {
        using var solution = ApplySyncFixture.Create();
        const string crlfFile =
            "namespace App;\r\n" +
            "\r\n" +
            "public class NeedsImport\r\n" +
            "{\r\n" +
            "    public StringBuilder Make() => new StringBuilder();\r\n" +
            "}\r\n";
        solution.WriteText("App/NeedsImport.cs", crlfFile);
        solution.Restore();
        var service = await LoadAsync(solution);

        var result = JObject.FromObject(await service.AddMissingImportsAsync("App/NeedsImport.cs"));

        result["success"]!.Value<bool>().Should().BeTrue(result.ToString());
        var content = result["data"]!["updatedContent"]!.Value<string>()!;
        content.Should().StartWith("using System.Text;\r\n", "the inserted using ends with the file's CRLF");
        content.Should().EndWith(crlfFile, "the rest of the file is unchanged");
        content.Replace("\r\n", "").Should().NotContain("\n", "no bare LF may be mixed into a CRLF file");
    }
}
