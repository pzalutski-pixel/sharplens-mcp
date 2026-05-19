namespace SharpLensMcp.Tests.Fixtures;

// Target for generate_equality_members apply-path snapshot test.
// Has a single public field so the generator emits a deterministic
// Equals/GetHashCode pair using HashCode.Combine(Value).
public class EqualityMembersTarget
{
    public int Value;
}
