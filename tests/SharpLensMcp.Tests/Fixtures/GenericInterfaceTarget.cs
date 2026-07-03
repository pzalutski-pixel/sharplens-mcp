namespace SharpLensMcp.Tests.Fixtures;

// Source for extract_interface on a GENERIC type. The extracted interface must
// declare the type parameter T and carry its `class` constraint, or the members
// (which reference T) cannot compile.
public class GenericInterfaceTarget<T> where T : class
{
    public T Value { get; set; } = default!;
    public T Get() => Value;
    public void Set(T value) => Value = value;
}
