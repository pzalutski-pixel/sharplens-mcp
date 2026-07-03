namespace SharpLensMcp;

// One god-object candidate in a GodObjectsData result.
internal sealed record GodObjectCandidate(
    string TypeName,
    int EfferentCoupling,
    int AfferentCoupling,
    int MemberCount,
    double Score,
    object? Location);
