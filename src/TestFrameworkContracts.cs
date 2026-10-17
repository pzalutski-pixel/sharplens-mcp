using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// How each test framework discovers the methods it runs, expressed over the attribute's
// type: its base-class chain and the interfaces it implements, matched by exact
// (namespace, name) so a look-alike in another namespace is never taken for a framework
// type. Each contract was checked against the frameworks' package metadata:
//  - xUnit v2 and v3: the attribute derives from Xunit.FactAttribute (TheoryAttribute,
//    Xunit.SkippableFact's SkippableFact/SkippableTheory and v3's CulturedFact do); in v3
//    the attribute implements Xunit.v3.IFactAttribute.
//  - NUnit: the attribute implements NUnit.Framework.Interfaces.ITestBuilder or
//    ISimpleTestBuilder (Test, TestCase, TestCaseSource, Theory, Combinatorial, ...).
//  - MSTest: the attribute derives from
//    Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute (DataTestMethod,
//    STATestMethod).
//  - TUnit: the attribute derives from TUnit.Core.BaseTestAttribute (TestAttribute).
//  - Reqnroll and SpecFlow: step bindings derive from StepDefinitionBaseAttribute
//    (Given/When/Then/StepDefinition) and hooks from HookAttribute, in the Reqnroll and
//    TechTalk.SpecFlow namespaces.
// Lifecycle methods that run as part of each test of their class (and of classes deriving
// from it): NUnit SetUp/TearDown/OneTimeSetUp/OneTimeTearDown, MSTest TestInitialize/
// TestCleanup/ClassInitialize/ClassCleanup, TUnit Before/After with HookType Test or
// Class. Lifecycle methods scoped wider than one class are hooks: MSTest
// AssemblyInitialize/AssemblyCleanup/GlobalTestInitialize/GlobalTestCleanup, TUnit
// Before/After with any other HookType and BeforeEvery/AfterEvery, and the NUnit
// lifecycle methods of a SetUpFixture class (which run for a whole namespace).
internal static class TestFrameworkContracts
{
    private const string MSTest = "Microsoft.VisualStudio.TestTools.UnitTesting";

    private static readonly (string ns, string name)[] TestAttributeBases =
    {
        ("Xunit", "FactAttribute"),
        (MSTest, "TestMethodAttribute"),
        ("TUnit.Core", "BaseTestAttribute"),
    };

    private static readonly (string ns, string name)[] TestAttributeInterfaces =
    {
        ("Xunit.v3", "IFactAttribute"),
        ("NUnit.Framework.Interfaces", "ITestBuilder"),
        ("NUnit.Framework.Interfaces", "ISimpleTestBuilder"),
    };

    private static readonly (string ns, string name)[] StepBindingAttributeBases =
    {
        ("Reqnroll", "StepDefinitionBaseAttribute"),
        ("TechTalk.SpecFlow", "StepDefinitionBaseAttribute"),
    };

    private static readonly (string ns, string name)[] HookAttributeBases =
    {
        ("Reqnroll", "HookAttribute"),
        ("TechTalk.SpecFlow", "HookAttribute"),
        (MSTest, "AssemblyInitializeAttribute"),
        (MSTest, "AssemblyCleanupAttribute"),
        (MSTest, "GlobalTestInitializeAttribute"),
        (MSTest, "GlobalTestCleanupAttribute"),
        ("TUnit.Core", "BeforeEveryAttribute"),
        ("TUnit.Core", "AfterEveryAttribute"),
    };

    private static readonly (string ns, string name)[] NUnitLifecycleAttributes =
    {
        ("NUnit.Framework", "SetUpAttribute"),
        ("NUnit.Framework", "TearDownAttribute"),
        ("NUnit.Framework", "OneTimeSetUpAttribute"),
        ("NUnit.Framework", "OneTimeTearDownAttribute"),
    };

