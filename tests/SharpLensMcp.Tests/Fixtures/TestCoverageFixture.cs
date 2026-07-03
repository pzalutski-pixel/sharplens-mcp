using Xunit;

namespace SharpLensMcp.Tests.Fixtures;

// Drives find_untested_code. The fixture deliberately mixes production-style
// methods (CoveredByTest, ChainedFromTest, CalledByChainedFromTest, NeverCalled)
// with a test method that reaches some but not others.

public class CoverageTarget
{
    public int CoveredByTest() => 1;
    public int ChainedFromTest() => CalledByChainedFromTest();
    public int CalledByChainedFromTest() => 99;
    public int NeverCalled() => 0;          // <- the canonical "untested" case
    public string OnlyCalledByProduction() => "prod";

    // Uncovered, with a known branch count for the cyclomatic-estimate lock:
    // 1 + if + && + ternary = 4 (the && and ternary must each count).
    public int NeverCalledComplex(bool p, bool q, int a)
    {
        if (p && q) return 1;
        return a > 0 ? a : -a;
    }

    // Production caller — not a test, doesn't help coverage.
    public string ProductionUser() => OnlyCalledByProduction();
}

// A base virtual reached only through a derived override, an interface member
// reached only through its implementation, and a method exercising a method-group
// reference plus a bare unqualified property read — the cases that read as
// untested unless reachability seeds through overrides/interfaces and the
// collector picks up method groups and bare reads.
public class CoverageBase
{
    public virtual int VirtualComputed() => 41;
}

public class CoverageDerived : CoverageBase
{
    public override int VirtualComputed() => base.VirtualComputed() + 1;
}

public interface ICoverageContract
{
    int ContractMethod();
}

public class CoverageImplementor : ICoverageContract
{
    public int PublicSeed { get; } = 5;

    public int ContractMethod() => 7;

    public int MethodGroupTarget() => 1;

    public int UsesMethodGroupAndProperty()
    {
        System.Func<int> f = MethodGroupTarget;   // method-group reference, no invocation node
        return f() + PublicSeed;                  // bare unqualified property read
    }
}

public class CoverageTargetTests
{
    [Fact]
    public void CoveredByTest_Test()
    {
        var t = new CoverageTarget();
        _ = t.CoveredByTest();
    }

    [Fact]
    public void ChainedFromTest_Test()
    {
        var t = new CoverageTarget();
        _ = t.ChainedFromTest();
    }

    [Fact]
    public void VirtualViaOverride_Test()
    {
        var d = new CoverageDerived();
        _ = d.VirtualComputed();          // resolves to the override; the base virtual is seeded
    }

    [Fact]
    public void ContractViaImpl_Test()
    {
        var impl = new CoverageImplementor();
        _ = impl.ContractMethod();        // resolves to the impl; the interface member is seeded
        _ = impl.UsesMethodGroupAndProperty();
    }
}
