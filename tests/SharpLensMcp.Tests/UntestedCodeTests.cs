using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_untested_code against the TestCoverageFixture. The fixture is in
// SharpLensMcp.Tests (this project) and the test methods are [Fact] in the
// same project. find_untested_code runs against the test project to verify
// the BFS-from-tests mechanism end-to-end.
public class UntestedCodeTests : RoslynServiceTestBase
{
    [Fact]
    public async Task FindUntestedCode_ReportsNeverCalledMethod_NotChainedOnes()
    {
        var result = await Service.FindUntestedCodeAsync(
            projectName: "SharpLensMcp.Tests",
            includeProperties: false,
            includeInternal: false,
            maxResults: 200);
        AssertSuccess(result);

        var data = GetData(result);
        // Lock testMethodCount with NotBeNull-first to defeat the null-conditional
        // silent-pass pattern (`data["X"]?.Value<int>().Should().Be(...)` skips the
        // assertion entirely if `X` is missing).
        data["testMethodCount"].Should().NotBeNull();
        data["testMethodCount"]!.Value<int>().Should().BeGreaterThan(0,
            "the test project has many [Fact] methods");

        var uncovered = data["uncoveredSymbols"] as JArray;
        uncovered.Should().NotBeNull();

        var names = uncovered!.Select(s => s["fullName"]?.Value<string>() ?? "").ToList();

        names.Should().Contain(n => n.Contains("CoverageTarget.NeverCalled"),
            "NeverCalled has no caller of any kind");
        names.Should().Contain(n => n.Contains("CoverageTarget.OnlyCalledByProduction"),
            "OnlyCalledByProduction is reached only from production code, not tests");
        names.Should().Contain(n => n.Contains("CoverageTarget.ProductionUser"),
            "ProductionUser has no [Fact] reaching it — only OnlyCalledByProduction via prod chain");

        names.Should().NotContain(n => n.Contains("CoverageTarget.CoveredByTest"),
            "CoveredByTest is directly invoked by a [Fact]");
        names.Should().NotContain(n => n.Contains("CoverageTarget.CalledByChainedFromTest"),
            "CalledByChainedFromTest is reached transitively via ChainedFromTest");
        names.Should().NotContain(n => n.Contains("CoverageTarget.ChainedFromTest"),
            "ChainedFromTest is the entry called by a [Fact]");

        // Test methods themselves must NOT appear in uncovered — they're the test
        // surface, not the production surface.
        names.Should().NotContain(n => n.Contains("CoverageTargetTests.CoveredByTest_Test"),
            "test methods are excluded from the production-surface uncovered list");
        names.Should().NotContain(n => n.Contains("CoverageTargetTests.ChainedFromTest_Test"),
            "test methods are excluded from the production-surface uncovered list");
    }
}