    private static readonly (string ns, string name)[] MSTestLifecycleAttributes =
    {
        (MSTest, "TestInitializeAttribute"),
        (MSTest, "TestCleanupAttribute"),
        (MSTest, "ClassInitializeAttribute"),
        (MSTest, "ClassCleanupAttribute"),
    };

    // TUnit's Before/After take a HookType; Test (0) and Class (1) scope them to the class.
    private static readonly (string ns, string name)[] TUnitScopedHookAttributes =
    {
        ("TUnit.Core", "BeforeAttribute"),
        ("TUnit.Core", "AfterAttribute"),
    };

    private const int TUnitHookTypeClass = 1;

    // The kind of entry point a method is, or null when no framework runs it directly
    // (lifecycle methods scoped to one class are not entry points of their own).
    public static TestEntryPointKind? ClassifyEntryPoint(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary) return null;
        var attributes = method.GetAttributes();
        if (attributes.Any(a => a.AttributeClass is { } c
                && (DerivesFromAny(c, TestAttributeBases) || ImplementsAny(c, TestAttributeInterfaces))))
        {
            return TestEntryPointKind.Test;
        }
        if (attributes.Any(a => a.AttributeClass is { } c && DerivesFromAny(c, StepBindingAttributeBases)))
        {
            return TestEntryPointKind.StepBinding;
        }
        if (attributes.Any(a => a.AttributeClass is { } c && DerivesFromAny(c, HookAttributeBases))
            || attributes.Any(a => IsTUnitScopedHook(a) && !IsClassScopedTUnitHook(a))
            || (attributes.Any(a => a.AttributeClass is { } c && DerivesFromAny(c, NUnitLifecycleAttributes))
                && IsNUnitSetUpFixture(method.ContainingType)))
        {
            return TestEntryPointKind.Hook;
        }
        return null;
    }

    // Whether the method is set-up/tear-down that runs as part of each test of its class.
    public static bool IsClassLifecycleMethod(IMethodSymbol method)
    {
        if (method.MethodKind != MethodKind.Ordinary) return false;
        return method.GetAttributes().Any(a => a.AttributeClass is { } c
            && ((DerivesFromAny(c, NUnitLifecycleAttributes) && !IsNUnitSetUpFixture(method.ContainingType))
                || DerivesFromAny(c, MSTestLifecycleAttributes)
                || (IsTUnitScopedHook(a) && IsClassScopedTUnitHook(a))));
    }

    // Whether the type is, or derives from, one with the given (namespace, name).
    public static bool DerivesFrom(ITypeSymbol type, string ns, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (Is(current, ns, name)) return true;
        }
        return false;
    }

    // Whether the type has the given (namespace, name), ignoring generic arity.
    public static bool Is(ITypeSymbol type, string ns, string name) =>
        type.Name == name && type.ContainingNamespace?.ToDisplayString() == ns;

    private static bool DerivesFromAny(ITypeSymbol type, (string ns, string name)[] bases) =>
        bases.Any(b => DerivesFrom(type, b.ns, b.name));

    private static bool ImplementsAny(ITypeSymbol type, (string ns, string name)[] interfaces) =>
        type.AllInterfaces.Any(i => interfaces.Any(b => Is(i, b.ns, b.name)));

    private static bool IsTUnitScopedHook(AttributeData attribute) =>
        attribute.AttributeClass is { } c && DerivesFromAny(c, TUnitScopedHookAttributes);

    private static bool IsClassScopedTUnitHook(AttributeData attribute) =>
        attribute.ConstructorArguments.FirstOrDefault() is { Kind: TypedConstantKind.Enum, Value: { } hookType }
        && Convert.ToInt32(hookType) <= TUnitHookTypeClass;

    // [SetUpFixture] is inherited, so a class deriving from one is a set-up fixture too.
    private static bool IsNUnitSetUpFixture(INamedTypeSymbol? type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetAttributes().Any(a => a.AttributeClass is { } c
                    && DerivesFrom(c, "NUnit.Framework", "SetUpFixtureAttribute")))
            {
                return true;
            }
        }
        return false;
    }
}
