using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One call site of a method, as CallGraphSession.CallersAsync finds it: the member whose
// body contains the call (a lambda's or local function's containing member; for a field,
// property or event initializer, each constructor that runs it) and the edge of that body
// that reaches the method.
internal sealed record CallerSite(IMethodSymbol Caller, ResolvedCallEdge Edge);
