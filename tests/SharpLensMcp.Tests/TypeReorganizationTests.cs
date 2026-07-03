using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// move_type_to_file / split_type are COMPUTE-ONLY by product rule: new tools
// never write unless the user explicitly validates a write path. Every test
// here therefore locks two things at once — the computed contents are right,
// and the disk stays untouched.
public class TypeReorganizationTests : RoslynServiceTestBase
{
    private string FixturesDir => Path.Combine(
        Path.GetDirectoryName(SolutionPath)!, "tests", "SharpLensMcp.Tests", "Fixtures");

    private string FixturePath => Path.Combine(FixturesDir, "TypeReorganizationFixture.cs");

    [Fact]
    public async Task MoveTypeToFile_ComputesBothFileContents_WithoutTouchingDisk()
    {
        var fixtureBefore = File.ReadAllText(FixturePath);

        var result = await Service.MoveTypeToFileAsync("MoveCandidateBeta");

        var data = GetData(result);
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse(
            "the tool is generation-only and must say so");
        data["typeName"]!.Value<string>().Should().Be("SharpLensMcp.Tests.Fixtures.MoveCandidateBeta");
        data["newFilePath"]!.Value<string>().Should().EndWith("MoveCandidateBeta.cs");

        var newFileContent = data["newFileContent"]!.Value<string>()!;
        newFileContent.Should().Contain("class MoveCandidateBeta");
        newFileContent.Should().Contain("namespace SharpLensMcp.Tests.Fixtures;",
            "the origin's file-scoped namespace must be reproduced");
        newFileContent.Should().NotContain("MoveCandidateAlpha");
        newFileContent.Should().NotContain("SplitCandidate");

        var updatedOrigin = data["updatedOriginContent"]!.Value<string>()!;
        updatedOrigin.Should().NotContain("class MoveCandidateBeta");
        updatedOrigin.Should().Contain("class MoveCandidateAlpha");
        updatedOrigin.Should().Contain("class SplitCandidate");

        File.Exists(Path.Combine(FixturesDir, "MoveCandidateBeta.cs")).Should().BeFalse(
            "a generation-only tool must never create the file");
        File.ReadAllText(FixturePath).Should().Be(fixtureBefore,
            "a generation-only tool must never modify the origin file");
    }

    [Fact]
    public async Task MoveTypeToFile_BothComputedContents_Compile()
    {
        var data = GetData(await Service.MoveTypeToFileAsync("MoveCandidateBeta"));

        foreach (var field in new[] { "newFileContent", "updatedOriginContent" })
        {
            var validation = await Service.ValidateCodeAsync(
                data[field]!.Value<string>()!, standalone: true);
            var validationData = GetData(validation);
            validationData["compiles"]!.Value<bool>().Should().BeTrue(
                $"{field} must be compilable C# as returned");
        }
    }

