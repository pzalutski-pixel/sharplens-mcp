namespace SharpLensMcp;

// A dependency graph as dependency_graph and find_circular_dependencies report it: the
// nodes in their graph's order (projects in logical-project order, namespaces ordinally)
// and the edges ordered by their From node, then their To node, in that same order.
internal sealed record DependencyGraph(IReadOnlyList<DependencyNode> Nodes, IReadOnlyList<DependencyEdge> Edges)
{
    // The graph as adjacency lists keyed by node id, every node present, for cycle
    // analysis (DependencyCycles).
    public Dictionary<string, List<string>> ToAdjacency()
    {
        var adjacency = Nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var edge in Edges) adjacency[edge.From].Add(edge.To);
        return adjacency;
    }
}
