namespace SharpLensMcp.Tests.Fixtures;

// Targets for name-based addressing tests: a uniquely-named method (with a
// call site, so a rename preview reports real changes) whose rename-by-name
// must match the positional rename on the same symbol, and an overloaded
// method pair driving the ambiguity / overloadIndex resolution paths.
public class NameAddressingTarget
{
    public int UniqueComputeValue(int seed) => seed * 2;

    public int CallUniqueComputeValue() => UniqueComputeValue(21);

    public string Describe() => "plain";

    public string Describe(int count) => "count:" + count;
}
