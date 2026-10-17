using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One declaration in a TestReachabilityGraph: the first symbol instance seen for its
// ReachabilityKey (always an OriginalDefinition), its key, and its containing type's key.
// The graph fills the memoized fields lazily, once per call.
internal sealed class ReachableNode
{
    public ReachableNode(ISymbol symbol, ReachabilityKey key, ReachabilityKey? containingTypeKey)
    {
        Symbol = symbol;
        Key = key;
        ContainingTypeKey = containingTypeKey;
    }

    public ISymbol Symbol { get; }
    public ReachabilityKey Key { get; }
    public ReachabilityKey? ContainingTypeKey { get; }

    // The nodes reaching this one marks reached: itself, an accessor's property or event,
    // and the override/interface declarations it seeds.
    internal IReadOnlyList<ReachableNode>? Covers { get; set; }

    // The base members it overrides and the interface members it implements.
    internal IReadOnlyList<ReachableNode>? RelatedDeclarations { get; set; }

    // What its body references; null until the body is bound.
    internal IReadOnlyList<ReachableCallee>? Callees { get; set; }
}
