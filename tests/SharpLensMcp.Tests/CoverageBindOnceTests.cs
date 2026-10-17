using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// How often find_tests binds each method body within ONE call, observed through
// RoslynService's body-bind seam (a callback fired each time the coverage walk binds a
// body to collect its callees). Binding is the expensive step, and the per-test walks
// share one callee memo, so each body is bound at most once per call no matter how many
// tests walk through it; each test's walk also stops as soon as the target is reached,
// since its answer is then known. The tool's answer is locked alongside the counts.
public class CoverageBindOnceTests
{
    internal const string ChainLib = @"
namespace Lib
{
    public class Chain
    {
        public int H1() => H2();
        public int H2() => H3();
        public int H3() => 3;
        public int Other() => 0;
    }
}";

    internal const string ChainTests = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}

namespace Lib.Tests
{
    public class ChainTests
    {
        [Xunit.Fact] public void T1() { _ = new Lib.Chain().H1(); }
        [Xunit.Fact] public void T2() { _ = new Lib.Chain().H1(); }
        [Xunit.Fact] public void T3() { _ = new Lib.Chain().H2(); }
        [Xunit.Fact] public void T4() { _ = new Lib.Chain().Other(); }
    }
}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_SharedHelperChain_BindsEachBodyOnce_AndStopsAtTheTarget(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(ChainLib, ChainTests, retarget);
        var (service, binds) = await CoverageRetargetingTests.LoadObservingBindsAsync(fixture);

        var data = CoverageRetargetingTests.Data(await service.FindTestsAsync("Lib.Chain", "H3"));

        data["totalTests"]!.Value<int>().Should().Be(4);
        ((JArray)data["coveringTests"]!).Select(t => t["testMethod"]!.Value<string>()).Should()
            .Equal(new[] { "T1", "T2", "T3" }, "T1 and T2 reach H3 through H1 -> H2, T3 through H2; T4 never does");

        // Every test body once; H1 and H2 once although three tests walk through them;
        // Other once (T4's walk exhausts without reaching H3). H3 itself is never bound:
        // reaching it already answers each test, so its callees are never needed.
        binds.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Lib.Tests.ChainTests.T1()"] = 1,
            ["Lib.Tests.ChainTests.T2()"] = 1,
            ["Lib.Tests.ChainTests.T3()"] = 1,
            ["Lib.Tests.ChainTests.T4()"] = 1,
            ["Lib.Chain.H1()"] = 1,
            ["Lib.Chain.H2()"] = 1,
            ["Lib.Chain.Other()"] = 1,
        });
    }
}
