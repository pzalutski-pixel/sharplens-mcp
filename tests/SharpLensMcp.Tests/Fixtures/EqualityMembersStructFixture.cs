namespace SharpLensMcp.Tests.Fixtures;

// Target for generate_equality_members on a STRUCT. A value type's generated
// equality must take the strongly-typed Equals parameter non-nullable (not
// Nullable<T>) and must not use ReferenceEquals — both are meaningless on a
// struct. Two fields so the member comparison and hash are non-trivial.
public struct EqualityMembersStructTarget
{
    public int Amount;
    public string Currency;
}
