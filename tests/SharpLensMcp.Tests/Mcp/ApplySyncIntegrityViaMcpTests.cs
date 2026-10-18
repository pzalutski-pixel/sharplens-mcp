using System.Text;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// sync_documents and the shared apply path through the JSON-RPC dispatcher, on a fresh
// McpServer per test loaded with an on-disk temp solution (the shared fixture's solution
// is the repository itself, which these tests must not write to). Field names are the
// wire's camelCase.
public class ApplySyncIntegrityViaMcpTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static async Task<McpHarnessOps> LoadAsync(DiskSolution solution)
    {
        var ops = new McpHarnessOps(new McpServer(TimeSpan.FromMinutes(5)));
        var load = await ops.CallToolAsync("load_solution", new { solutionPath = solution.SolutionPath });
        load["success"]!.Value<bool>().Should().BeTrue(load.ToString());
        return ops;
    }

    private static async Task<JToken> DataAsync(McpHarnessOps ops, string tool, object arguments)
    {
        var inner = await ops.CallToolAsync(tool, arguments);
        inner["success"]!.Value<bool>().Should().BeTrue($"{tool} must succeed; got: {inner}");
        return inner["data"]!;
    }

    private static string[] Strings(JToken token) => ((JArray)token).Select(t => t.Value<string>()!).ToArray();

    [Fact]
    public async Task SyncDocuments_EmptyOrBlankPathList_IsInvalidParameter()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var ops = await LoadAsync(solution);

        var empty = await ops.CallToolAsync("sync_documents", new { filePaths = Array.Empty<string>() });
        empty["success"]!.Value<bool>().Should().BeFalse();
        empty["error"]!["code"]!.Value<string>().Should().Be("INVALID_PARAMETER");

        var blank = await ops.CallToolAsync("sync_documents", new { filePaths = new[] { "" } });
        blank["success"]!.Value<bool>().Should().BeFalse();
        blank["error"]!["code"]!.Value<string>().Should().Be("INVALID_PARAMETER");
    }

    [Fact]
    public async Task SyncDocuments_UnchangedFileIsUnchanged_AndAnUnknownPathIsAnError()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var ops = await LoadAsync(solution);

        var data = await DataAsync(ops, "sync_documents", new { filePaths = new[] { "App/Other.cs", "App/Typo.cs" } });

        data["updated"]!.Value<int>().Should().Be(0);
        data["unchanged"]!.Value<int>().Should().Be(1);
        data["totalSynced"]!.Value<int>().Should().Be(0);
        ((JArray)data["errors"]!).Select(e => (e["path"]!.Value<string>(), e["error"]!.Value<string>()))
            .Should().Equal(("App/Typo.cs", "File not found on disk and not in the solution"));
    }

    [Fact]
    public async Task SyncDocuments_FullSyncAddsCompileItems_AndRejectsOtherFiles()
    {
        using var solution = ApplySyncFixture.Create(
            extraAppXml: "\n  <ItemGroup>\n    <Compile Remove=\"Excluded.cs\" />\n  </ItemGroup>\n");
        solution.Restore();
        var ops = await LoadAsync(solution);
        solution.WriteText("App/NewOne.cs", "namespace App;\n\npublic class NewOne\n{\n}\n");
        solution.WriteText("App/README.md", "# Readme\n");
        solution.WriteText("App/obj/Stray.cs", "namespace App; public class Stray { }\n");
        solution.WriteText("App/Excluded.cs", "namespace App; public class Excluded { }\n");

        var full = await DataAsync(ops, "sync_documents", new { });
        Strings(full["addedFiles"]!).Should().Equal("App/NewOne.cs");
        full["updated"]!.Value<int>().Should().Be(0);

        var explicitSync = await DataAsync(ops, "sync_documents",
            new { filePaths = new[] { "App/README.md", "App/obj/Stray.cs", "App/Excluded.cs" } });
        explicitSync["added"]!.Value<int>().Should().Be(0);
        ((JArray)explicitSync["errors"]!).Select(e => e["error"]!.Value<string>()).Should().Equal(
            "Not a C# source file (.cs)",
            "No project in the solution includes this file in its compile items",
            "No project in the solution includes this file in its compile items");

        var found = await DataAsync(ops, "search_symbols", new { query = "NewOne" });
        ((JArray)found["results"]!).Select(r => r["fullyQualifiedName"]!.Value<string>()).Should().Equal("App.NewOne");
    }

    [Fact]
    public async Task SyncDocuments_NewFileInASiblingDirectorySharingAPrefix_IsNotAssignedToThePrefixProject()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var ops = await LoadAsync(solution);
        solution.WriteText("AppExtra/Stray.cs", "namespace Stray;\n\npublic class Stray\n{\n}\n");

        var data = await DataAsync(ops, "sync_documents", new { filePaths = new[] { "AppExtra/Stray.cs" } });

        data["added"]!.Value<int>().Should().Be(0);
        ((JArray)data["errors"]!).Select(e => (e["path"]!.Value<string>(), e["error"]!.Value<string>()))
            .Should().Equal(("AppExtra/Stray.cs", "No project in the solution includes this file in its compile items"));
        var found = await DataAsync(ops, "search_symbols", new { query = "Stray" });
        ((JArray)found["results"]!).Should().BeEmpty("the file was not added to App");
    }

    [Fact]
    public async Task SyncDocuments_NewFileInASiblingProjectSharingAPrefix_GoesToItsOwnProject()
    {
        using var solution = new DiskSolution("App/App.csproj", "AppExtra/AppExtra.csproj");
        solution.WriteText("App/App.csproj", DiskSolution.SdkProject("net8.0"));
        solution.WriteText("AppExtra/AppExtra.csproj", DiskSolution.SdkProject("net8.0"));
        solution.WriteText("App/Other.cs", ApplySyncFixture.Other);
        solution.WriteText("AppExtra/Seed.cs", "namespace AppExtra;\n\npublic class Seed\n{\n}\n");
        solution.Restore();
        var ops = await LoadAsync(solution);
        solution.WriteText("AppExtra/Stray.cs", "namespace AppExtra;\n\npublic class Stray\n{\n}\n");

        var data = await DataAsync(ops, "sync_documents", new { filePaths = new[] { "AppExtra/Stray.cs" } });
        Strings(data["addedFiles"]!).Should().Equal("AppExtra/Stray.cs");

        var structure = await DataAsync(ops, "get_project_structure", new { includeDocuments = true, includeReferences = false });
        var documentsByProject = ((JArray)structure["projects"]!).ToDictionary(
            p => p["name"]!.Value<string>()!,
            p => ((JArray)p["documents"]!).Select(d => d["name"]!.Value<string>()!).ToList());
        documentsByProject["AppExtra"].Should().Contain("Stray.cs");
        documentsByProject["App"].Should().NotContain("Stray.cs");
    }

    [Fact]
    public async Task SyncThenRename_InAWindows1252File_KeepsItsEncoding()
    {
        using var solution = ApplySyncFixture.Create();
        var bytes = ApplySyncFixture.Windows1252.GetBytes(ApplySyncFixture.Latin);
        solution.WriteBytes("App/Latin.cs", bytes);
        solution.Restore();
        var ops = await LoadAsync(solution);

        var sync = await DataAsync(ops, "sync_documents", new { });
        sync["updated"]!.Value<int>().Should().Be(0, "no file changed on disk since the load");

        var rename = await ops.CallToolAsync("rename_symbol",
            new { typeName = "Latin", memberName = "Greet", newName = "Hello", preview = false });

        if (ApplySyncFixture.FallbackDecodesWindows1252)
        {
            rename["success"]!.Value<bool>().Should().BeTrue(rename.ToString());
            solution.ReadBytes("App/Latin.cs").Should().Equal(
                ApplySyncFixture.Windows1252.GetBytes(ApplySyncFixture.Latin.Replace("Greet", "Hello")));
        }
        else
        {
            // UTF-8 fallback: the file does not round-trip, so the apply is refused untouched.
            rename["success"]!.Value<bool>().Should().BeFalse(rename.ToString());
            rename["error"]!["code"]!.Value<string>().Should().Be(ErrorCodes.UnsupportedEncoding);
            solution.ReadBytes("App/Latin.cs").Should().Equal(bytes);
        }
    }

    [Fact]
    public async Task RenameAfterSync_LeavesAnUnsyncedDiskEditInAnotherFileAlone()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var ops = await LoadAsync(solution);
        solution.WriteText("App/Other.cs", ApplySyncFixture.Other + "// edit1\n");
        await DataAsync(ops, "sync_documents", new { filePaths = new[] { "App/Other.cs" } });
        solution.WriteText("App/Other.cs", ApplySyncFixture.Other + "// edit1\n// edit2\n");

        var data = await DataAsync(ops, "rename_symbol",
            new { typeName = "Widget", memberName = "Spin", newName = "Spin2", preview = false });

        solution.ReadBytes("App/Other.cs").Should().Equal(Utf8NoBom.GetBytes(ApplySyncFixture.Other + "// edit1\n// edit2\n"));
        ((JArray)data["changes"]!).Select(c => c["filePath"]!.Value<string>())
            .Should().BeEquivalentTo(new[] { "Lib/Widget.cs", "App/Service.cs" });
    }

    [Fact]
    public async Task Rename_OverAFileEditedOnDisk_IsRefusedAsStale()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var ops = await LoadAsync(solution);
        // The preview reads Service.cs, so the loaded text predates the disk edit below.
        await DataAsync(ops, "rename_symbol", new { typeName = "Widget", memberName = "Spin", newName = "Spin2", preview = true });
        var userEdit = ApplySyncFixture.Service + "// user work\n";
        solution.WriteText("App/Service.cs", userEdit);

        var inner = await ops.CallToolAsync("rename_symbol",
            new { typeName = "Widget", memberName = "Spin", newName = "Spin2", preview = false });

        inner["success"]!.Value<bool>().Should().BeFalse();
        inner["error"]!["code"]!.Value<string>().Should().Be("STALE_DOCUMENT");
        inner["error"]!["hint"]!.Value<string>().Should().Be("run sync_documents, then retry");
        Strings(inner["error"]!["context"]!["staleFiles"]!).Should().Equal("App/Service.cs");
        solution.ReadText("App/Service.cs").Should().Be(userEdit);
        solution.ReadText("Lib/Widget.cs").Should().Be(ApplySyncFixture.Widget);
    }

    [Fact]
    public async Task OrganizeUsingsApply_KeepsANoBomFileWithoutABom()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteText("App/Usings.cs",
            "using System.Text;\nusing System;\n\nnamespace App;\n\npublic class Usings\n{\n" +
            "    public StringBuilder Make() => new StringBuilder(Environment.NewLine);\n}\n");
        solution.Restore();
        var ops = await LoadAsync(solution);

        await DataAsync(ops, "organize_usings", new { filePath = "App/Usings.cs", preview = false });

        solution.ReadBytes("App/Usings.cs").Should().Equal(Utf8NoBom.GetBytes(
            "using System;\nusing System.Text;\n\nnamespace App;\n\npublic class Usings\n{\n" +
            "    public StringBuilder Make() => new StringBuilder(Environment.NewLine);\n}\n"));
    }

    [Fact]
    public async Task SyncedNewFileThenApply_LeavesTheProjectFileUntouched()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var ops = await LoadAsync(solution);
        var projectBefore = solution.ReadBytes("App/App.csproj");
        solution.WriteText("App/NewOne.cs", "namespace App;\n\npublic class NewOne\n{\n}\n");
        await DataAsync(ops, "sync_documents", new { filePaths = new[] { "App/NewOne.cs" } });

        await DataAsync(ops, "rename_symbol", new { typeName = "Widget", memberName = "Spin", newName = "Spin2", preview = false });

        solution.ReadBytes("App/App.csproj").Should().Equal(projectBefore);
    }

    [Fact]
    public async Task MoveTypeCodeAction_WritesTheNewFile_AndLeavesTheProjectFileUntouched()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteText("App/Pair.cs", "namespace App;\n\npublic class One\n{\n}\n\npublic class Two\n{\n}\n");
        solution.Restore();
        var ops = await LoadAsync(solution);
        var projectBefore = solution.ReadBytes("App/App.csproj");

        var data = await DataAsync(ops, "apply_code_action_by_title",
            new { filePath = "App/Pair.cs", line = 6, column = 13, title = "Move type to Two.cs", preview = false });

        solution.ReadBytes("App/App.csproj").Should().Equal(projectBefore);
        solution.ReadBytes("App/Two.cs").Should().Equal(Utf8NoBom.GetBytes("namespace App;\n\npublic class Two\n{\n}\n"));
        ((JArray)data["changedFiles"]!).Select(f => (f["filePath"]!.Value<string>(), f["changeType"]!.Value<string>()))
            .Should().BeEquivalentTo(new[] { ("App/Two.cs", "Added"), ("App/Pair.cs", "Modified") });
    }

    [Fact]
    public async Task ChangeSignature_InAMultiTargetedProject_ReportsTheFrameworkItBreaks()
    {
        using var solution = new DiskSolution("Multi/Multi.csproj");
        solution.WriteText("Multi/Multi.csproj", DiskSolution.SdkProject("net8.0;netstandard2.0"));
        solution.WriteText("Multi/Guard.cs",
            "namespace Multi;\n\npublic class Guard\n{\n    public void Take(object value)\n    {\n    }\n}\n");
        solution.Restore();
        var ops = await LoadAsync(solution);

        // System.HashCode exists on net8.0 and not on netstandard2.0.
        var data = await DataAsync(ops, "change_signature", new
        {
            typeName = "Guard",
            memberName = "Take",
            changes = new[] { new { action = "add", name = "hash", type = "System.HashCode", defaultValue = "default" } },
            preview = false
        });

        Strings(data["postApplyErrors"]!).Should().Equal(
            "Multi/Guard.cs (line 4) CS0234: The type or namespace name 'HashCode' does not exist in the namespace 'System' (are you missing an assembly reference?)");
    }

    [Fact]
    public async Task ChangeSignature_NewErrorIdenticalToAnExistingOne_IsReported()
    {
        using var solution = new DiskSolution("Old/Old.csproj");
        solution.WriteText("Old/Old.csproj", DiskSolution.SdkProject("netstandard2.0"));
        solution.WriteText("Old/Guard.cs",
            "namespace Old;\n\npublic class Guard\n{\n" +
            "    public void Existing(System.HashCode first)\n    {\n    }\n\n" +
            "    public void Take(object value)\n    {\n    }\n}\n");
        solution.Restore();
        var ops = await LoadAsync(solution);

        var data = await DataAsync(ops, "change_signature", new
        {
            typeName = "Guard",
            memberName = "Take",
            changes = new[] { new { action = "add", name = "hash", type = "System.HashCode", defaultValue = "default" } },
            preview = false
        });

        Strings(data["postApplyErrors"]!).Should().Equal(
            "Old/Guard.cs (line 8) CS0234: The type or namespace name 'HashCode' does not exist in the namespace 'System' (are you missing an assembly reference?)");
    }

    [Fact]
    public async Task ChangeSignature_BreakingADependentProject_IsReported()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteText("App/Delegates.cs",
            "using System;\nusing Lib;\n\nnamespace App;\n\npublic class Delegates\n{\n" +
            "    public Func<int, int> Make() => new Widget().Spin;\n}\n");
        solution.Restore();
        var ops = await LoadAsync(solution);

        var data = await DataAsync(ops, "change_signature", new
        {
            typeName = "Widget",
            memberName = "Spin",
            changes = new[] { new { action = "add", name = "extra", type = "int", defaultValue = "0" } },
            preview = false
        });

        Strings(data["postApplyErrors"]!).Should().Equal(
            "App/Delegates.cs (line 7) CS0123: No overload for 'Spin' matches delegate 'Func<int, int>'");
    }

    [Fact]
    public async Task RenamePreview_ReportsEveryEditedSpan()
    {
        using var solution = ApplySyncFixture.Create();
        solution.Restore();
        var ops = await LoadAsync(solution);

        var data = await DataAsync(ops, "rename_symbol",
            new { typeName = "Widget", memberName = "Spin", newName = "Spin2", preview = true, verbosity = "compact" });

        ((JArray)data["changes"]!).Select(c => (
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
    public async Task ExtractMethod_IntoAnLfFile_WritesOnlyLf()
    {
        using var solution = ApplySyncFixture.Create();
        solution.WriteText("App/Extract.cs",
            "namespace App;\n\npublic class Extract\n{\n    public int M()\n    {\n" +
            "        var x = 1;\n        var y = x + 1;\n        return y;\n    }\n}\n");
        solution.Restore();
        var ops = await LoadAsync(solution);

        await DataAsync(ops, "extract_method",
            new { filePath = "App/Extract.cs", startLine = 6, endLine = 7, methodName = "Compute", preview = false });

        var written = solution.ReadText("App/Extract.cs");
        written.Count(c => c == '\r').Should().Be(0);
        written.Should().Contain("Compute()");
    }
}
