using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One member a body invokes or touches, as CallEdgeCollector reports it.
//  - Target: the member that carries the body that runs, as an OriginalDefinition (a
//    reduced extension method's static definition, a partial member's implementation
//    part, an accessor rather than its property when one runs).
//  - ViaDispatch: the reference is a virtual or interface dispatch, so the member that
//    actually runs may be any override or implementation of Target.
//  - InMemberInitializer: the edge is in a field, property or event initializer that the
//    constructor being collected runs, not in the constructor's own body.
//  - Syntax: the syntax of the operation that makes the call (CallEdgeCollector.SiteLocation
//    gives the location a tool reports for it).
//  - ReceiverType: the static type of the receiver the member is reached through (the
//    instance expression, the extended value of an extension call, the collection of a
//    foreach); null when there is none (a static member, a constructor).
internal readonly record struct CallEdge(
    ISymbol Target,
    CallEdgeKind Kind,
    bool ViaDispatch,
    bool InMemberInitializer,
    SyntaxNode Syntax,
    ITypeSymbol? ReceiverType);
