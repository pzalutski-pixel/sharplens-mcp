using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_tests against the real loaded solution's TestCoverageFixture, which
// pairs CoverageTarget members with the tests that reach them (directly and
// transitively). Each assertion names the exact covering test.
public class FindTestsTests : RoslynServiceTestBase
{
    private async Task<JToken> FindFor(string memberName)
    {
        var result = await Service.FindTestsAsync("CoverageTarget", memberName);
        return GetData(result);
    }

    private static List<string> CoveringTestNames(JToken data) =>
        (data["coveringTests"] as JArray)!.Select(t => t["testMethod"]!.Value<string>()!).ToList();

    [Fact]
    public async Task DirectlyTestedMember_MapsToItsCoveringTest()
    {
        var data = await FindFor("CoveredByTest");

        // totalTests is a whole-solution scan count (not pinnable to an exact value),
        // but TestCoverageFixture alone declares two test methods, so it is at least 2.
        data["totalTests"]!.Value<int>().Should().BeGreaterThanOrEqualTo(2,
            "TestCoverageFixture alone declares CoveredByTest_Test and ChainedFromTest_Test");
        CoveringTestNames(data).Should().Contain("CoveredByTest_Test");
        CoveringTestNames(data).Should().NotContain("ChainedFromTest_Test",
            "CoveredByTest is reached only by its own test");

        var entry = (data["coveringTests"] as JArray)!
            .First(t => t["testMethod"]!.Value<string>() == "CoveredByTest_Test");
        entry["containingType"]!.Value<string>().Should().Contain("CoverageTargetTests");
        entry["testProject"]!.Value<string>().Should().Be("SharpLensMcp.Tests");
        var fixtureRel = entry["location"]!["filePath"]!.Value<string>()!;
        fixtureRel.Should().EndWith("TestCoverageFixture.cs");
        // Pin the covering test's reported line to its actual 0-based declaration line
        // in the fixture (resolved from a marker) instead of leaving location unchecked.
        var fixtureLines = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(SolutionPath)!, fixtureRel));
        var testLine = Array.FindIndex(fixtureLines, l => l.Contains("public void CoveredByTest_Test()"));
        testLine.Should().BeGreaterThan(-1, "the fixture must declare CoveredByTest_Test");
        entry["location"]!["line"]!.Value<int>().Should().Be(testLine,
            "the covering test's reported line must equal its 0-based declaration line");
    }

    [Fact]
    public async Task TransitivelyReachedMember_MapsToTheChainingTest()
    {
        var data = await FindFor("CalledByChainedFromTest");

        CoveringTestNames(data).Should().Contain("ChainedFromTest_Test",
            "the member is reached transitively through ChainedFromTest");
        CoveringTestNames(data).Should().NotContain("CoveredByTest_Test");
    }

    [Fact]
    public async Task UncoveredMember_ReturnsNoTests()
    {
        var data = await FindFor("NeverCalled");

        (data["coveringTests"] as JArray)!.Should().BeEmpty(
            "NeverCalled is reached by no test method");
        data["totalTests"]!.Value<int>().Should().BeGreaterThanOrEqualTo(2,
            "totalTests still reports the scanned tests — at least the fixture's two");
    }

    [Fact]
    public async Task InterfaceMember_CoveredThroughImplementation_IsReported()
    {
        // ICoverageContract.ContractMethod is reached only through CoverageImplementor's
        // implementation — the impl body never names the interface member, so the only
        // way it counts as covered is the override/interface seeding find_untested_code
        // uses. Without find_tests sharing that seeding, this returns no covering test,
        // disagreeing with find_untested_code on the same symbol.
        var result = await Service.FindTestsAsync("ICoverageContract", "ContractMethod");
        var data = GetData(result);

        CoveringTestNames(data).Should().Contain("ContractViaImpl_Test",
            "the interface member is exercised through CoverageImplementor.ContractMethod, which ContractViaImpl_Test calls");
    }

    [Fact]
    public async Task UnknownType_PropagatesTypeNotFound()
    {
        var result = await Service.FindTestsAsync("NoSuchTypeAnywhere", "X");

        AssertError(result, ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task TypeLevel_NoMember_CoversTypeViaAnyMember()
    {
        // With no memberName, a test covers the type when it reaches ANY of its members,
        // so both the direct and the transitive fixture tests qualify.
        var data = GetData(await Service.FindTestsAsync("CoverageTarget"));

        data["target"]!.Value<string>().Should().EndWith("CoverageTarget",
            "the type itself is the coverage target when no member is given");
        var names = CoveringTestNames(data);
        names.Should().Contain("CoveredByTest_Test");
        names.Should().Contain("ChainedFromTest_Test",
            "both fixture tests reach a member of CoverageTarget, so both cover the type");
    }
}
