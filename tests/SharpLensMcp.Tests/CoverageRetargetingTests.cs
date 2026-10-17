using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_tests and find_untested_code across a project boundary that Roslyn crosses through
// a RETARGETING assembly (the test project's references differ from the library's). A
// symbol a test body binds to is then a distinct instance from the library's own symbol
// for the same declaration, so coverage membership must go through cross-compilation
// identity, and a method reached through both instances is still one body to walk.
// Every case runs on the retargeted shape AND on the control shape (shared symbols):
// the expected answer is the same for both.
public class CoverageRetargetingTests
{
    internal const string LibSource = @"
namespace Lib
{
    public interface ICalc { int Triple(int x); }

    public class Calculator : ICalc
    {
        public int Add(int a, int b) => a + b;
        public int Chain() => Helper();
        public int Helper() => 7;
        public int Triple(int x) => x * 3;
        public int Never() => 0;
    }
}";

    internal const string TestSource = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}

namespace Lib.Tests
{
    public class CalculatorTests
    {
        [Xunit.Fact]
        public void AddTest() { _ = new Lib.Calculator().Add(1, 2); }

        [Xunit.Fact]
        public void ChainTest() { _ = new Lib.Calculator().Chain(); }

        [Xunit.Fact]
        public void TripleViaImplTest() { _ = new Lib.Calculator().Triple(2); }
    }
}";

    // A service over the fixture. Both projects must first compile cleanly: a fixture with
    // a compile error binds part of its code to error symbols, and a test over it could
    // pass or fail for a reason unrelated to what it is about.
    internal static async Task<RoslynService> LoadAsync(RetargetedSolutionFixture fixture)
    {
        foreach (var project in fixture.Workspace.CurrentSolution.Projects)
        {
            var compilation = (await project.GetCompilationAsync())!;
            compilation.GetDiagnostics()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .Should().BeEmpty($"the {project.Name} fixture source must compile");
        }
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(fixture.Workspace);
        return service;
    }

    // A service over the fixture whose coverage walk reports, through the body-bind
    // seam, how many times each method body was bound during a call.
    internal static async Task<(RoslynService service, Dictionary<string, int> binds)> LoadObservingBindsAsync(
        RetargetedSolutionFixture fixture)
    {
        var service = await LoadAsync(fixture);
        var binds = new Dictionary<string, int>(StringComparer.Ordinal);
        service.ObserveCoverageBodyBindsForTesting(method =>
        {
            var name = method.ToDisplayString();
            binds[name] = binds.GetValueOrDefault(name) + 1;
        });
        return (service, binds);
    }

    internal static JToken Data(object result)
    {
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("the response envelope must carry success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        return json["data"]!;
    }

    private static List<string> CoveringNames(JToken data) =>
        ((JArray)data["coveringTests"]!).Select(t => t["testMethod"]!.Value<string>()!).ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_DirectlyCalledLibMember_IsCoveredByItsTest(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, TestSource, retarget);
        var data = Data(await (await LoadAsync(fixture)).FindTestsAsync("Lib.Calculator", "Add"));

        data["totalTests"]!.Value<int>().Should().Be(3, "Lib.Tests declares exactly three [Fact] methods");
        CoveringNames(data).Should().Equal(new[] { "AddTest" },
            "AddTest calls Calculator.Add directly; the retargeted Add it binds to is the same declaration");
        var entry = ((JArray)data["coveringTests"]!)[0];
        entry["containingType"]!.Value<string>().Should().Be("Lib.Tests.CalculatorTests");
        entry["testProject"]!.Value<string>().Should().Be(RetargetedSolutionFixture.TestProjectName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_TypeLevel_EveryTestReachingAnyMemberCovers(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, TestSource, retarget);
        var data = Data(await (await LoadAsync(fixture)).FindTestsAsync("Lib.Calculator"));

        CoveringNames(data).Should().Equal(new[] { "AddTest", "ChainTest", "TripleViaImplTest" },
            "each test calls a Calculator member directly, so each covers the type");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_InterfaceMemberSeededThroughRetargetedImplementation(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, TestSource, retarget);
        var data = Data(await (await LoadAsync(fixture)).FindTestsAsync("Lib.ICalc", "Triple"));

        CoveringNames(data).Should().Equal(new[] { "TripleViaImplTest" },
            "the test reaches Calculator.Triple, which implements ICalc.Triple, so the interface member is seeded covered");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindUntestedCode_DirectlyCalledLibMembers_AreNotReportedUntested(bool retarget)
    {
        using var fixture = new RetargetedSolutionFixture(LibSource, TestSource, retarget);
        var data = Data(await (await LoadAsync(fixture)).FindUntestedCodeAsync(
            projectName: RetargetedSolutionFixture.LibProjectName,
            includeProperties: true, includeInternal: false, maxResults: 50));

        data["productionProject"]!.Value<string>().Should().Be(RetargetedSolutionFixture.LibProjectName);
        data["testMethodCount"]!.Value<int>().Should().Be(3);
        ((JArray)data["testProjectsScanned"]!).Select(p => p.Value<string>()).Should()
            .Equal(RetargetedSolutionFixture.TestProjectName);
        ((JArray)data["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>()).Should().Equal(
            new[] { "Lib.Calculator.Never()" },
            "Add, Chain and Triple are called directly by tests, Helper transitively, and ICalc.Triple is seeded " +
            "through Calculator.Triple — only Never is unreached");
    }

    private const string CrossLib = @"
namespace Lib
{
    public class Calculator
    {
        public int Chain() => Helper();
        public int Helper() => Leaf();
        public int Leaf() => 1;
        public int Never() => 0;
    }
}";

    private const string CrossTests = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
}

namespace Lib.Tests
{
    public class CalculatorTests
    {
        [Xunit.Fact] public void ChainTest() { _ = new Lib.Calculator().Chain(); }
        [Xunit.Fact] public void HelperTest() { _ = new Lib.Calculator().Helper(); }
    }
}";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindUntestedCode_MethodReachedFromBothProjects_IsWalkedOnce(bool retarget)
    {
        // Helper is reached twice: from Chain's body (bound in Lib, Lib's own symbol) and
        // from HelperTest (bound in Lib.Tests, a retargeted instance when retarget is on).
        // Both denote one declaration, so Helper's and Leaf's bodies are each bound once.
        using var fixture = new RetargetedSolutionFixture(CrossLib, CrossTests, retarget);
        var (service, binds) = await LoadObservingBindsAsync(fixture);

        var data = Data(await service.FindUntestedCodeAsync(
            projectName: RetargetedSolutionFixture.LibProjectName,
            includeProperties: false, includeInternal: false, maxResults: 50));

        ((JArray)data["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>()).Should()
            .Equal("Lib.Calculator.Never()");
        binds.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Lib.Tests.CalculatorTests.ChainTest()"] = 1,
            ["Lib.Tests.CalculatorTests.HelperTest()"] = 1,
            ["Lib.Calculator.Chain()"] = 1,
            ["Lib.Calculator.Helper()"] = 1,
            ["Lib.Calculator.Leaf()"] = 1,
        });
    }
}
