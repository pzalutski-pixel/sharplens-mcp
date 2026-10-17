namespace SharpLensMcp;

// One edge of a dependency graph (DependencyGraph): the node From depends on the node To,
// both by DependencyNode.Id.
internal sealed record DependencyEdge(string From, string To);
