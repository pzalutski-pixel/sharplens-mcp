namespace SharpLensMcp.Tests.Fixtures;

// Drives get_complexity_metrics with methods of exactly-known shape so the tests
// assert fixed metric values instead of `>= N` / type-only checks against the
// live, evolving RoslynService source. (The metric ALGORITHM itself is locked
// exactly in EstimateCyclomaticAndSortTests; this fixture pins the tool's
// end-to-end output on stable inputs.)
public class ComplexityTarget
{
    // No decision points at all: cyclomatic 1, nesting 0, cognitive 0.
    public int Trivial(int a, int b) => a + b;

    // Four ifs plus one loop — five decision points — for a high, known cyclomatic.
    public int Branchy(int x)
    {
        var total = 0;
        if (x > 0) total += 1;
        if (x > 10) total += 1;
        if (x > 20) total += 1;
        if (x > 30) total += 1;
        for (int i = 0; i < x; i++)
        {
            total += i;
        }
        return total;
    }
}
