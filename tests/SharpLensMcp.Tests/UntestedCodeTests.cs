using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_untested_code against the TestCoverageFixture. The fixture is in
// SharpLensMcp.Tests (this project) and the test methods are [Fact] in the
// same project. find_untested_code runs against the test project to verify
// the BFS-from-tests mechanism end-to-end — chained reachability, production-
// only callers (not tests), and test methods themselves being excluded from
// the production-surface list.
public class UntestedCodeTests : RoslynServiceTestBase
{
    [Fact]
    public async Task FindUntestedCode_ReportsNeverCalledAndProductionOnlyChains_ExcludesReachableAndTestMethods()
    {
        var result = await Service.FindUntestedCodeAsync(
            projectName: "SharpLensMcp.Tests",
            includeProperties: false,
            includeInternal: false,
            maxResults: 200);
        AssertSuccess(result);

        var data = GetData(result);
        // Locks the response-envelope metadata. NotBeNull-first defeats the
        // null-conditional silent-pass on every accessor.
        data["productionProject"]!.Value<string>().Should().Be("SharpLensMcp.Tests",
            "the impl must pick the project requested by projectName (Quality.cs:137)");
        data["testMethodCount"]!.Value<int>().Should().BeGreaterThan(0,
            "the test project has many [Fact] methods — IsTestMethod must match at least one");
        data["reachableSymbolCount"]!.Value<int>().Should().BeGreaterThan(
            data["testMethodCount"]!.Value<int>(),
            "BFS-from-tests must reach AT LEAST one production symbol beyond the test methods themselves (CoveredByTest etc.)");

        // testProjectsScanned tracks every project where IsTestMethod found a hit
        // (Quality.cs:90, 108). The fixture's [Fact] methods live in SharpLensMcp.Tests,
        // so the set must include that name.
        var scanned = (data["testProjectsScanned"] as JArray)!;
        scanned.Select(s => s.Value<string>()).Should().Contain("SharpLensMcp.Tests",
            "every project that contains a [Fact]/[Theory]/[Test]/[TestMethod] must appear in testProjectsScanned");

        var uncovered = (data["uncoveredSymbols"] as JArray)!;
        var names = uncovered.Select(s => s["fullName"]!.Value<string>()!).ToList();

        // Expected uncovered set: NeverCalled (zero callers), OnlyCalledByProduction
        // (only reached from production code, not tests), ProductionUser (only the
        // OnlyCalledByProduction chain — still no [Fact] reaches it).
        names.Should().Contain(n => n.Contains("CoverageTarget.NeverCalled"),
            "NeverCalled has no caller of any kind");
        names.Should().Contain(n => n.Contains("CoverageTarget.OnlyCalledByProduction"),
            "OnlyCalledByProduction is reached only from production code, not tests");
        names.Should().Contain(n => n.Contains("CoverageTarget.ProductionUser"),
            "ProductionUser has no [Fact] reaching it — only the prod chain calls it");

        // Reached set must NOT appear in uncovered.
        names.Should().NotContain(n => n.Contains("CoverageTarget.CoveredByTest"),
            "CoveredByTest is directly invoked by CoveredByTest_Test");
        names.Should().NotContain(n => n.Contains("CoverageTarget.CalledByChainedFromTest"),
            "CalledByChainedFromTest is reached transitively via ChainedFromTest — locks the BFS step beyond depth 1");
        names.Should().NotContain(n => n.Contains("CoverageTarget.ChainedFromTest"),
            "ChainedFromTest is the entry called by ChainedFromTest_Test");

        // Test methods themselves must NOT appear (Quality.cs:158 filters IsTestMethod).
        names.Should().NotContain(n => n.Contains("CoverageTargetTests.CoveredByTest_Test"),
            "test methods are excluded from the production-surface uncovered list");
        names.Should().NotContain(n => n.Contains("CoverageTargetTests.ChainedFromTest_Test"),
            "test methods are excluded from the production-surface uncovered list");

        // Per-entry shape on NeverCalled (Quality.cs:62-70). Without this lock,
        // a regression that drops kind/accessibility/complexity/location/reason
        // would still pass every name-only assertion above.
        var neverCalled = uncovered.First(s =>
            s["fullName"]!.Value<string>()!.Contains("CoverageTarget.NeverCalled"));
        neverCalled["kind"]!.Value<string>().Should().Be("Method");
        neverCalled["accessibility"]!.Value<string>().Should().Be("Public");
        neverCalled["complexity"]!.Value<int>().Should().BeGreaterOrEqualTo(1,
            "EstimateCyclomaticAsync returns 1 + branches; the floor is 1");
        neverCalled["location"].Should().NotBeNull(
            "every uncovered entry must surface its declaration location");
        neverCalled["reason"]!.Value<string>().Should().Be("Not reachable from any test method",
            "the impl's documented reason string at Quality.cs:69 must round-trip verbatim");
    }
}
