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
        var t = typeof(TrackedTarget); // typeof reference to TrackedTarget type
        var read = TrackedField;       // read

        // Cast reference to TrackedTarget (drives "cast" classification).
        object boxed = this;
        var unboxed = (TrackedTarget)boxed;
        _ = unboxed;

        _ = value;
        _ = t;
        _ = read;
    }
}
