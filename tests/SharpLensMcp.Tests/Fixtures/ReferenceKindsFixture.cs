using System;

namespace SharpLensMcp.Tests.Fixtures;

// Drives the find_references kind classifier. The field `TrackedField` is used
// in every kind the classifier must distinguish: write, invocation, typeof,
// nameof, attribute argument, and plain read.

[AttributeUsage(AttributeTargets.All)]
public sealed class TrackedAttribute : Attribute
{
    public string FieldName { get; }
    public TrackedAttribute(string fieldName) { FieldName = fieldName; }
}

public class TrackedTarget
{
    public Func<int> TrackedField = () => 0;

    [Tracked(nameof(TrackedField))]   // nameof + attribute argument
    public void Use()
    {
        TrackedField = () => 7;       // write
        var value = TrackedField();    // invocation (and read of field)
        var t = typeof(TrackedTarget); // typeof — not a reference to TrackedField
        var read = TrackedField;       // read
        _ = value;
        _ = t;
        _ = read;
    }
}
