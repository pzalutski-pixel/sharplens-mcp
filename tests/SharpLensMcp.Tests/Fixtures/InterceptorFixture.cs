namespace SharpLensMcp.Tests.Fixtures;

// Targets for find_interceptors. The attribute is declared locally (the real
// one is typically file-local or generator-emitted; the tool matches by
// simple name) and is NOT in a compiler-enabled interceptors namespace, so
// it compiles as an ordinary attribute - scan-only, no rerouting happens.
[System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
public sealed class InterceptsLocationAttribute : System.Attribute
{
    public InterceptsLocationAttribute(string filePath, int line, int character) { }
    public InterceptsLocationAttribute(int version, string data) { }
}

public class InterceptorHost
{
    public void Original() { }

    public void CallSite()
    {
        Original(); // intercepted-call marker: the legacy attribute below points here (1-based line 20)
    }
}

public static class InterceptorImpls
{
    [InterceptsLocation("tests/SharpLensMcp.Tests/Fixtures/InterceptorFixture.cs", 20, 9)]
    public static void InterceptOriginal(InterceptorHost host) { }

    [InterceptsLocation(1, "T0tally-0paque-Data==")]
    public static void InterceptViaData(InterceptorHost host) { }
}
