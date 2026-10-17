using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One member's body as CallGraphSession walked it: the methods it calls (dispatch fan-out
// included, each call site once across the target frameworks of its project), the throw
// sites it contains, and whether any syntax was bound (false for a member with no body
// in the solution).
internal sealed record WalkedBody(
    IMethodSymbol Carrier,
    IReadOnlyList<ResolvedCallEdge> Callees,
    IReadOnlyList<ThrowSite> Throws,
    bool Bound);
