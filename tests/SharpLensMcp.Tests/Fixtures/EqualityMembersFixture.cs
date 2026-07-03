namespace SharpLensMcp.Tests.Fixtures;

// Target for generate_equality_members apply-path snapshot test.
// Has a single public field so the generator emits a deterministic
// Equals/GetHashCode pair hashing Value via the HashCode builder.
public class EqualityMembersTarget
{
    public int Value;
}
