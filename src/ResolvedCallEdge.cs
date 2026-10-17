using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One method a body calls, as the call tools report it (CallGraphSession): a CallEdge
// whose target is a method, or one of the in-solution implementations or overrides that
// may run behind a dispatched CallEdge.
//  - Target: the member whose body runs (an OriginalDefinition, see
//    CallEdgeCollector.ResolveBodyCarrier).
//  - ViaDispatch: false for the member the call is bound to as written; true for an
//    implementation or override reached through interface or virtual dispatch from a
//    receiver whose static type can be an instance of the implementing type.
//  - Syntax: the operation syntax that makes the call; Site: the location reported for it
//    (CallEdgeCollector.SiteLocation).
internal sealed record ResolvedCallEdge(
    IMethodSymbol Target,
    CallEdgeKind Kind,
    bool ViaDispatch,
    SyntaxNode Syntax,
    Location Site);
