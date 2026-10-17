namespace SharpLensMcp;

// The cycles of a dependency graph (DependencyCycles.Analyze).
//   - Cycles: elementary cycles, each closed (its first node repeated at the end) and
//     starting at its ordinally smallest node, ordered by length and then node sequence.
//   - Truncated: the graph has more elementary cycles than the cap; Cycles then holds the
//     first ones the search found (start nodes in order, neighbors in order), ordered as
//     above.
//   - StronglyConnectedComponents: every set of mutually dependent nodes (two or more
//     nodes, or one node depending on itself), each sorted ordinally, ordered by its first
//     node — always complete, whatever the cap.
internal sealed record CycleAnalysis(
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    bool Truncated,
    IReadOnlyList<IReadOnlyList<string>> StronglyConnectedComponents);
