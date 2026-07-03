namespace SharpLensMcp;

// Typed result of find_god_objects, composed by GetProjectHealthAsync.
internal sealed record GodObjectsData(
    int TotalCount,
    IReadOnlyList<GodObjectCandidate> Candidates);
