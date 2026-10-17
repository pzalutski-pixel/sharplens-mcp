namespace SharpLensMcp;

// The edges CallEdgeCollector found for one method, the throw sites of the bodies it
// walked, and whether it bound any syntax to find them (false for a member with no
// source, or a compiler-synthesized constructor with no initializers to run).
internal sealed record CallEdgeResult(IReadOnlyList<CallEdge> Edges, IReadOnlyList<ThrowSite> Throws, bool BodyBound);
