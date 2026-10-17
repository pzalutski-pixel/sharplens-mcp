using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The one rule for which members a tool lists as a type's members, and how inheritance
// extends the list.
//
// IsListed(member) — a member declared on its containing type is listed unless it is:
//   - implicitly declared (a default constructor, an auto-property backing field, a
//     record's synthesized Equals/GetHashCode/ToString/PrintMembers/EqualityContract/
//     Deconstruct/copy constructor/<Clone>$, a field-like event's accessors);
//   - compiler-generated in metadata, where IsImplicitlyDeclared is never set: a name
//     starting with '<' (backing fields, closure and state-machine types, <Clone>$) or a
//     [CompilerGenerated] attribute (a compiled record's synthesized members);
//   - not a listable member kind (MemberClassifier.Classify is null): property and event
//     accessors, which their property or event represents, and non-members;
//   - a metadata member visible only inside its own assembly (private, internal, private
//     protected), unless it is an explicit interface implementation;
//   - a member of a delegate type: its constructor, Invoke, BeginInvoke and EndInvoke are
//     provided by the runtime (implicitly declared in source, plain methods in
//     metadata), and the delegate's signature is a property of the type itself.
// A record's positional properties and primary constructor are declared by its
// parameter list, so they are listed.
//
// Of(type, includeInherited) — the listed members of the type in declaration order,
// then, with includeInherited, the members it inherits, nearest base first:
//   - a class, struct or record inherits from its base-class chain, stopping before the
//     framework roots every type of its kind derives from (System.Object,
//     System.ValueType, System.Enum, System.Delegate, System.MulticastDelegate); an
//     interface inherits from all its base interfaces; enums and delegates inherit nothing
//     listable;
//   - constructors, static constructors and finalizers are not inherited; private base
//     members are not inherited; an internal or private protected base member is
//     inherited only when its assembly grants the derived type's assembly access;
//     explicit interface implementations are not inherited;
//   - a base member hidden by a member already in the list, declared in a type that
//     derives from the member's base, is not inherited (members of two sibling base
//     interfaces never hide each other), by the C# hiding rule: a field, property, event or nested type hides every base member of
//     the same name; a method hides base non-methods of the same name and base methods
//     of the same signature (name, type-parameter count, parameter types and by-ref-ness);
//     an indexer hides base indexers of the same signature. An override is a method or
//     property of its base member's signature, so it hides (replaces) the member it
//     overrides.
internal static class UserVisibleMembers
{
    private static readonly HashSet<SpecialType> FrameworkRoots = new()
    {
        SpecialType.System_Object,
        SpecialType.System_ValueType,
        SpecialType.System_Enum,
        SpecialType.System_Delegate,
        SpecialType.System_MulticastDelegate
    };

    private const string CompilerGeneratedAttribute = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";

    public static bool IsListed(ISymbol member)
    {
        if (member.IsImplicitlyDeclared) return false;
        if (member.ContainingType is { TypeKind: TypeKind.Delegate }) return false;
        if (MemberClassifier.Classify(member) is null) return false;
        if (member.Name.StartsWith('<')) return false;
        if (member.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == CompilerGeneratedAttribute)) return false;

