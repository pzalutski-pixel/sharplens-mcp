namespace SharpLensMcp.Tests.Fixtures;

// Target for extract_method's apply path. The ExtractTarget method's body has
// three statements; the test selects the middle two (`var partial = ...; var total = ...;`)
// and asserts the apply path writes the extracted method into this file AND
// replaces the selected statements with the call expression.
//
// ExtractMethodApplyTests snapshots this file in InitializeAsync and restores
// it in DisposeAsync so the apply test doesn't leave the working tree dirty.

public class ExtractMethodApplyTarget
{
    public int ExtractTarget(int a, int b, int c)
    {
        var partial = a + b;
        var total = partial + c;
        return total;
    }
}
