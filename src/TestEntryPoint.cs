using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One runner-invoked entry point: the method the runner calls (an xUnit v3 assembly
// fixture's entry is its constructor), what kind of entry point it is, the project it
// lives in, and the roots the coverage walk starts from for it: the method itself plus
// the lifecycle code that runs with it (for a test, its class's constructors,
// initializers, set-up/tear-down, fixtures and disposal; for an instance binding or hook,
// its class's construction and disposal).
internal sealed record TestEntryPoint(
    IMethodSymbol Method,
    TestEntryPointKind Kind,
    string ProjectName,
    IReadOnlyList<IMethodSymbol> Roots)
{
    // The kind as find_tests reports it.
    public string KindName => Kind switch
    {
        TestEntryPointKind.Test => "test",
        TestEntryPointKind.StepBinding => "stepBinding",
        _ => "hook"
    };
}
