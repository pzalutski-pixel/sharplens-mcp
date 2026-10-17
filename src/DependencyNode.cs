namespace SharpLensMcp;

// One node of a dependency graph (DependencyGraph): Id is unique within the graph and, for
// a project graph, a valid Mermaid node id; Name is what the node stands for (a project's
// plain name, a namespace); FilePath is a project's file as the tools report paths (null
// for a namespace or an in-memory project); TargetFrameworks lists a multi-targeted
// project's frameworks (empty otherwise).
internal sealed record DependencyNode(string Id, string Name, string? FilePath, IReadOnlyList<string> TargetFrameworks);
