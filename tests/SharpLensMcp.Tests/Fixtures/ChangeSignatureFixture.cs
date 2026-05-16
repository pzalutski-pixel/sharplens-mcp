namespace SharpLensMcp.Tests.Fixtures;

// Targets for change_signature tests. Covers every supported declaration kind
// (method, constructor, local function) and every supported call-site shape
// (plain invocation, `new Foo(...)`, `: this(...)`, `: base(...)`, `new(...)`,
// positional and named arguments).

public class ChangeSignatureBase
{
    public ChangeSignatureBase(int seed) { Seed = seed; }
    public int Seed { get; }
}

public class ChangeSignatureMethodHolder
{
    // 5 call sites, mix of positional + named.
    public int Concat(int first, int second, int third)
    {
        return first * 100 + second * 10 + third;
    }

    public int InvokeMethodAllPositional()
    {
        return Concat(1, 2, 3);
    }

    public int InvokeMethodMixed()
    {
        return Concat(first: 4, second: 5, third: 6) + Concat(7, 8, 9);
    }

    public int InvokeMethodNamedOnly()
    {
        return Concat(third: 30, first: 10, second: 20);
    }

    public int InvokeMethodFromAnotherClass()
    {
        var other = new ChangeSignatureMethodHolder();
        return other.Concat(0, 0, 1);
    }
}

public class ChangeSignatureCtorHolder : ChangeSignatureBase
{
    public string Label { get; }

    // Primary ctor — change_signature on this must update the three call sites below.
    public ChangeSignatureCtorHolder(int seed, string label) : base(seed)
    {
        Label = label;
    }

    public ChangeSignatureCtorHolder(int seed) : this(seed, "default") { }

    public static ChangeSignatureCtorHolder Make(int seed) => new ChangeSignatureCtorHolder(seed, "made");
}

public class ChangeSignatureLocalFunctionHolder
{
    public int Total()
    {
        int Combine(int a, int b) => a + b;

        return Combine(1, 2) + Combine(3, 4);
    }
}
