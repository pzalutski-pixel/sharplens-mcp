namespace SharpLensMcp.Tests.Fixtures;

// Fixture for AnalyzeControlFlow/AnalyzeDataFlow tests. FilterAndSum provides a
// method body where the selected region contains both a `break` and a `continue`
// — needed to lock down that ControlFlow.ExitPoints surfaces both statement
// kinds, not just returns. The other methods provide region-clamping targets.
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

    // For region clamping — an initializer spanning three lines. A selection of
    // just the middle line must resolve to this one declaration statement, not
    // the whole method body.
    public int CombineAcrossLines(int x, int y)
    {
        var combined =
            x * 2 +
            y;
        return combined;
    }

    // For region clamping inside a switch section, whose statement list lives on
    // the section rather than on a block.
    public string DescribeCode(int code)
    {
        switch (code)
        {
            case 1:
                var label = "one";
                var suffix = "!";
                return label + suffix;
            default:
                return "other";
        }
    }
}
