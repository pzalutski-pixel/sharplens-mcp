using System.Text;

namespace SharpLensMcp.Tests.Fixtures;

// Deliberately dead code for remove_unused_code. The host class and its live
// method are referenced from UnusedRemovalToolTests so only the two dead
// helpers (and the ghost class) are reported. The second dead helper is the
// file's ONLY user of System.Text, so removing it makes the using directive
// newly unnecessary. The dead member names appear nowhere else in this file —
// the tests assert the rewritten content via NotContain on those names.
public class UnusedRemovalHost
{
    public string LiveMethod() => "live";

    private int DeadHelperOne() => 1;

    private string DeadHelperTwo(int seed)
    {
        var builder = new StringBuilder();
        builder.Append(seed);
        return builder.ToString();
    }
}

public class UnusedRemovalGhost
{
    public void Phantom() { }
}
