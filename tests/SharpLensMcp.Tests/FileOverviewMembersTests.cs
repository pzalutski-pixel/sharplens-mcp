using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_file_overview's per-type member outline: in-memory partial class split
// across two files plus a nested type, so the "this file only" and
// nested-type-appears-twice behaviors are pinned, along with the
// maxMembersPerType cap.
public class FileOverviewMembersTests
{
    private const string FileOne = """
        namespace OutlineProbe;

        public partial class Host
        {
            public int FieldA;
            public string PropA { get; set; } = "";
            public void MethodA() { }

            public class Nested
            {
                public int NestedField;
            }
        }
        """;

    private const string FileTwo = """
        namespace OutlineProbe;

        public partial class Host
        {
            public void MethodB() { }
        }
        """;

    private static (RoslynService service, string fileOne, string fileTwo) CreateService()
    {
        var (workspace, documents) = TestHelpers.CreateWorkspaceWithMultipleDocuments(
            (FileOne, "HostOne.cs"), (FileTwo, "HostTwo.cs"));
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return (service, documents[0].FilePath!, documents[1].FilePath!);
    }

    private static JToken TypeEntry(JToken data, string name) =>
        (data["typeDeclarations"] as JArray)!.First(t => t["name"]!.Value<string>() == name);

    [Fact]
    public async Task PartialInFileOne_ListsOnlyItsOwnMembers_PlusNestedType()
    {
        var (service, fileOne, _) = CreateService();

        var data = JObject.FromObject(await service.GetFileOverviewAsync(fileOne))["data"]!;

        var host = TypeEntry(data, "Host");
        var memberNames = (host["members"] as JArray)!.Select(m => m["name"]!.Value<string>()).ToList();
        memberNames.Should().BeEquivalentTo(new[] { "FieldA", "PropA", "MethodA", "Nested" },
            "Host's members in this file only — MethodB lives in the other file");
        memberNames.Should().NotContain("MethodB");

        // Each member carries the outline fields.
        var method = (host["members"] as JArray)!.First(m => m["name"]!.Value<string>() == "MethodA");
        method["kind"]!.Value<string>().Should().Be("Method");
        method["accessibility"]!.Value<string>().Should().Be("Public");
        method["signature"]!.Value<string>().Should().Contain("MethodA");
        // Pin startLine to MethodA's actual 0-based line in the fixture string (resolved
        // from a marker) rather than a `> 0` tautology.
        var fileOneLines = FileOne.Replace("\r\n", "\n").Split('\n');
        var methodALine = Array.FindIndex(fileOneLines, l => l.Contains("public void MethodA()"));
        method["startLine"]!.Value<int>().Should().Be(methodALine,
            "the member outline must report MethodA's 0-based start line");

        (host["members"] as JArray)!.First(m => m["name"]!.Value<string>() == "FieldA")
            ["kind"]!.Value<string>().Should().Be("Field");

        // The nested type appears as a member AND as its own top-level entry.
        var nested = TypeEntry(data, "Nested");
        (nested["members"] as JArray)!.Select(m => m["name"]!.Value<string>())
            .Should().ContainSingle().Which.Should().Be("NestedField");
    }

    [Fact]
    public async Task PartialInFileTwo_ListsOnlyItsOwnMember()
    {
        var (service, _, fileTwo) = CreateService();

        var data = JObject.FromObject(await service.GetFileOverviewAsync(fileTwo))["data"]!;

        var host = TypeEntry(data, "Host");
        (host["members"] as JArray)!.Select(m => m["name"]!.Value<string>())
            .Should().BeEquivalentTo(new[] { "MethodB" },
                "the file-two partial declares only MethodB");
    }

    [Fact]
    public async Task MaxMembersPerType_CapsAndFlagsTruncation()
    {
        var (service, fileOne, _) = CreateService();

        var data = JObject.FromObject(await service.GetFileOverviewAsync(fileOne, maxMembersPerType: 1))["data"]!;

        var host = TypeEntry(data, "Host");
        (host["members"] as JArray)!.Count.Should().Be(1);
        host["membersTruncated"]!.Value<bool>().Should().BeTrue();
        host["memberCount"]!.Value<int>().Should().Be(4, "memberCount still reports the true total");
    }

    [Fact]
    public async Task MaxMembersPerTypeOutOfRange_ReturnsInvalidParameter()
    {
        var (service, fileOne, _) = CreateService();

        var result = await service.GetFileOverviewAsync(fileOne, maxMembersPerType: 0);

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }
}
