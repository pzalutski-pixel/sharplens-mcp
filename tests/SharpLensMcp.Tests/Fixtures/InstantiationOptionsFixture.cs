using System;

namespace SharpLensMcp.Tests.Fixtures;

// Targets for get_instantiation_options branch tests.

// IDisposable implementer — exercises implementsIDisposable=true at
// Inspection.cs:1049 + the hint about using `using` (Inspection.cs:1056).
public sealed class DisposableTarget : IDisposable
{
    public DisposableTarget() { }
    public void Dispose() { }
}

// Class with static factory methods returning itself — exercises the
// factoryMethods detection at Inspection.cs:997-1012.
public class FactoryTarget
{
    private FactoryTarget() { }

    public static FactoryTarget Create() => new();
    public static FactoryTarget Build(int seed) => new();
}

// Class whose instances are produced by static methods on OTHER types
// matching the Create/Build/New prefix — exercises externalFactories
// detection at Inspection.cs:1015-1046.
public class ExternalFactoryTarget
{
    public ExternalFactoryTarget() { }
}

public static class ExternalFactoryHost
{
    public static ExternalFactoryTarget CreateTarget() => new();
    public static ExternalFactoryTarget BuildTarget() => new();
    public static ExternalFactoryTarget NewTarget() => new();
}
