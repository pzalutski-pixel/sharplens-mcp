namespace SharpLensMcp;

// What a runner-invoked entry point of the coverage walk is.
internal enum TestEntryPointKind
{
    // A test method (xUnit, NUnit, MSTest, TUnit); its class's lifecycle code runs with it.
    Test,
    // A BDD step binding (Reqnroll, SpecFlow), invoked when a scenario step matches it.
    StepBinding,
    // A hook the runner invokes outside any one test: BDD hooks, and assembly-, run- or
    // namespace-scoped set-up/tear-down (MSTest AssemblyInitialize, an NUnit SetUpFixture,
    // TUnit Before(Assembly) or BeforeEvery, an xUnit v3 assembly fixture).
    Hook
}
