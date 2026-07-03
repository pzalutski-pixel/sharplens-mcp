namespace SharpLensMcp.Tests.Fixtures;

// Targets for get_exception_flow: a thrower, a caller that catches, a caller
// that lets the exception escape, a bare-rethrower, and a method whose own
// throw is absorbed locally.
public class ExceptionFlowTarget
{
    public void ThrowerA()
    {
        throw new InvalidOperationException("a");
    }

    public int CatcherB()
    {
        try
        {
            ThrowerA();
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
        return 0;
    }

    public void BareCallerC()
    {
        ThrowerA();
    }

    public void RethrowerD()
    {
        try
        {
            ThrowerA();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
    }

    public void LocalCatchE()
    {
        try
        {
            throw new ArgumentException("e");
        }
        catch (ArgumentException)
        {
        }
    }

    public void ConstructsThrowingCtorF()
    {
        var x = new ThrowingCtor();
    }

    public int ReadsThrowingPropertyG()
    {
        var t = new PropertyThrower();
        return t.Risky;
    }
}

// A constructor whose body throws — the exception must follow the `new` call.
public class ThrowingCtor
{
    public ThrowingCtor()
    {
        throw new InvalidOperationException("ctor");
    }
}

// A property whose getter throws — the exception must follow the read.
public class PropertyThrower
{
    public int Risky
    {
        get { throw new FormatException("getter"); }
    }
}
