namespace SharpLensMcp.Tests;

// In-memory stand-ins for the test frameworks' public attribute and interface types,
// declared in each framework's real namespace with the real names and the real
// inheritance/interface shape (checked against the packages' metadata: xunit 2.9.2 and
// xunit.v3 1.0.0/4.0.2, Xunit.SkippableFact 1.4.13, NUnit 4.2.2/5.0.0,
// MSTest.TestFramework 3.6.4/4.5.1, TUnit.Core 0.6.123/1.73.19, Reqnroll 2.2.1/3.3.4,
// SpecFlow 3.9.74). The Xunit types take xUnit v2's shape (FactAttribute implements no
// interface there) alongside the v3-only Xunit.v3 interfaces and AssemblyFixtureAttribute.
// The coverage engine recognises an entry point by that shape, so a stub with the wrong
// base type or interface would not be detected, exactly as the real framework would not
// discover it.
internal static class CoverageFrameworkStubs
{
    public const string Source = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public class FactAttribute : System.Attribute { }
    public class TheoryAttribute : FactAttribute { }
    public class SkippableFactAttribute : FactAttribute { }
    public interface IClassFixture<TFixture> where TFixture : class { }
    public interface ICollectionFixture<TFixture> where TFixture : class { }
    public interface IAsyncLifetime
    {
        System.Threading.Tasks.Task InitializeAsync();
        System.Threading.Tasks.Task DisposeAsync();
    }
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class CollectionAttribute : System.Attribute
    {
        public CollectionAttribute(string name) { }
        public CollectionAttribute(System.Type type) { }
    }
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class CollectionDefinitionAttribute : System.Attribute
    {
        public CollectionDefinitionAttribute(string name) { }
    }
    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    public class AssemblyFixtureAttribute : System.Attribute, Xunit.v3.IAssemblyFixtureAttribute
    {
        public AssemblyFixtureAttribute(System.Type assemblyFixtureType) { }
    }
}

namespace Xunit.v3
{
    public interface IFactAttribute { }
    public interface IAssemblyFixtureAttribute { }
}

namespace NUnit.Framework.Interfaces
{
    public interface ITestBuilder { }
    public interface ISimpleTestBuilder { }
}

namespace NUnit.Framework
{
    public abstract class NUnitAttribute : System.Attribute { }
    public class TestAttribute : NUnitAttribute, NUnit.Framework.Interfaces.ISimpleTestBuilder { }
    public class TestCaseAttribute : NUnitAttribute, NUnit.Framework.Interfaces.ITestBuilder
    {
        public TestCaseAttribute(params object[] arguments) { }
    }
    public abstract class CombiningStrategyAttribute : NUnitAttribute, NUnit.Framework.Interfaces.ITestBuilder { }
    public class TheoryAttribute : CombiningStrategyAttribute { }
    public class TestFixtureAttribute : NUnitAttribute { }
    public class SetUpFixtureAttribute : NUnitAttribute { }
    public class SetUpAttribute : NUnitAttribute { }
    public class TearDownAttribute : NUnitAttribute { }
    public class OneTimeSetUpAttribute : NUnitAttribute { }
    public class OneTimeTearDownAttribute : NUnitAttribute { }
}

namespace Microsoft.VisualStudio.TestTools.UnitTesting
{
    public class TestClassAttribute : System.Attribute { }
    public class TestMethodAttribute : System.Attribute { }
    public class DataTestMethodAttribute : TestMethodAttribute { }
    public class TestInitializeAttribute : System.Attribute { }
    public class TestCleanupAttribute : System.Attribute { }
    public class ClassInitializeAttribute : System.Attribute { }
    public class ClassCleanupAttribute : System.Attribute { }
    public class AssemblyInitializeAttribute : System.Attribute { }
    public class AssemblyCleanupAttribute : System.Attribute { }
    public class GlobalTestInitializeAttribute : System.Attribute { }
    public class GlobalTestCleanupAttribute : System.Attribute { }
}

namespace TUnit.Core
{
    public enum HookType { Test, Class, Assembly, TestSession, TestDiscovery }
    public abstract class TUnitAttribute : System.Attribute { }
    public abstract class BaseTestAttribute : TUnitAttribute { }
    public class TestAttribute : BaseTestAttribute { }
    public class HookAttribute : TUnitAttribute { }
    public class BeforeAttribute : HookAttribute { public BeforeAttribute(HookType hookType, string file = """", int line = 0) { } }
    public class AfterAttribute : HookAttribute { public AfterAttribute(HookType hookType, string file = """", int line = 0) { } }
    public class BeforeEveryAttribute : HookAttribute { public BeforeEveryAttribute(HookType hookType, string file = """", int line = 0) { } }
    public class AfterEveryAttribute : HookAttribute { public AfterEveryAttribute(HookType hookType, string file = """", int line = 0) { } }
}

namespace Reqnroll
{
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false)]
    public class BindingAttribute : System.Attribute { }
    public abstract class StepDefinitionBaseAttribute : System.Attribute { }
    public class GivenAttribute : StepDefinitionBaseAttribute { public GivenAttribute(string regex) { } }
    public class WhenAttribute : StepDefinitionBaseAttribute { public WhenAttribute(string regex) { } }
    public abstract class HookAttribute : System.Attribute { }
    public class BeforeScenarioAttribute : HookAttribute { }
    public class BeforeTestRunAttribute : HookAttribute { }
}

namespace TechTalk.SpecFlow
{
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false)]
    public class BindingAttribute : System.Attribute { }
    public abstract class StepDefinitionBaseAttribute : System.Attribute { }
    public class GivenAttribute : StepDefinitionBaseAttribute { public GivenAttribute(string regex) { } }
    public abstract class HookAttribute : System.Attribute { }
    public class AfterScenarioAttribute : HookAttribute { }
}
";
}
