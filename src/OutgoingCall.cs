using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One entry of an outgoing-call list (get_outgoing_calls, analyze_method): a method a body
// calls, how (Kind, ViaDispatch, as ResolvedCallEdge), the call site that first reached
// it, and the hop it was found at (1 for a direct call).
internal sealed record OutgoingCall(IMethodSymbol Target, CallEdgeKind Kind, bool ViaDispatch, Location Site, int Depth);
