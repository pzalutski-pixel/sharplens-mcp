using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Every apply tool surfaces the files it touched under one key, changedFiles, with the
// canonical per-file shape (the apply report via FormatAppliedChanges) — not per-tool
// ad-hoc keys (filesModified) or a missing signal.
public class ApplyChangedFilesShapeTests
{
    private static RoslynService Loaded(string code)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task ChangeSignatureApply_EmitsCanonicalChangedFiles()
    {
        var service = Loaded("namespace Demo { public class C { public int Add(int amount) => amount + 1; } }");

        var data = JObject.FromObject(await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "add", Name = "extra", Type = "int", DefaultValue = "0" } },
            preview: false, typeName: "C", memberName: "Add"))["data"]!;

        data["filesModified"].Should().BeNull("the per-tool filesModified key is replaced by changedFiles");
        var changedFiles = (data["changedFiles"] as JArray)!;
        changedFiles.Should().NotBeEmpty();
        var entry = changedFiles[0];
        entry["filePath"]!.Value<string>().Should().EndWith("C.cs");
        entry["fileName"]!.Value<string>().Should().Be("C.cs");
        entry["changeType"]!.Value<string>().Should().Be("Modified");
        entry["isNewFile"]!.Value<bool>().Should().BeFalse();
        entry["changeCount"]!.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GenerateEqualityApply_EmitsCanonicalChangedFiles()
    {
        var service = Loaded("namespace Demo { public class Box { public int X { get; set; } } }");

        var data = JObject.FromObject(await service.GenerateEqualityMembersAsync(
            filePath: null, line: null, column: null, preview: false, typeName: "Box"))["data"]!;

        var changedFiles = (data["changedFiles"] as JArray)!;
        changedFiles.Should().NotBeEmpty("codegen apply now reports the files it changed, like the other apply tools");
        changedFiles[0]["changeType"]!.Value<string>().Should().Be("Modified");
        changedFiles[0]["fileName"]!.Value<string>().Should().Be("C.cs");
    }
}
