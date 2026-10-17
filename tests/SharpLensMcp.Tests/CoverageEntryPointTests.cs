using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The entry points a test runner invokes that no call in the source reaches: test methods
// recognised by each framework's discovery contract (attribute base type or interface,
// not the attribute's exact name), step bindings and hooks, and the lifecycle code that
// runs as part of a test (constructors, initializers, set-up/tear-down methods, fixtures,
// IAsyncLifetime and Dispose, including members declared on base classes). Lib holds one
// member per entry point; the stubbed frameworks (CoverageFrameworkStubs) live in Lib.Tests.
public class CoverageEntryPointTests
{
    internal const string DiscoveryLib = @"
namespace Lib
{
    public class Target
    {
        public void ViaSkippableFact() { }
        public void ViaV3CustomFact() { }
        public void ViaNUnitTheory() { }
        public void ViaNUnitTestCase() { }
        public void ViaMSTestDataTestMethod() { }
        public void ViaTUnitTest() { }
        public void ViaReqnrollStep() { }
        public void ViaReqnrollHook() { }
        public void ViaSpecFlowStep() { }
        public void ViaSpecFlowHook() { }
        public void Unreached() { }
    }
}";

    internal static readonly string DiscoveryTests = CoverageFrameworkStubs.Source + @"
namespace Lib.Tests.Fakes
{
    // Same simple names as real framework types, in an unrelated namespace.
    public class FactAttribute : System.Attribute { }
    public class WhenAttribute : System.Attribute { }
}

namespace Lib.Tests
{
    public class MyV3FactAttribute : System.Attribute, Xunit.v3.IFactAttribute { }

    public class XunitTests
    {
        [Xunit.SkippableFact] public void Skippable() { new Lib.Target().ViaSkippableFact(); }
        [MyV3Fact] public void CustomV3() { new Lib.Target().ViaV3CustomFact(); }
    }

    [NUnit.Framework.TestFixture]
    public class NUnitTests
    {
        [NUnit.Framework.Theory] public void TheoryTest() { new Lib.Target().ViaNUnitTheory(); }
        [NUnit.Framework.TestCase(1)] public void CaseTest(int x) { new Lib.Target().ViaNUnitTestCase(); }
    }

    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public class MSTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethod]
        public void DataTest() { new Lib.Target().ViaMSTestDataTestMethod(); }
    }

    public class TUnitTests
    {
        [TUnit.Core.Test] public void TUnitTest() { new Lib.Target().ViaTUnitTest(); }
    }

    [Reqnroll.Binding]
    public class ReqnrollSteps
    {
        [Reqnroll.When(""it runs"")] public void WhenItRuns() { new Lib.Target().ViaReqnrollStep(); }
        [Reqnroll.BeforeScenario] public void BeforeScenario() { new Lib.Target().ViaReqnrollHook(); }
    }

    [TechTalk.SpecFlow.Binding]
    public class SpecFlowSteps
    {
        [TechTalk.SpecFlow.Given(""a thing"")] public void GivenAThing() { new Lib.Target().ViaSpecFlowStep(); }
        [TechTalk.SpecFlow.AfterScenario] public void AfterScenario() { new Lib.Target().ViaSpecFlowHook(); }
    }

    public class LookAlikes
    {
        [Fakes.Fact] public void NotATest() { new Lib.Target().Unreached(); }
        [Fakes.When] public void NotAStep() { new Lib.Target().Unreached(); }
    }
}";

    internal const string LifecycleLib = @"
namespace Lib
{
    public class Life
    {
        public void ViaCtor() { }
        public void ViaFieldInit() { }
        public void ViaBaseCtor() { }
        public void ViaClassFixture() { }
        public void ViaCollectionFixture() { }
        public void ViaCollectionDefinitionClassFixture() { }
        public void ViaInitializeAsync() { }
        public void ViaDisposeAsync() { }
        public void ViaDispose() { }
        public void ViaNUnitBaseSetUp() { }
        public void ViaNUnitTearDown() { }
        public void ViaNUnitOneTimeSetUp() { }
        public void ViaMSTestTestInitialize() { }
        public void ViaMSTestClassCleanup() { }
        public void ViaTUnitBeforeTest() { }
        public void ViaOrphanSetUp() { }
        public void Unreached() { }
    }
}";

    internal static readonly string LifecycleTests = CoverageFrameworkStubs.Source + @"
