using System;

namespace SharpLensMcp.Tests.Fixtures;

// Targets for get_super_method: an override that also implicitly implements an
// interface member, a pure override, a plain method, an implicit-only
// implementer, an explicit interface implementation, a property override, an
// event implementation, and an override of a metadata (BCL) base.
public interface ISuperContract
{
    int Handle(int value);
    void ExplicitOnly();
    event Action Fired;
}

public abstract class SuperBase
{
    public abstract int Handle(int value);
    public virtual string Describe() => "base";
    public virtual int Score => 0;
}

public class SuperDerived : SuperBase, ISuperContract
{
    // Overrides SuperBase.Handle AND implicitly implements ISuperContract.Handle.
    public override int Handle(int value) => value;

    // Pure override — no interface involvement.
    public override string Describe() => "derived";

    // Property override of SuperBase.Score.
    public override int Score => 5;

    // Override of a METADATA base — object.ToString — so the super is inMetadata.
    public override string ToString() => "derived";

    // Explicit interface implementation.
    void ISuperContract.ExplicitOnly() { }

    // Implicitly implements the interface event.
    public event Action Fired = delegate { };

    // Plain method — neither overrides nor implements.
    public int PlainMethod() => 0;
}

public class SimpleImpl : ISuperContract
{
    // Implicit implementations, no overrides.
    public int Handle(int value) => value;
    public void ExplicitOnly() { }
    public event Action Fired = delegate { };
}
