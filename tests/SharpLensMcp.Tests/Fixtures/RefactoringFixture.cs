using System;

namespace SharpLensMcp.Tests.Fixtures;

// Targets for extract_method / extract_variable / inline_variable / encapsulate_field tests.
// Each member here has a deterministic position so tests can pin to it.

public class RefactoringTarget
{
    // For encapsulate_field — the test will target this public field and assert that
    // the preview wraps it in a property accessor pair.
    public int BareCounter;

    // For extract_method — the body is a block of three statements that can be lifted.
    public int Sum(int a, int b, int c)
    {
        var partial = a + b;
        var total = partial + c;
        return total;
    }

    // For inline_variable — `temp` is used exactly once after its declaration.
    public string GreetingFor(string name)
    {
        var temp = "Hello, " + name;
        return temp;
    }

    // For extract_variable — the test selects the expression `a * 2 + 7`.
    public int Compute(int a)
    {
        return a * 2 + 7;
    }
}
