namespace SharpLensMcp.Tests.Fixtures;

// An expression-bodied property and indexer (no accessor list) of known branch count, so
// get_complexity_metrics' property scope can be asserted at fixed values through the wire.
public class ExpressionBodiedComplexityTarget
{
    private readonly int[] _values = new int[8];

    // One ?: branch → cyclomatic 2.
    public int Sign => _values[0] > 0 ? 1 : -1;

    // One ?: branch, one parameter → cyclomatic 2, parameters 1.
    public int this[int i] => i > 0 ? _values[i] : 0;
}
