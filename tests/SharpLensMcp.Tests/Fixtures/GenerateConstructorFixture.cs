namespace SharpLensMcp.Tests.Fixtures;

// Targets for generate_constructor branch tests.

// Class with regular get/set properties (not init-only) so includeProperties=true
// picks them up (SetMethod != null with non-private setter).
public class GenCtorWithProperties
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

// Class with a nullable annotated field to exercise the initializeToDefault=true branch.
public class GenCtorWithNullableField
{
    public string? OptionalName;
}

// Class with no instance fields and no settable properties — exercises the
// "No fields or properties found to initialize" AnalysisFailed branch.
public class GenCtorEmpty
{
    public void DoNothing() { }
}
