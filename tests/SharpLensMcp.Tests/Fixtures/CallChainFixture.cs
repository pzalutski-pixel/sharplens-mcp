namespace SharpLensMcp.Tests.Fixtures;

// Fixture for get_outgoing_calls maxDepth tests. A three-level call chain
// where each level is only reachable from the one above it: Level1 calls
// Level2, Level2 calls Level3, Level3 calls nothing. Depth-1 analysis on
// Level1 must surface Level2 alone; depth 2 adds Level3; depth 3 explores
// the full chain with nothing left truncated.
public class CallChainFixtureTarget
{
    public int Level1(int seed) => Level2(seed) + 1;

    private int Level2(int seed) => Level3(seed) * 2;

    private int Level3(int seed) => seed - 3;
}
