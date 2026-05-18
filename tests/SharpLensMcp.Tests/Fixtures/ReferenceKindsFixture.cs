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

    // A const used directly as an attribute argument (no typeof/nameof/cast/invocation
    // wrap) so the kind classifier walks up to AttributeSyntax FIRST and returns
    // "attribute" — the otherwise-unreachable branch.
    public const string TrackedMarker = "marker";

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

    // Separate method whose attribute argument is TrackedMarker (a const) directly —
    // no nameof/typeof/cast/invocation wrapper, so the classifier returns "attribute".
    // TrackedAttribute lacks AllowMultiple, so we can't stack two on Use().
    [Tracked(TrackedMarker)]
    public void UseMarker() { }
}
