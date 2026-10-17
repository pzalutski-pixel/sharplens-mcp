using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The ordered members one name denotes on a type, as OverloadOrdering builds them.
// Members[i] is what overloadIndex i means in every tool. The first DeclaredCount
// entries are declared on the type itself; any after them are inherited (present
// only when the set was built with inherited members), so a declared member keeps
// its index whether or not a tool includes inherited ones.
internal sealed record OverloadSet(IReadOnlyList<ISymbol> Members, int DeclaredCount)
{
    public int Count => Members.Count;

    public bool IsInherited(int index) => index >= DeclaredCount;

    // The index of the member a symbol denotes — any spelling of it: a constructed
    // generic, a reduced extension method, either part of a partial member — or -1.
    public int IndexOf(ISymbol symbol)
    {
        for (var i = 0; i < Members.Count; i++)
        {
            if (SymbolParts.SameMember(Members[i], symbol)) return i;
        }
        return -1;
    }
}
