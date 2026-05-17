namespace SharpLensMcp.Tests.Fixtures;

// Fixture for AnalyzeControlFlow tests. Provides a method body where the
// selected region contains both a `break` and a `continue` — needed to lock
// down that ControlFlow.ExitPoints surfaces both statement kinds, not just
// returns.
public class ControlFlowFixtureTarget
{
    public int FilterAndSum(int[] values, int threshold)
    {
        var total = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] < 0)
            {
                continue;  // skip negatives
            }
            if (values[i] > threshold)
            {
                break;     // stop at first overshoot
            }
            total += values[i];
        }
        return total;
    }
}
