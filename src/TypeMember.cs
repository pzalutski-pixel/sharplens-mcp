using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One entry of a type's member listing (UserVisibleMembers.Of): the member, its kind,
// whether it is inherited rather than declared by the listed type, and whether it is an
// explicit interface implementation — reachable only through the interface, so it is not
// a member of the type's own surface and a listing that reports public members must not
// count it as one.
internal sealed record TypeMember(ISymbol Symbol, MemberKind Kind, bool IsInherited, bool IsExplicitInterfaceImplementation);
