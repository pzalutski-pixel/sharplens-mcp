using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// A name add_missing_imports looks for, as the compiler would bind it once a using is
// added: a type name of a given arity (an attribute name also as its
// "Attribute"-suffixed type, which must derive from System.Attribute), or an extension
// method callable on one of the receiver types it is invoked on (Receivers, collected
// from every unresolved call).
internal sealed class UnresolvedName
{
    public UnresolvedName(string name, int arity, bool isAttribute, bool isExtension)
    {
        Name = name;
        Arity = arity;
        IsAttribute = isAttribute;
        IsExtension = isExtension;
    }

    public string Name { get; }
    public int Arity { get; }
    public bool IsAttribute { get; }
    public bool IsExtension { get; }
    public List<ITypeSymbol> Receivers { get; } = new();
}
