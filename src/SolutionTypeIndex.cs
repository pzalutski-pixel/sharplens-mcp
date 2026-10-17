using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The solution's source types as the call tools need them to follow dispatch: for every
// virtual, abstract or interface member, the in-solution overrides and implementations of
// it and the type each one is resolved for; every type's supertypes; and every class's
// direct subclasses. Built once per tool call from every project compilation; all keys
// are the solution's cross-compilation identity (CrossCompilationSymbol.SolutionKey), so
// the per-target-framework compilations of a multi-targeted project and retargeted views
// of a referenced project collapse onto one declaration.
//
// DispatchTargets answers "what may run behind this call" receiver-type aware: a call
// bound to member M through a receiver of static type R runs R's own implementation of M
// or the implementation in a type derived from R (a class, or a type implementing the
// interface R); a sealed class or a struct receiver runs only its own. A receiver of an
// unrelated type, or a sealed type that does not override M, therefore fans out to
// nothing.
internal sealed class SolutionTypeIndex
{
    // member key -> (implementation, key of the type it is resolved for)
    private readonly Dictionary<string, List<(IMethodSymbol Implementation, string TypeKey)>> _implementations = new(StringComparer.Ordinal);
    // type key -> keys of the type, its base types and all its interfaces
    private readonly Dictionary<string, HashSet<string>> _supertypes = new(StringComparer.Ordinal);
    // class key -> its source classes whose direct base type it is
    private readonly Dictionary<string, List<INamedTypeSymbol>> _directSubclasses = new(StringComparer.Ordinal);
    // implementation key -> the members it overrides or implements for some type
    private readonly Dictionary<string, List<IMethodSymbol>> _dispatchedThrough = new(StringComparer.Ordinal);
    private readonly HashSet<string> _indexedTypes = new(StringComparer.Ordinal);

