using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The lifecycle code a test runner runs around the entry points of one test assembly,
// which no call in the source reaches. Built per compilation from the assembly's own
// source types (collection definitions are looked up among them).
//  - Around each test of a class: the class's instance constructors (base constructors
//    and field/property initializers follow from them as call edges), its set-up/
//    tear-down methods and those of its base classes (TestFrameworkContracts), its
//    xUnit IAsyncLifetime / TUnit IAsyncInitializer / IDisposable / IAsyncDisposable
//    implementations, and the fixtures it receives: xUnit IClassFixture<T> on the class
//    and ICollectionFixture<T> / IClassFixture<T> on the collection definition named by
//    its [Collection], each constructed and initialized/disposed through the same
//    interfaces.
//  - Around an instance step binding or hook: its class's construction and disposal.
//  - Once for the assembly: each xUnit v3 assembly fixture ([assembly: AssemblyFixture]).
internal sealed class TestLifecycleResolver
{
    private static readonly (string ns, string name)[] LifecycleInterfaces =
    {
        ("Xunit", "IAsyncLifetime"),
        ("TUnit.Core.Interfaces", "IAsyncInitializer"),
        ("System", "IDisposable"),
        ("System", "IAsyncDisposable"),
    };

    private readonly IReadOnlyList<INamedTypeSymbol> _sourceTypes;
    private readonly Dictionary<INamedTypeSymbol, IReadOnlyList<IMethodSymbol>> _testRoots =
        new(SymbolEqualityComparer.Default);
    private Dictionary<string, List<INamedTypeSymbol>>? _collectionDefinitions;

    public TestLifecycleResolver(IReadOnlyList<INamedTypeSymbol> sourceTypes)
    {
        _sourceTypes = sourceTypes;
    }

    // The lifecycle roots that run with each test declared in `testClass`.
    public IReadOnlyList<IMethodSymbol> RootsForTestsOf(INamedTypeSymbol testClass)
    {
        if (_testRoots.TryGetValue(testClass, out var cached)) return cached;
        var roots = new List<IMethodSymbol>();
        roots.AddRange(ConstructionAndDisposal(testClass));
        for (var type = testClass; type != null; type = type.BaseType)
        {
            roots.AddRange(type.GetMembers().OfType<IMethodSymbol>().Where(TestFrameworkContracts.IsClassLifecycleMethod));
        }
        foreach (var fixture in ClassFixtures(testClass).Concat(CollectionFixtures(testClass)))
        {
            roots.AddRange(ConstructionAndDisposal(fixture));
        }
        _testRoots[testClass] = roots;
        return roots;
    }

    // The runner constructs a type and initializes/disposes it through its lifecycle
    // interfaces.
    public IReadOnlyList<IMethodSymbol> ConstructionAndDisposal(INamedTypeSymbol type)
    {
        var roots = new List<IMethodSymbol>();
        if (!type.IsStatic) roots.AddRange(type.InstanceConstructors);
        foreach (var iface in type.AllInterfaces)
        {
            if (!LifecycleInterfaces.Any(l => TestFrameworkContracts.Is(iface, l.ns, l.name))) continue;
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation)
                {
                    roots.Add(implementation);
                }
            }
        }
        return roots;
    }

    // Each xUnit v3 assembly fixture type declared in source, by [assembly: AssemblyFixture(typeof(T))]
    // or [assembly: AssemblyFixture<T>].
    public IEnumerable<INamedTypeSymbol> AssemblyFixtures(IAssemblySymbol assembly)
    {
        foreach (var attribute in assembly.GetAttributes())
        {
            if (attribute.AttributeClass is not { } attributeClass
                || !attributeClass.AllInterfaces.Any(i => TestFrameworkContracts.Is(i, "Xunit.v3", "IAssemblyFixtureAttribute")))
            {
                continue;
            }
            var fixture = attribute.ConstructorArguments.FirstOrDefault() is { Kind: TypedConstantKind.Type, Value: INamedTypeSymbol argument }
                ? argument
                : attributeClass.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
            if (fixture != null && fixture.Locations.Any(l => l.IsInSource)) yield return fixture;
        }
    }

    private static IEnumerable<INamedTypeSymbol> ClassFixtures(INamedTypeSymbol type) =>
        FixtureArguments(type, "IClassFixture");

    private static IEnumerable<INamedTypeSymbol> FixtureArguments(INamedTypeSymbol type, string interfaceName) =>
        type.AllInterfaces
            .Where(i => TestFrameworkContracts.Is(i, "Xunit", interfaceName) && i.TypeArguments.Length == 1)
            .Select(i => i.TypeArguments[0])
            .OfType<INamedTypeSymbol>();

    // The collection fixtures (and the class fixtures a collection definition declares for
    // every class in it) of the xUnit collection the test class belongs to. [Collection] is
    // inherited; it names the definition by string, by typeof, or by its generic argument.
    private IEnumerable<INamedTypeSymbol> CollectionFixtures(INamedTypeSymbol testClass)
    {
        foreach (var definition in CollectionDefinitionsOf(testClass))
        {
            foreach (var fixture in FixtureArguments(definition, "ICollectionFixture")) yield return fixture;
            foreach (var fixture in FixtureArguments(definition, "IClassFixture")) yield return fixture;
        }
    }

    private IEnumerable<INamedTypeSymbol> CollectionDefinitionsOf(INamedTypeSymbol testClass)
    {
        for (var type = testClass; type != null; type = type.BaseType)
        {
            var collection = type.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass is { } c && TestFrameworkContracts.Is(c, "Xunit", "CollectionAttribute"));
            if (collection == null) continue;

            if (collection.AttributeClass!.TypeArguments.FirstOrDefault() is INamedTypeSymbol genericDefinition)
            {
                return new[] { genericDefinition };
            }
            return collection.ConstructorArguments.FirstOrDefault() switch
            {
                { Kind: TypedConstantKind.Type, Value: INamedTypeSymbol definition } => new[] { definition },
                { Kind: TypedConstantKind.Primitive, Value: string name } =>
                    CollectionDefinitions().TryGetValue(name, out var named) ? named : Enumerable.Empty<INamedTypeSymbol>(),
                _ => Enumerable.Empty<INamedTypeSymbol>()
            };
        }
        return Enumerable.Empty<INamedTypeSymbol>();
    }

    // The assembly's [CollectionDefinition("name")] classes, by name.
    private Dictionary<string, List<INamedTypeSymbol>> CollectionDefinitions()
    {
        if (_collectionDefinitions != null) return _collectionDefinitions;
        _collectionDefinitions = new Dictionary<string, List<INamedTypeSymbol>>(StringComparer.Ordinal);
        foreach (var type in _sourceTypes)
        {
            foreach (var attribute in type.GetAttributes())
            {
                if (attribute.AttributeClass is { } c && TestFrameworkContracts.Is(c, "Xunit", "CollectionDefinitionAttribute")
                    && attribute.ConstructorArguments.FirstOrDefault() is { Value: string name })
                {
                    if (!_collectionDefinitions.TryGetValue(name, out var list))
                    {
                        _collectionDefinitions[name] = list = new List<INamedTypeSymbol>();
                    }
                    list.Add(type);
                }
            }
        }
        return _collectionDefinitions;
    }
}
