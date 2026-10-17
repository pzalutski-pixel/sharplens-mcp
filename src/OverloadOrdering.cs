using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The one ordering every overloadIndex refers to (see OverloadSet). A member name
// on a type denotes:
//   1. the members DECLARED on the type with that name: methods (user-defined
//      operators and conversions by their metadata name, e.g. op_Addition),
//      properties (indexers as "this[]"), fields, events and nested types;
//      constructors via ".ctor" or the type's simple name and the static constructor
//      via ".cctor"; and explicit interface implementations, matched by the
//      implemented member's name ("Draw"), the interface-qualified name
//      ("IShape.Draw") or their own name ("Lib.IShape.Draw"). Accessors (get_X,
//      add_E) are reached through their property or event and are not in the set.
//      Compiler-synthesized members (a struct's parameterless constructor, a record's
//      copy constructor and Equals) are members like any other;
//   2. when inherited members are requested, then the members of each base type
//      outward (each base interface, for an interface) that C# member lookup still
//      sees from the type: accessible from it, not overridden by or
//      signature-identical to (hidden by) a member already in the set, not a
//      non-method hidden by a member already in the set, and none at all once the
//      set holds a non-method (which hides every base member of the name).
//      Constructors and explicit interface implementations are not inherited.
// Within a level members are ordered by their earliest declaring location
// (SymbolParts.LocationOrder over the user-authored parts, or over every part when
// none is user-authored); compiler-synthesized members (which carry their type's
// location, not one of their own) and members without a source location follow in
// symbol-table order, so whether the compiler synthesizes a member never shifts the
// index of a declared one.
internal static class OverloadOrdering
{
    public const string ConstructorName = ".ctor";
    public const string StaticConstructorName = ".cctor";

    // The members memberName denotes on type, declared first then (with
    // includeInherited) inherited. isUserSource identifies the solution's own
    // documents so a generator-emitted part never decides a member's position;
    // null treats every source tree as user-authored.
    public static OverloadSet MembersNamed(
        INamedTypeSymbol type, string memberName, bool includeInherited,
        Func<SyntaxTree, bool>? isUserSource = null)
    {
        var declared = Order(DeclaredMembersNamed(type, memberName), isUserSource);
        var members = new List<ISymbol>(declared);
        if (includeInherited && !IsConstructorName(type, memberName))
        {
            foreach (var level in InheritanceLevels(type))
            {
                if (members.Any(m => m is not IMethodSymbol && !IsExplicitImplementation(m))) break;
                var visible = level.GetMembers(memberName)
                    .Where(m => IsAddressable(m)
                             && !IsExplicitImplementation(m)
                             && IsAccessibleFrom(m, type)
                             && !IsHiddenBy(m, members))
                    .ToList();
                members.AddRange(Order(visible, isUserSource));
            }
        }
        return new OverloadSet(members, declared.Count);
    }

    // The set a member belongs to: its constructors for a constructor, the set its
    // implemented member's name denotes for an explicit interface implementation,
    // the set its name denotes on its containing type otherwise. A local function, a
    // lambda or an accessor cannot be overloaded and forms a set of its own.
    public static OverloadSet OverloadsOf(
        ISymbol member, bool includeInherited, Func<SyntaxTree, bool>? isUserSource = null)
    {
        var canonical = SymbolParts.Canonical(member);
        if (canonical.ContainingType is not { } containingType) return Single(canonical);

        switch (canonical)
        {
            case IMethodSymbol
            {
                MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction
                    or MethodKind.PropertyGet or MethodKind.PropertySet
                    or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
            }:
                return Single(canonical);
            case IMethodSymbol { MethodKind: MethodKind.Constructor }:
                return MembersNamed(containingType, ConstructorName, includeInherited: false, isUserSource);
        }

        var implemented = ExplicitlyImplemented(canonical);
        var name = implemented.IsEmpty ? canonical.Name : implemented[0].Name;
        return MembersNamed(containingType, name, includeInherited, isUserSource);
    }

    private static OverloadSet Single(ISymbol symbol) => new(new[] { symbol }, 1);

    private static bool IsConstructorName(INamedTypeSymbol type, string memberName) =>
        memberName == ConstructorName || memberName == type.Name;

