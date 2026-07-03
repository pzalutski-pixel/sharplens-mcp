using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// When removing usings empties the using block entirely, the file header carried by the
// (now removed) first using must move onto the first member instead of being deleted with
// the using. Covers both callers of the header-preserving rebuild: organize_usings (the
// sole using is unused) and remove_unused_code (the sole using is orphaned).
public class PinHeaderTriviaEmptyTests
{
    [Fact]
    public async Task OrganizeUsings_RemovingTheSoleUnusedUsing_KeepsHeaderAtTop()
    {
        const string code = @"// Copyright 2026 Example
using System.Text;

namespace Demo
{
    public class C { public int N() => 1; }
}";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.OrganizeUsingsAsync(document.FilePath!))["data"]!;
        data["removedUsings"]!.Value<int>().Should().Be(1, "System.Text is unused");

        var organized = data["organizedText"]!.Value<string>()!;
        organized.Should().StartWith("// Copyright 2026 Example",
            "the file header must survive at the top when the sole using is removed");
        organized.Should().NotContain("using System.Text;", "the unused using is removed");
    }

    [Fact]
    public async Task RemoveUnused_OrphaningTheSoleUsing_KeepsHeaderAtTop()
    {
        const string code = @"// Copyright 2026 Example
using System.Text;

namespace Demo
{
    public class C
    {
        private StringBuilder _dead = new();
        public int Live() => 1;
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.RemoveUnusedCodeAsync(
            projectName: "TestProject", symbolNames: new List<string> { "_dead" }))["data"]!;
        var file = (data["files"] as JArray)!.Single();

        file["removedUsings"]!.Value<int>().Should().Be(1,
            "removing _dead orphans its only consumer, the sole using System.Text");
        var updated = file["updatedContent"]!.Value<string>()!;
        updated.Should().StartWith("// Copyright 2026 Example",
            "the file header must survive at the top when the sole using is removed");
        updated.Should().NotContain("using System.Text;", "the orphaned using is removed");
    }
}
