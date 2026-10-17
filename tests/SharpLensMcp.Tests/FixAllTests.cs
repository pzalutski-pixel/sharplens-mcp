using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// fix_all over an in-memory workspace with CS0219 (unused local) spread across
// two files. Compute-only: every test reads back files[].updatedContent and
// asserts the dead variables are gone from the COMPUTED text while the live
// solution stays untouched (the agent does the writing).
public class FixAllTests
{
    private const string FileA = """
        namespace FixAllProbe;

        public class HolderA
        {
            public int One()
            {
                int unusedA1 = 5;
                return 1;
            }

            public int Two()
            {
                int unusedA2 = 7;
                return 2;
            }
        }
        """;

    private const string FileB = """
        namespace FixAllProbe;

        public class HolderB
        {
            public int Three()
            {
                int unusedB1 = 9;
                return 3;
            }
        }
        """;

    private static (RoslynService service, AdhocWorkspaceHandle handle) CreateService(params (string, string)[] files)
    {
        var (workspace, documents) = TestHelpers.CreateWorkspaceWithMultipleDocuments(files);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, new AdhocWorkspaceHandle(documents));
    }

    private sealed record AdhocWorkspaceHandle(Microsoft.CodeAnalysis.Document[] Documents);

    [Fact]
    public async Task SolutionScope_ComputesRemovalForEveryInstanceAcrossFiles()
    {
        var (service, _) = CreateService((FileA, "HolderA.cs"), (FileB, "HolderB.cs"));

        var result = await service.FixAllAsync("CS0219", scope: "solution");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse();
        data["diagnosticsMatched"]!.Value<int>().Should().Be(3, "three unused locals across the two files");

        var files = (data["files"] as JArray)!;
        files.Count.Should().Be(2, "both files carry fixable instances");
        foreach (var file in files)
        {
            var content = file["updatedContent"]!.Value<string>()!;
            content.Should().NotContain("unused", "every unused local must be gone from the computed text");
            content.Should().Contain("return", "the surrounding method bodies must survive");
        }
    }

    [Fact]
    public async Task ResultContent_HasNoRemainingDiagnostic_WhenReloaded()
    {
        var (service, _) = CreateService((FileA, "HolderA.cs"), (FileB, "HolderB.cs"));
        var data = JObject.FromObject(await service.FixAllAsync("CS0219", scope: "solution"))["data"]!;

        // Play the agent: rebuild a workspace from the computed contents and
        // confirm fix_all now finds nothing to do — the fix actually resolved it.
        var rewritten = (data["files"] as JArray)!
            .ToDictionary(
                f => Path.GetFileName(f["filePath"]!.Value<string>()!),
                f => f["updatedContent"]!.Value<string>()!);
        var (verifyService, _) = CreateService(
            (rewritten.GetValueOrDefault("HolderA.cs", FileA), "HolderA.cs"),
            (rewritten.GetValueOrDefault("HolderB.cs", FileB), "HolderB.cs"));

        var second = JObject.FromObject(await verifyService.FixAllAsync("CS0219", scope: "solution"))["data"]!;
        second["diagnosticsMatched"]!.Value<int>().Should().Be(0,
            "the computed removals must clear every CS0219");
        (second["files"] as JArray)!.Should().BeEmpty();
    }

    [Fact]
    public async Task FileScope_OnlyFixesTheNamedFile()
    {
        var (service, handle) = CreateService((FileA, "HolderA.cs"), (FileB, "HolderB.cs"));
        var fileBPath = handle.Documents.First(d => d.Name == "HolderB.cs").FilePath!;

        var data = JObject.FromObject(
            await service.FixAllAsync("CS0219", scope: "file", filePath: fileBPath))["data"]!;

        data["diagnosticsMatched"]!.Value<int>().Should().Be(1, "only HolderB's single instance is in scope");
        var files = (data["files"] as JArray)!;
        files.Should().ContainSingle();
        files[0]["filePath"]!.Value<string>().Should().EndWith("HolderB.cs");
    }

    [Fact]
    public async Task NothingToFix_ReturnsEmptySuccess_WithTheFullEnvelope()
    {
        var (service, _) = CreateService(
            ("namespace Clean; public class C { public int M() { return 0; } }", "Clean.cs"));

        var json = JObject.FromObject(await service.FixAllAsync("CS0219", scope: "solution"));
        var data = json["data"]!;

        data["diagnosticsMatched"]!.Value<int>().Should().Be(0);
        (data["files"] as JArray)!.Should().BeEmpty();
        // The empty result carries the same fields as the populated success
        // envelope, instead of a divergent shape missing fixTitle/fallbackMode.
        data["fixTitle"]!.Type.Should().Be(JTokenType.Null);
        data["fallbackMode"]!.Value<bool>().Should().BeFalse();
        data["analyzerIdOutOfScope"]!.Value<bool>().Should().BeFalse(
            "CS0219 is a compiler id — a genuine clean result, not an out-of-scope id");
        json["meta"]!["TotalCount"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task AnalyzerSourcedId_ReportsOutOfScope_NotMisleadingNothingToFix()
    {
        var (service, _) = CreateService((FileA, "HolderA.cs"));

        var data = JObject.FromObject(await service.FixAllAsync("IDE0005", scope: "solution"))["data"]!;

        data["analyzerIdOutOfScope"]!.Value<bool>().Should().BeTrue(
            "IDE0005 is analyzer-sourced; fix_all reads compiler diagnostics only, so it is out of scope, " +
            "not necessarily absent");
        data["diagnosticsMatched"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task UnknownScope_ReturnsInvalidParameter()
    {
        var (service, _) = CreateService((FileA, "HolderA.cs"));

        var result = await service.FixAllAsync("CS0219", scope: "galaxy");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task FileScopeWithoutFilePath_ReturnsInvalidParameter()
    {
        var (service, _) = CreateService((FileA, "HolderA.cs"));

        var result = await service.FixAllAsync("CS0219", scope: "file");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Contain("filePath");
    }

    [Fact]
    public async Task UnknownProject_ReturnsInvalidParameterListingProjects()
    {
        var (service, _) = CreateService((FileA, "HolderA.cs"));

        var result = await service.FixAllAsync("CS0219", scope: "project", projectName: "NoSuchProject");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter,
            "an unknown project name is an invalid argument, the canonical project-resolution error");
        json["error"]!["Hint"]!.Value<string>().Should().Contain("TestProject",
            "the error must list the available project names");
    }

    [Fact]
    public async Task NonExistentDiagnosticId_MatchesNothing_AndReturnsEmptySuccess()
    {
        var (service, _) = CreateService((FileA, "HolderA.cs"));

        // A diagnostic id that never occurs short-circuits at the match stage
        // (before any provider lookup) with the empty-success shape.
        var data = JObject.FromObject(await service.FixAllAsync("CS9999", scope: "solution"))["data"]!;
        data["diagnosticsMatched"]!.Value<int>().Should().Be(0);
        (data["files"] as JArray)!.Should().BeEmpty();
    }
}
