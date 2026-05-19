namespace SharpLensMcp.Tests.Fixtures;

// Target for add_null_checks apply-path snapshot test. The method takes a
// reference parameter (string) — add_null_checks will emit
// `ArgumentNullException.ThrowIfNull(input);` at the top of the body.
public class AddNullChecksTarget
{
    public string Process(string input)
    {
        return input.ToUpperInvariant();
    }
}