namespace Lib.Tests
{
    public abstract class XunitBase
    {
        protected XunitBase() { new Lib.Life().ViaBaseCtor(); }
    }

    public class ClassFx { public ClassFx() { new Lib.Life().ViaClassFixture(); } }
    public class CollFx { public CollFx() { new Lib.Life().ViaCollectionFixture(); } }
    public class DefClassFx { public DefClassFx() { new Lib.Life().ViaCollectionDefinitionClassFixture(); } }

    [Xunit.CollectionDefinition(""shared"")]
    public class SharedCollection : Xunit.ICollectionFixture<CollFx>, Xunit.IClassFixture<DefClassFx> { }

    [Xunit.Collection(""shared"")]
    public class XunitLifecycleTests : XunitBase, Xunit.IClassFixture<ClassFx>, Xunit.IAsyncLifetime, System.IDisposable
    {
        private readonly int _seed = Seed();
        private static int Seed() { new Lib.Life().ViaFieldInit(); return 1; }

        public XunitLifecycleTests() { new Lib.Life().ViaCtor(); }

        public System.Threading.Tasks.Task InitializeAsync()
        {
            new Lib.Life().ViaInitializeAsync();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public System.Threading.Tasks.Task DisposeAsync()
        {
            new Lib.Life().ViaDisposeAsync();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        public void Dispose() { new Lib.Life().ViaDispose(); }

        [Xunit.Fact] public void Runs() { }
        [Xunit.Fact] public void RunsToo() { }
    }

    public class NUnitBase
    {
        [NUnit.Framework.SetUp] public void BaseSetUp() { new Lib.Life().ViaNUnitBaseSetUp(); }
    }

    public class NUnitLifecycleTests : NUnitBase
    {
        [NUnit.Framework.TearDown] public void TearDown() { new Lib.Life().ViaNUnitTearDown(); }
        [NUnit.Framework.OneTimeSetUp] public void OneTime() { new Lib.Life().ViaNUnitOneTimeSetUp(); }
        [NUnit.Framework.Test] public void NRuns() { }
    }

    public class MSTestLifecycleTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestInitialize]
        public void Init() { new Lib.Life().ViaMSTestTestInitialize(); }

        [Microsoft.VisualStudio.TestTools.UnitTesting.ClassCleanup]
        public static void Cleanup() { new Lib.Life().ViaMSTestClassCleanup(); }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod] public void MRuns() { }
    }

    public class TUnitLifecycleTests
    {
        [TUnit.Core.Before(TUnit.Core.HookType.Test)] public void BeforeEach() { new Lib.Life().ViaTUnitBeforeTest(); }
        [TUnit.Core.Test] public void TRuns() { }
    }

    // A set-up method in a class that declares no tests never runs.
    public class NoTestsHere
    {
        [NUnit.Framework.SetUp] public void Orphan() { new Lib.Life().ViaOrphanSetUp(); }
    }
}";

    internal const string HooksLib = @"
namespace Lib
{
    public class Hooks
    {
        public void ViaAssemblyInit() { }
        public void ViaGlobalTestInit() { }
        public void ViaSetUpFixture() { }
        public void ViaTUnitAssemblyHook() { }
        public void ViaTUnitBeforeEvery() { }
        public void ViaAssemblyFixture() { }
        public void ViaBeforeTestRun() { }
        public void Unreached() { }
    }
}";

    internal static readonly string HooksTests =
        "[assembly: Xunit.AssemblyFixture(typeof(Lib.Tests.AsmFx))]\n" + CoverageFrameworkStubs.Source + @"
namespace Lib.Tests
{
    public class AsmFx { public AsmFx() { new Lib.Hooks().ViaAssemblyFixture(); } }

