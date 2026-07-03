using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks the organize_usings contract: unused directives (compiler CS8019) are
// actually removed — the tool's description always promised this and the old
// implementation only sorted — and the rest are grouped global → plain (System
// root first) → static → alias. Also locks the exact-root System check: a
// namespace merely STARTING with "System" (SystemFoo) sorts as a plain using.
public class OrganizeUsingsTests
{
    private const string MessyUsingsCode = @"using SystemFoo.Helpers;
using System.Text;
using static System.Math;
using Alias = System.Console;
using System;

namespace SystemFoo.Helpers
{
    public static class Helper
    {
        public static void Help() { }
    }
}

namespace Production
{
    public class Orderly
    {
        public void Run()
        {
            Helper.Help();
            var max = Max(1, 2);
            Alias.WriteLine(max);
            var g = Guid.NewGuid();
        }
    }
}";

    private static (RoslynService service, string filePath) CreateService()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(MessyUsingsCode, "Messy.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, document.FilePath!);
    }

    [Fact]
    public async Task Preview_RemovesUnusedUsing_AndOrdersGroups()
    {
        var (service, filePath) = CreateService();

        var result = await service.OrganizeUsingsAsync(filePath);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var data = json["data"]!;
        data["preview"]!.Value<bool>().Should().BeTrue("preview is the default mode");
        data["applied"]!.Value<bool>().Should().BeFalse();
        data["totalUsings"]!.Value<int>().Should().Be(5);
        data["removedUsings"]!.Value<int>().Should().Be(1,
            "System.Text is never used — the compiler flags it CS8019 and the tool must drop it");
        data["changed"]!.Value<bool>().Should().BeTrue();

        var organized = data["organizedText"]!.Value<string>()!;
        organized.Should().NotContain("using System.Text;",
            "the unused directive must be removed, not just re-sorted");

        // Group order: plain (System root first, SystemFoo is NOT a System root) →
        // static → alias.
        var systemIdx = organized.IndexOf("using System;", StringComparison.Ordinal);
        var systemFooIdx = organized.IndexOf("using SystemFoo.Helpers;", StringComparison.Ordinal);
        var staticIdx = organized.IndexOf("using static System.Math;", StringComparison.Ordinal);
        var aliasIdx = organized.IndexOf("using Alias = System.Console;", StringComparison.Ordinal);
        systemIdx.Should().BeGreaterOrEqualTo(0);
        systemFooIdx.Should().BeGreaterThan(systemIdx,
            "SystemFoo starts with 'System' but is not a System root namespace — it sorts after the real System usings");
        staticIdx.Should().BeGreaterThan(systemFooIdx, "static usings group after plain usings");
        aliasIdx.Should().BeGreaterThan(staticIdx, "alias usings group last");
    }

    [Fact]
    public async Task Apply_PersistsOrganizedUsings_AndSecondRunReportsNoChange()
    {
        var (service, filePath) = CreateService();

        var applyResult = await service.OrganizeUsingsAsync(filePath, preview: false);
        var applyJson = JObject.FromObject(applyResult);
        applyJson["success"].Should().NotBeNull();
        applyJson["success"]!.Value<bool>().Should().BeTrue(applyJson.ToString());
        applyJson["data"]!["applied"]!.Value<bool>().Should().BeTrue();

        // The unified apply path must leave the in-memory solution updated:
        // a second run sees the organized file and reports nothing to change.
        var secondResult = await service.OrganizeUsingsAsync(filePath);
        var secondJson = JObject.FromObject(secondResult);
        secondJson["success"]!.Value<bool>().Should().BeTrue(secondJson.ToString());
        secondJson["data"]!["changed"]!.Value<bool>().Should().BeFalse(
            "applying then re-organizing must be idempotent");
        secondJson["data"]!["removedUsings"]!.Value<int>().Should().Be(0,
            "the unused using is already gone after the first apply");
    }

    [Fact]
    public async Task Batch_ReportsRemovedUsingsPerFile()
    {
        var (service, _) = CreateService();

        var result = await service.OrganizeUsingsBatchAsync(projectName: "TestProject", filePattern: null, preview: true);
        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var files = json["data"]!["files"] as JArray;
        files.Should().NotBeNullOrEmpty("the messy fixture must be reported as changed");
        files![0]!["removedUsings"].Should().NotBeNull("batch entries must carry the per-file removal count");
        files[0]!["removedUsings"]!.Value<int>().Should().Be(1);
    }
}
