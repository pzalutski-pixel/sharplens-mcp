using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_super_method against SuperMethodFixture: an override that also implements
// an interface returns both, a pure override returns only the base, a plain
// method returns nothing, an implicit-only implementer returns the interface
// member, and an explicit implementation (reached positionally) returns its
// interface member.
public class SuperMethodTests : RoslynServiceTestBase
{
    private string FixturePath => Path.Combine(
        Path.GetDirectoryName(SolutionPath)!, "tests", "SharpLensMcp.Tests", "Fixtures", "SuperMethodFixture.cs");

    private static List<string> Signatures(JToken list) =>
        (list as JArray)!.Select(e => e["signature"]!.Value<string>()!).ToList();

    [Fact]
    public async Task OverrideThatAlsoImplements_ReturnsBothBaseAndInterface()
    {
        var data = GetData(await Service.GetSuperMethodAsync(
            typeName: "SuperDerived", memberName: "Handle"));

        Signatures(data["overrides"]!).Should().ContainSingle()
            .Which.Should().Contain("SuperBase.Handle");
        Signatures(data["implements"]!).Should().ContainSingle()
            .Which.Should().Contain("ISuperContract.Handle");

        var baseEntry = (data["overrides"] as JArray)![0];
        baseEntry["inMetadata"]!.Value<bool>().Should().BeFalse();
        baseEntry["location"]!["filePath"]!.Value<string>().Should().EndWith("SuperMethodFixture.cs");
        // The location must carry a column like every other navigation location.
        baseEntry["location"]!["column"].Should().NotBeNull();
        baseEntry["location"]!["column"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task PureOverride_ReturnsOnlyTheBaseMember()
    {
        var data = GetData(await Service.GetSuperMethodAsync(
            typeName: "SuperDerived", memberName: "Describe"));

        Signatures(data["overrides"]!).Should().ContainSingle()
            .Which.Should().Contain("SuperBase.Describe");
        (data["implements"] as JArray)!.Should().BeEmpty();
    }

    [Fact]
    public async Task PlainMethod_ReturnsBothEmpty()
    {
        var data = GetData(await Service.GetSuperMethodAsync(
            typeName: "SuperDerived", memberName: "PlainMethod"));

        (data["overrides"] as JArray)!.Should().BeEmpty();
        (data["implements"] as JArray)!.Should().BeEmpty();
    }

    [Fact]
    public async Task ImplicitImplementer_ReturnsTheInterfaceMember()
    {
        var data = GetData(await Service.GetSuperMethodAsync(
            typeName: "SimpleImpl", memberName: "Handle"));

        (data["overrides"] as JArray)!.Should().BeEmpty("SimpleImpl has no base class");
        Signatures(data["implements"]!).Should().ContainSingle()
            .Which.Should().Contain("ISuperContract.Handle");
    }

    [Fact]
    public async Task ExplicitImplementation_ReachedPositionally_ReturnsTheInterfaceMember()
    {
        var lines = File.ReadAllLines(FixturePath);
        var lineIndex = Array.FindIndex(lines, l => l.Contains("void ISuperContract.ExplicitOnly"));
        lineIndex.Should().BeGreaterThan(-1);
        var column = lines[lineIndex].IndexOf("ExplicitOnly", StringComparison.Ordinal);

        var data = GetData(await Service.GetSuperMethodAsync(
            filePath: FixturePath, line: lineIndex, column: column));

        Signatures(data["implements"]!).Should().ContainSingle()
            .Which.Should().Contain("ISuperContract.ExplicitOnly");
    }

    [Fact]
    public async Task PropertyOverride_ReturnsTheBaseProperty()
    {
        var data = GetData(await Service.GetSuperMethodAsync(
            typeName: "SuperDerived", memberName: "Score"));

        Signatures(data["overrides"]!).Should().ContainSingle()
            .Which.Should().Contain("SuperBase.Score");
        (data["implements"] as JArray)!.Should().BeEmpty();
    }

    [Fact]
    public async Task EventImplementation_ReturnsTheInterfaceEvent()
    {
        var data = GetData(await Service.GetSuperMethodAsync(
            typeName: "SimpleImpl", memberName: "Fired"));

        Signatures(data["implements"]!).Should().ContainSingle()
            .Which.Should().Contain("ISuperContract.Fired");
    }

    [Fact]
    public async Task OverrideOfMetadataBase_ReportsInMetadataTrue()
    {
        // SuperDerived.ToString overrides object.ToString, which lives in metadata.
        var data = GetData(await Service.GetSuperMethodAsync(
            typeName: "SuperDerived", memberName: "ToString"));

        var overrides = (data["overrides"] as JArray)!;
        overrides.Should().ContainSingle();
        overrides[0]["inMetadata"]!.Value<bool>().Should().BeTrue(
            "object.ToString is defined in metadata, not solution source");
        overrides[0]["location"]!.Type.Should().Be(JTokenType.Null,
            "a metadata member has no source location");
    }

    [Fact]
    public async Task NeitherAddressingMode_ReturnsInvalidParameter()
    {
        var result = await Service.GetSuperMethodAsync();

        AssertError(result, ErrorCodes.InvalidParameter);
    }
}