    public class MSHooks
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.AssemblyInitialize]
        public static void AsmInit() { new Lib.Hooks().ViaAssemblyInit(); }

        [Microsoft.VisualStudio.TestTools.UnitTesting.GlobalTestInitialize]
        public static void GlobalInit() { new Lib.Hooks().ViaGlobalTestInit(); }
    }

    [NUnit.Framework.SetUpFixture]
    public class NamespaceSetUp
    {
        [NUnit.Framework.OneTimeSetUp] public void Once() { new Lib.Hooks().ViaSetUpFixture(); }
    }

    public class TUnitHooks
    {
        [TUnit.Core.Before(TUnit.Core.HookType.Assembly)] public static void AsmHook() { new Lib.Hooks().ViaTUnitAssemblyHook(); }
        [TUnit.Core.BeforeEvery(TUnit.Core.HookType.Test)] public static void Every() { new Lib.Hooks().ViaTUnitBeforeEvery(); }
    }

    [Reqnroll.Binding]
    public class RunHooks
    {
        [Reqnroll.BeforeTestRun] public static void BeforeRun() { new Lib.Hooks().ViaBeforeTestRun(); }
    }
}";

    private static async Task<JToken> Run(string lib, string tests, bool retarget, Func<RoslynService, Task<object>> call)
    {
        using var fixture = new RetargetedSolutionFixture(lib, tests, retarget);
        var service = await CoverageRetargetingTests.LoadAsync(fixture);
        return CoverageRetargetingTests.Data(await call(service));
    }

    private static Task<object> UntestedLib(RoslynService service) => service.FindUntestedCodeAsync(
        projectName: RetargetedSolutionFixture.LibProjectName,
        includeProperties: false, includeInternal: false, maxResults: 100);

    private static List<string> UncoveredNames(JToken data) =>
        ((JArray)data["uncoveredSymbols"]!).Select(s => s["fullName"]!.Value<string>()!).ToList();

    private static List<(string method, string type, string kind)> Covering(JToken data) =>
        ((JArray)data["coveringTests"]!).Select(t => (
            t["testMethod"]!.Value<string>()!,
            t["containingType"]!.Value<string>()!,
            t["kind"]!.Value<string>()!)).ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindUntestedCode_RecognisesEachFrameworksDiscoveryContract(bool retarget)
    {
        var data = await Run(DiscoveryLib, DiscoveryTests, retarget, UntestedLib);

        data["testMethodCount"]!.Value<int>().Should().Be(6,
            "SkippableFact (derives Fact), a v3 IFactAttribute, NUnit Theory and TestCase (ITestBuilder), " +
            "MSTest DataTestMethod (derives TestMethod) and TUnit Test (derives BaseTestAttribute)");
        data["entryPointCounts"].Should().NotBeNull("the entry-point breakdown must be reported");
        data["entryPointCounts"]!["tests"]!.Value<int>().Should().Be(6);
        data["entryPointCounts"]!["stepBindings"]!.Value<int>().Should().Be(2, "one Reqnroll and one SpecFlow step");
        data["entryPointCounts"]!["hooks"]!.Value<int>().Should().Be(2, "one Reqnroll and one SpecFlow hook");
        ((JArray)data["testProjectsScanned"]!).Select(p => p.Value<string>()).Should()
            .Equal(RetargetedSolutionFixture.TestProjectName);
        UncoveredNames(data).Should().Equal(new[] { "Lib.Target.Unreached()" },
            "the look-alike attributes in Lib.Tests.Fakes are no framework's contract, so their callee stays unreached");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_ReportsTheKindOfEachEntryPoint(bool retarget)
    {
        var step = await Run(DiscoveryLib, DiscoveryTests, retarget,
            s => s.FindTestsAsync("Lib.Target", "ViaReqnrollStep"));
        step["totalTests"]!.Value<int>().Should().Be(6, "totalTests counts test methods only");
        Covering(step).Should().Equal(("WhenItRuns", "Lib.Tests.ReqnrollSteps", "stepBinding"));

        var hook = await Run(DiscoveryLib, DiscoveryTests, retarget,
            s => s.FindTestsAsync("Lib.Target", "ViaSpecFlowHook"));
        Covering(hook).Should().Equal(("AfterScenario", "Lib.Tests.SpecFlowSteps", "hook"));

        var test = await Run(DiscoveryLib, DiscoveryTests, retarget,
            s => s.FindTestsAsync("Lib.Target", "ViaNUnitTheory"));
        Covering(test).Should().Equal(("TheoryTest", "Lib.Tests.NUnitTests", "test"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindUntestedCode_LifecycleCodeRunsAsPartOfTheTests(bool retarget)
    {
        var data = await Run(LifecycleLib, LifecycleTests, retarget, UntestedLib);

        data["testMethodCount"]!.Value<int>().Should().Be(5);
        data["entryPointCounts"]!["tests"]!.Value<int>().Should().Be(5);
        data["entryPointCounts"]!["stepBindings"]!.Value<int>().Should().Be(0);
        data["entryPointCounts"]!["hooks"]!.Value<int>().Should().Be(0);
        UncoveredNames(data).Should().Equal(new[] { "Lib.Life.Unreached()", "Lib.Life.ViaOrphanSetUp()" },
            "constructors (own and base), field initializers, class and collection fixtures, IAsyncLifetime, " +
            "Dispose, NUnit set-up/tear-down (also on a base class), MSTest initialize/cleanup and TUnit " +
            "Before(Test) all run for a test; only a set-up in a class without tests never runs");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_LifecycleCodeFoldsIntoTheTestsOfItsClass(bool retarget)
    {
        var ctor = await Run(LifecycleLib, LifecycleTests, retarget, s => s.FindTestsAsync("Lib.Life", "ViaCtor"));
        Covering(ctor).Should().Equal(
            ("Runs", "Lib.Tests.XunitLifecycleTests", "test"),
            ("RunsToo", "Lib.Tests.XunitLifecycleTests", "test"));

        var collection = await Run(LifecycleLib, LifecycleTests, retarget,
            s => s.FindTestsAsync("Lib.Life", "ViaCollectionFixture"));
        Covering(collection).Should().Equal(
            ("Runs", "Lib.Tests.XunitLifecycleTests", "test"),
            ("RunsToo", "Lib.Tests.XunitLifecycleTests", "test"));

        var baseSetUp = await Run(LifecycleLib, LifecycleTests, retarget,
            s => s.FindTestsAsync("Lib.Life", "ViaNUnitBaseSetUp"));
        Covering(baseSetUp).Should().Equal(("NRuns", "Lib.Tests.NUnitLifecycleTests", "test"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindUntestedCode_AssemblyAndRunScopedHooksAreEntryPoints(bool retarget)
    {
        var data = await Run(HooksLib, HooksTests, retarget, UntestedLib);

        data["testMethodCount"]!.Value<int>().Should().Be(0);
        data["entryPointCounts"]!["tests"]!.Value<int>().Should().Be(0);
        data["entryPointCounts"]!["stepBindings"]!.Value<int>().Should().Be(0);
        data["entryPointCounts"]!["hooks"]!.Value<int>().Should().Be(7,
            "MSTest AssemblyInitialize and GlobalTestInitialize, the NUnit SetUpFixture's OneTimeSetUp, TUnit " +
            "Before(Assembly) and BeforeEvery, the Reqnroll BeforeTestRun hook and the xUnit v3 assembly fixture");
        ((JArray)data["testProjectsScanned"]!).Select(p => p.Value<string>()).Should()
            .Equal(new[] { RetargetedSolutionFixture.TestProjectName }, "a project with entry points of any kind is scanned");
        UncoveredNames(data).Should().Equal("Lib.Hooks.Unreached()");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindTests_AssemblyScopedHooksAreReportedAsHooks(bool retarget)
    {
        var init = await Run(HooksLib, HooksTests, retarget, s => s.FindTestsAsync("Lib.Hooks", "ViaAssemblyInit"));
        Covering(init).Should().Equal(("AsmInit", "Lib.Tests.MSHooks", "hook"));

        var fixtureCtor = await Run(HooksLib, HooksTests, retarget,
            s => s.FindTestsAsync("Lib.Hooks", "ViaAssemblyFixture"));
        Covering(fixtureCtor).Should().Equal((".ctor", "Lib.Tests.AsmFx", "hook"));
    }
}