    [Fact]
    public async Task MoveTypeToFile_RemovesEmptiedBlockNamespace_AndKeepsNeighborDocComment()
    {
        const string code = @"
namespace Keepers
{
    /// <summary>Documentation for Keep.</summary>
    public class Keep { }
}

namespace Movers
{
    /// <summary>Documentation for Moved.</summary>
    public class Moved { }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "TwoNamespaces.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = GetData(await service.MoveTypeToFileAsync("Moved"));

        var updatedOrigin = data["updatedOriginContent"]!.Value<string>()!;
        updatedOrigin.Should().NotContain("namespace Movers",
            "the namespace that lost its only type must be removed, not left empty");
        updatedOrigin.Should().Contain("class Keep");
        updatedOrigin.Should().Contain("Documentation for Keep.",
            "the surviving neighbor's doc comment must not be dropped");

        var newFileContent = data["newFileContent"]!.Value<string>()!;
        newFileContent.Should().Contain("namespace Movers");
        newFileContent.Should().Contain("Documentation for Moved.",
            "the moved type carries its own doc comment to the new file");
        newFileContent.Should().NotContain("namespace Keepers",
            "the new file must not carry an empty Keepers namespace");
    }

    [Fact]
    public async Task MoveTypeToFile_KeepsDocCommentOfTypeFollowingTheMovedOne()
    {
        // The moved type is immediately followed, in the same namespace, by a
        // doc-commented type — that doc must survive the removal.
        const string code = @"
namespace N
{
    public class Moved { }

    /// <summary>Doc for After.</summary>
    public class After { }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Adjacent.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = GetData(await service.MoveTypeToFileAsync("Moved"));
        var updatedOrigin = data["updatedOriginContent"]!.Value<string>()!;

        updatedOrigin.Should().Contain("class After");
        updatedOrigin.Should().Contain("Doc for After.",
            "removing the preceding type must not strip the following type's doc comment");
    }

    [Fact]
    public async Task MoveTypeToFile_TypeAlreadyAlone_IsANoOp()
    {
        var result = await Service.MoveTypeToFileAsync("AddNullChecksTarget");

        var data = GetData(result);
        data["alreadyAlone"]!.Value<bool>().Should().BeTrue(
            "AddNullChecksTarget is the only type in AddNullChecksFixture.cs");
        data["message"]!.Value<string>().Should().Contain("already the only type");
    }

    [Fact]
    public async Task MoveTypeToFile_OriginAlreadyBearsTheTypeName_ReturnsInvalidParameter()
    {
        var result = await Service.MoveTypeToFileAsync("ExactTypeNameHost");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Hint"]!.Value<string>().Should().Contain("move the OTHER types");
    }

    [Fact]
    public async Task MoveTypeToFile_NestedType_ReturnsInvalidParameter()
    {
        var result = await Service.MoveTypeToFileAsync("NestedHelper");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("nested type");
    }

    [Fact]
    public async Task SplitType_ComputesPartialHalves_WithoutTouchingDisk()
    {
        var fixtureBefore = File.ReadAllText(FixturePath);

        var result = await Service.SplitTypeAsync(
            "SplitCandidate", new List<string> { "MoveMe1", "MoveMe2" });

        var data = GetData(result);
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse();
        data["newFilePath"]!.Value<string>().Should().EndWith("SplitCandidate.Partial.cs",
            "default newFileName is {TypeName}.Partial.cs");
        data["partialModifierAdded"]!.Value<bool>().Should().BeTrue(
            "SplitCandidate is not declared partial in the fixture");

        var moved = (data["movedMembers"] as JArray)!;
        moved.Select(m => m["name"]!.Value<string>()).Should().BeEquivalentTo("MoveMe1", "MoveMe2");
        moved.Select(m => m["kind"]!.Value<string>()).Should().AllBe("Method");

        var newFileContent = data["newFileContent"]!.Value<string>()!;
        newFileContent.Should().Contain("partial class SplitCandidate");
        newFileContent.Should().Contain("MoveMe1");
        newFileContent.Should().Contain("MoveMe2");
        newFileContent.Should().NotContain("KeepMe");
        newFileContent.Should().NotContain("KeepValue");
        newFileContent.Should().NotContain("NestedHelper");

        var updatedOrigin = data["updatedOriginContent"]!.Value<string>()!;
        updatedOrigin.Should().Contain("partial class SplitCandidate",
            "the origin half must also gain the partial modifier");
        updatedOrigin.Should().Contain("KeepMe");
        updatedOrigin.Should().Contain("NestedHelper");
        updatedOrigin.Should().NotContain("MoveMe1");
        updatedOrigin.Should().NotContain("MoveMe2");

        // Each moved member must exist exactly once across the two halves.
        CountOccurrences(newFileContent + updatedOrigin, "MoveMe1").Should().Be(1);
        CountOccurrences(newFileContent + updatedOrigin, "MoveMe2").Should().Be(1);

        File.Exists(Path.Combine(FixturesDir, "SplitCandidate.Partial.cs")).Should().BeFalse(
            "a generation-only tool must never create the file");
        File.ReadAllText(FixturePath).Should().Be(fixtureBefore,
            "a generation-only tool must never modify the origin file");
    }

    [Fact]
    public async Task SplitType_BothComputedContents_Compile()
    {
        var data = GetData(await Service.SplitTypeAsync(
            "SplitCandidate", new List<string> { "MoveMe1", "MoveMe2" }));

        foreach (var field in new[] { "newFileContent", "updatedOriginContent" })
        {
            var validation = await Service.ValidateCodeAsync(
                data[field]!.Value<string>()!, standalone: true);
            var validationData = GetData(validation);
            validationData["compiles"]!.Value<bool>().Should().BeTrue(
                $"{field} must be compilable C# as returned");
        }
    }

    [Fact]
    public async Task SplitType_UnknownMemberName_ReturnsSymbolNotFound()
    {
        var result = await Service.SplitTypeAsync(
            "SplitCandidate", new List<string> { "MoveMe1", "NoSuchMember" });

        AssertError(result, ErrorCodes.SymbolNotFound);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("NoSuchMember");
    }

    [Fact]
    public async Task SplitType_NestedMemberType_ReturnsInvalidParameter()
    {
        var result = await Service.SplitTypeAsync(
            "SplitCandidate", new List<string> { "NestedHelper" });

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("nested type");
    }

    [Fact]
    public async Task SplitType_OnARecord_ReturnsInvalidParameter()
    {
        // RecordFixture.cs declares `record Person`; records cannot be split.
        var result = await Service.SplitTypeAsync(
            "Person", new List<string> { "Name" });

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("Records cannot be split");
    }

    [Fact]
    public async Task SplitType_SelectingEveryMember_ReturnsInvalidParameter()
    {
        // MoveCandidateAlpha has exactly one member: selecting it would leave an
        // empty origin half, which is move_type_to_file's job.
        var result = await Service.SplitTypeAsync(
            "MoveCandidateAlpha", new List<string> { "AlphaValue" });

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("move_type_to_file instead");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