    private static IReadOnlyList<ISymbol> DeclaredMembersNamed(INamedTypeSymbol type, string memberName)
    {
        if (IsConstructorName(type, memberName)) return type.InstanceConstructors.Cast<ISymbol>().ToList();
        if (memberName == StaticConstructorName) return type.StaticConstructors.Cast<ISymbol>().ToList();

        var explicitImplementations = type.GetMembers()
            .Where(m => ExplicitlyImplemented(m).Any(i => ImplementedNameMatches(i, memberName)));
        return type.GetMembers(memberName)
            .Concat(explicitImplementations)
            .Where(IsAddressable)
            .Distinct(SymbolEqualityComparer.Default)
            .ToList();
    }

    private static bool ImplementedNameMatches(ISymbol implemented, string memberName) =>
        implemented.Name == memberName
        || $"{implemented.ContainingType.Name}.{implemented.Name}" == memberName
        || $"{implemented.ContainingType.ToDisplayString()}.{implemented.Name}" == memberName;

    private static ImmutableArray<ISymbol> ExplicitlyImplemented(ISymbol member) => member switch
    {
        IMethodSymbol method => ImmutableArray<ISymbol>.CastUp(method.ExplicitInterfaceImplementations),
        IPropertySymbol property => ImmutableArray<ISymbol>.CastUp(property.ExplicitInterfaceImplementations),
        IEventSymbol @event => ImmutableArray<ISymbol>.CastUp(@event.ExplicitInterfaceImplementations),
        _ => ImmutableArray<ISymbol>.Empty
    };

    private static bool IsExplicitImplementation(ISymbol member) => !ExplicitlyImplemented(member).IsEmpty;

    private static bool IsAddressable(ISymbol member) => member is not IMethodSymbol
    {
        MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet
            or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
    };

    // The types whose members the type inherits, nearest first.
    private static IEnumerable<INamedTypeSymbol> InheritanceLevels(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Interface)
        {
            foreach (var baseInterface in type.AllInterfaces) yield return baseInterface;
            yield break;
        }
        for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            yield return baseType;
    }

    private static bool IsAccessibleFrom(ISymbol member, INamedTypeSymbol type) => member.DeclaredAccessibility switch
    {
        Accessibility.Private => false,
        Accessibility.Internal or Accessibility.ProtectedAndInternal =>
            SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, type.ContainingAssembly)
            || member.ContainingAssembly.GivesAccessTo(type.ContainingAssembly),
        _ => true
    };

    // Whether a base member is hidden from lookup by the members already in the set:
    // any member hides a base non-method of the name; a base method is hidden by a
    // member that overrides it or has the identical signature. Explicit interface
    // implementations hide nothing.
    private static bool IsHiddenBy(ISymbol baseMember, IReadOnlyList<ISymbol> members)
    {
        var hiders = members.Where(m => !IsExplicitImplementation(m)).ToList();
        if (hiders.Count == 0) return false;
        if (baseMember is not IMethodSymbol baseMethod) return true;
        return hiders.OfType<IMethodSymbol>().Any(m => Overrides(m, baseMethod) || SameSignature(m, baseMethod));
    }

    private static bool Overrides(IMethodSymbol method, IMethodSymbol baseMethod)
    {
        for (var overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, baseMethod.OriginalDefinition))
                return true;
        }
        return false;
    }

    // Same arity, parameter count, parameter ref kinds and parameter types; a
    // method type parameter matches the one at the same position.
    private static bool SameSignature(IMethodSymbol a, IMethodSymbol b)
    {
        if (a.Arity != b.Arity || a.Parameters.Length != b.Parameters.Length) return false;
        for (var i = 0; i < a.Parameters.Length; i++)
        {
            var pa = a.Parameters[i];
            var pb = b.Parameters[i];
            if (pa.RefKind != pb.RefKind) return false;
            var sameType = pa.Type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } ta
                && pb.Type is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } tb
                    ? ta.Ordinal == tb.Ordinal
                    : SymbolEqualityComparer.Default.Equals(pa.Type, pb.Type);
            if (!sameType) return false;
        }
        return true;
    }

    private static IReadOnlyList<ISymbol> Order(IReadOnlyList<ISymbol> members, Func<SyntaxTree, bool>? isUserSource) =>
        members
            .Select((member, index) => (
                member,
                index,
                location: member.IsImplicitlyDeclared ? null : EarliestLocation(member, isUserSource)))
            .OrderBy(x => x.location, SymbolParts.LocationOrder)
            .ThenBy(x => x.index)
            .Select(x => x.member)
            .ToList();

    private static Location? EarliestLocation(ISymbol member, Func<SyntaxTree, bool>? isUserSource)
    {
        var locations = SymbolParts.SourceLocations(member);
        return (isUserSource == null ? null : locations.FirstOrDefault(l => isUserSource(l.SourceTree!)))
            ?? locations.FirstOrDefault();
    }
}
