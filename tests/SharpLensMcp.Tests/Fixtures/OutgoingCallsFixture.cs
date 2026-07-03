namespace SharpLensMcp.Tests.Fixtures;

// Drives get_outgoing_calls' broadened reference collection: an unqualified
// property read carries no member-access node, yet must still surface as an
// outgoing call.
public class OutgoingCallsTarget
{
    public int Magnitude { get; } = 7;

    public int UsesUnqualifiedProperty() => Magnitude + 1;
}
