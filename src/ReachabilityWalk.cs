namespace SharpLensMcp;

// The result of one TestReachabilityGraph walk: every declaration reached (including
// seeded override/interface declarations), the members a reached body referenced through
// virtual or interface dispatch, and whether the walk stopped because it reached its
// target.
internal sealed record ReachabilityWalk(
    IReadOnlySet<ReachabilityKey> Reached,
    IReadOnlySet<ReachabilityKey> Dispatched,
    bool ReachedTarget);
