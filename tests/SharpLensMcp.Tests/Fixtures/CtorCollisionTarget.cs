namespace SharpLensMcp.Tests.Fixtures;

// Source for generate_constructor where a stripped field name and a property
// camel-case to the same parameter name (_value → value, Value → value). The
// generated constructor must give them distinct parameter names or it won't
// compile.
public class CtorCollisionTarget
{
    private readonly int _value = 0;
    public int Value { get; set; }

    public int Sum() => _value + Value;
}
