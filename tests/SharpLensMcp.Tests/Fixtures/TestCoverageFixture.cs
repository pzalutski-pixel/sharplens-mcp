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

    // Production caller — not a test, doesn't help coverage.
    public string ProductionUser() => OnlyCalledByProduction();
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
}
