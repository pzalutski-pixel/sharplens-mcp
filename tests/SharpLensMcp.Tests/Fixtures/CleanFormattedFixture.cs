namespace SharpLensMcp.Tests.Fixtures;

// Already correctly formatted: format_document_batch must report no changes for
// this file. Locks suppression-operator spacing ("null!") and intentional blank
// lines between members against formatter regressions — the previous engine
// (NormalizeWhitespace) rewrote both.
public class CleanFormattedTarget
{
    private string? _name;

    public int Count { get; set; }

    public void Reset()
    {
        _name = null!;
    }

    public string Describe()
    {
        return _name ?? "unset";
    }
}