    public static SolutionTypeIndex Build(IEnumerable<Compilation> compilations, CancellationToken cancellationToken)
    {
        var index = new SolutionTypeIndex();
        foreach (var compilation in compilations)
        {
            foreach (var type in SourceTypes(compilation.Assembly.GlobalNamespace))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index.Add(type);
            }
        }
        return index;
    }

    private static IEnumerable<INamedTypeSymbol> SourceTypes(INamespaceSymbol root)
    {
        var namespaces = new Stack<INamespaceSymbol>();
        namespaces.Push(root);
        while (namespaces.Count > 0)
        {
            var current = namespaces.Pop();
            foreach (var child in current.GetNamespaceMembers()) namespaces.Push(child);
            var types = new Stack<INamedTypeSymbol>(current.GetTypeMembers());
            while (types.Count > 0)
            {
                var type = types.Pop();
                foreach (var nested in type.GetTypeMembers()) types.Push(nested);
                if (type.Locations.Any(l => l.IsInSource)) yield return type;
            }
        }
    }

    private void Add(INamedTypeSymbol type)
    {
        if (CrossCompilationSymbol.SolutionKey(type.OriginalDefinition) is not { } typeKey) return;
        if (!_indexedTypes.Add(typeKey)) return;

        var supertypes = new HashSet<string>(StringComparer.Ordinal) { typeKey };
        for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (Key(baseType) is { } baseKey) supertypes.Add(baseKey);
        }
        foreach (var iface in type.AllInterfaces)
        {
            if (Key(iface) is { } interfaceKey) supertypes.Add(interfaceKey);
        }
        _supertypes[typeKey] = supertypes;

        if (type.BaseType is { } directBase && Key(directBase) is { } directBaseKey)
        {
            if (!_directSubclasses.TryGetValue(directBaseKey, out var subclasses)) _directSubclasses[directBaseKey] = subclasses = new();
            subclasses.Add(type);
        }

        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            for (var overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
            {
                AddImplementation(overridden, method, typeKey);
            }
        }
        if (type.TypeKind == TypeKind.Interface) return;
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.IsStatic || !(member.IsAbstract || member.IsVirtual)) continue;
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation
                    && implementation.Locations.Any(l => l.IsInSource))
                {
                    AddImplementation(member, implementation, typeKey);
                }
            }
        }
    }

    private void AddImplementation(IMethodSymbol member, IMethodSymbol implementation, string typeKey)
    {
        if (Key(member) is not { } memberKey) return;
        var carrier = (IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(implementation);
        if (!_implementations.TryGetValue(memberKey, out var implementations)) _implementations[memberKey] = implementations = new();
        implementations.Add((carrier, typeKey));

        if (Key(carrier) is not { } carrierKey) return;
        if (!_dispatchedThrough.TryGetValue(carrierKey, out var members)) _dispatchedThrough[carrierKey] = members = new();
        if (!members.Contains(member.OriginalDefinition, CrossCompilationSymbolComparer.Instance)) members.Add(member.OriginalDefinition);
    }

    // The members a call can be bound to and dispatch to `implementation`: every member
    // it overrides and every interface member it implements for some source type (an
    // inherited implementation included).
    public IReadOnlyList<IMethodSymbol> DispatchedThrough(IMethodSymbol implementation) =>
        Key(CallEdgeCollector.ResolveBodyCarrier(implementation)) is { } key && _dispatchedThrough.TryGetValue(key, out var members)
            ? members
            : Array.Empty<IMethodSymbol>();

    private static string? Key(ISymbol symbol) => CrossCompilationSymbol.SolutionKey(symbol.OriginalDefinition);

    // The in-solution members that may run for a call bound to `target` through a receiver
    // of static type `receiverType` (null when the call has no receiver expression, which
    // dispatches over the target's own type). Excludes the target itself; empty for a
    // member that does not dispatch.
    public IReadOnlyList<IMethodSymbol> DispatchTargets(IMethodSymbol target, ITypeSymbol? receiverType)
    {
        var definition = target.OriginalDefinition;
        if (!CallEdgeCollector.IsDispatchable(definition) || Key(definition) is not { } targetKey) return Array.Empty<IMethodSymbol>();

        var receiver = receiverType as INamedTypeSymbol ?? definition.ContainingType;
        var receiverKey = Key(receiver);
        var results = new List<IMethodSymbol>();
        var seen = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance) { definition };
        void Add(IMethodSymbol implementation)
        {
            if (seen.Add(implementation)) results.Add(implementation);
        }

        if (receiver.TypeKind != TypeKind.Interface && ResolveFor(receiver, definition) is { } own
            && own.Locations.Any(l => l.IsInSource))
        {
            Add((IMethodSymbol)CallEdgeCollector.ResolveBodyCarrier(own));
        }
        if (receiver.IsSealed || receiver.IsValueType || receiverKey == null) return results;

        if (_implementations.TryGetValue(targetKey, out var implementations))
        {
            foreach (var (implementation, typeKey) in implementations)
            {
                if (typeKey != receiverKey && _supertypes.TryGetValue(typeKey, out var supertypes) && supertypes.Contains(receiverKey))
                {
                    Add(implementation);
                }
            }
        }
        return results;
    }

    // The member a class or struct runs for `member`: its implementation of an interface
    // member, or the most derived override of a virtual member in its base chain.
    private static IMethodSymbol? ResolveFor(INamedTypeSymbol type, IMethodSymbol member)
    {
        if (member.ContainingType.TypeKind == TypeKind.Interface)
        {
            var boundInterfaceMember = type.AllInterfaces
                .Where(i => CrossCompilationSymbolComparer.Instance.Equals(i.OriginalDefinition, member.ContainingType.OriginalDefinition))
                .SelectMany(i => i.GetMembers(member.Name).OfType<IMethodSymbol>())
                .FirstOrDefault(m => CrossCompilationSymbolComparer.Instance.Equals(m.OriginalDefinition, member));
            return boundInterfaceMember == null ? null : type.FindImplementationForInterfaceMember(boundInterfaceMember) as IMethodSymbol;
        }
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(member.Name).OfType<IMethodSymbol>())
            {
                if (CrossCompilationSymbolComparer.Instance.Equals(candidate.OriginalDefinition, member)) return candidate;
                for (var overridden = candidate.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                {
                    if (CrossCompilationSymbolComparer.Instance.Equals(overridden.OriginalDefinition, member)) return candidate;
                }
            }
        }
        return null;
    }

    // The source classes whose direct base type is `type`.
    public IReadOnlyList<INamedTypeSymbol> DirectSubclasses(INamedTypeSymbol type) =>
        Key(type) is { } key && _directSubclasses.TryGetValue(key, out var subclasses)
            ? subclasses
            : Array.Empty<INamedTypeSymbol>();
}