        var inSource = member.Locations.Any(l => l.IsInSource);
        if (!inSource
            && !IsExplicitInterfaceImplementation(member)
            && member.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal)
        {
            return false;
        }
        return true;
    }

    public static bool IsExplicitInterfaceImplementation(ISymbol member) => member switch
    {
        IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0,
        IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
        IEventSymbol @event => @event.ExplicitInterfaceImplementations.Length > 0,
        _ => false
    };

    public static IReadOnlyList<TypeMember> Of(INamedTypeSymbol type, bool includeInherited)
    {
        var listed = type.GetMembers().Where(IsListed).Select(m => Entry(m, isInherited: false)).ToList();
        if (!includeInherited) return listed;

        foreach (var baseType in InheritanceSources(type))
        {
            // Hiding is decided against everything listed from types that derive from this
            // base, so a base member hidden by a nearer base's member is dropped too. A
            // member of a sibling base interface (IAlpha.Do beside IBeta.Do under
            // ICombined : IAlpha, IBeta) does not derive from it and hides nothing: C# keeps
            // both, and access through the derived interface is ambiguous.
            var hiders = listed.Where(m => !m.IsExplicitInterfaceImplementation).Select(m => m.Symbol).ToList();
            foreach (var member in baseType.GetMembers())
            {
                if (!IsListed(member) || !IsInheritable(member, type)) continue;
                if (hiders.Any(hider => DerivesFrom(hider.ContainingType, baseType) && Hides(hider, member))) continue;
                listed.Add(Entry(member, isInherited: true));
            }
        }
        return listed;
    }

    private static TypeMember Entry(ISymbol member, bool isInherited) =>
        new(member, MemberClassifier.Classify(member)!.Value, isInherited, IsExplicitInterfaceImplementation(member));

    // The types whose members the type inherits, nearest first.
    private static IEnumerable<INamedTypeSymbol> InheritanceSources(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface) return type.AllInterfaces;
        if (type.TypeKind is TypeKind.Enum or TypeKind.Delegate) return Array.Empty<INamedTypeSymbol>();

        var chain = new List<INamedTypeSymbol>();
        for (var baseType = type.BaseType;
             baseType != null && !FrameworkRoots.Contains(baseType.SpecialType);
             baseType = baseType.BaseType)
        {
            chain.Add(baseType);
        }
        return chain;
    }

    private static bool IsInheritable(ISymbol member, INamedTypeSymbol derived)
    {
        if (member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor })
        {
            return false;
        }
        if (IsExplicitInterfaceImplementation(member)) return false;
        return member.DeclaredAccessibility switch
        {
            Accessibility.Private => false,
            Accessibility.Internal or Accessibility.ProtectedAndInternal =>
                member.ContainingAssembly is { } declaring
                && derived.ContainingAssembly is { } deriving
                && declaring.GivesAccessTo(deriving),
            _ => true
        };
    }

    // Whether `derived` inherits from `baseType`, through its base-class chain or its
    // interfaces.
    private static bool DerivesFrom(INamedTypeSymbol? derived, INamedTypeSymbol baseType)
    {
        if (derived == null) return false;
        var target = baseType.OriginalDefinition;
        for (var current = derived.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, target)) return true;
        }
        return derived.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, target));
    }

    // The C# hiding rule (see the header): whether `hider`, declared in a type that
    // derives from the base declaring `hidden`, hides it.
    private static bool Hides(ISymbol hider, ISymbol hidden)
    {
        if (hider is IMethodSymbol hiderMethod)
        {
            return hidden is IMethodSymbol hiddenMethod
                ? hiderMethod.Name == hiddenMethod.Name && SameSignature(hiderMethod, hiddenMethod)
                : hider.Name == hidden.Name;
        }
        if (hider is IPropertySymbol { IsIndexer: true } hiderIndexer)
        {
            return hidden is IPropertySymbol { IsIndexer: true } hiddenIndexer
                && SameParameters(hiderIndexer.Parameters, hiddenIndexer.Parameters);
        }
        // A field, non-indexer property, event or nested type hides every base member of
        // its name (an indexer is named "this[]" and never collides with one).
        return hider.Name == hidden.Name;
    }

    private static bool SameSignature(IMethodSymbol hider, IMethodSymbol hidden)
    {
        if (hider.Arity != hidden.Arity) return false;
        // Compare a generic method's parameters with the base method's type parameters
        // replaced by the hider's own, so M<T>(T) and M<U>(U) are one signature.
        var comparable = hidden.Arity > 0
            ? hidden.ConstructedFrom.Construct(hider.TypeParameters.Cast<ITypeSymbol>().ToArray())
            : hidden;
        return SameParameters(hider.Parameters, comparable.Parameters);
    }

    private static bool SameParameters(IReadOnlyList<IParameterSymbol> x, IReadOnlyList<IParameterSymbol> y) =>
        x.Count == y.Count
        && x.Zip(y).All(pair =>
            (pair.First.RefKind == RefKind.None) == (pair.Second.RefKind == RefKind.None)
            && SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type));
}
