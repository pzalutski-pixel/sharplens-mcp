using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// get_super_method: navigate from an override or implementation to what it
// overrides / implements. Reports the full OverriddenX chain to its root plus
// every interface member this symbol satisfies (explicit and implicit). Works
// for methods, properties, and events via the matching symbol APIs.
public partial class RoslynService
{
    public async Task<object> GetSuperMethodAsync(
        string? filePath = null,
        int? line = null,
        int? column = null,
        string? typeName = null,
        string? memberName = null,
        int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName, overloadIndex,
            memberNameRequired: true, cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var (symbol, _, error) = await ResolveSymbolAtAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                $"'{symbol.ToDisplayString()}' is not a method, property, or event",
                hint: "get_super_method navigates override/implementation relationships, which only members have.",
                context: new { actualKind = symbol.Kind.ToString() });
        }

        // The override chain, root-ward, each hop reported.
        var overrides = new List<object>();
        for (var overridden = GetOverriddenSymbol(symbol);
             overridden != null;
             overridden = GetOverriddenSymbol(overridden))
        {
            overrides.Add(DescribeSuperSymbol(overridden));
        }

        // Interface members this symbol satisfies: explicit first, then implicit
        // (deduplicated — an explicit implementation also satisfies the member).
        var implementsList = new List<object>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var explicitMember in GetExplicitInterfaceImplementations(symbol))
        {
            if (seen.Add(explicitMember))
            {
                implementsList.Add(DescribeSuperSymbol(explicitMember));
            }
        }

        var containingType = symbol.ContainingType;
        if (containingType != null)
        {
            foreach (var iface in containingType.AllInterfaces)
            {
                foreach (var interfaceMember in iface.GetMembers())
                {
                    var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                    if (implementation == null
                        || !SymbolEqualityComparer.Default.Equals(implementation, symbol)) continue;
                    if (seen.Add(interfaceMember))
                    {
                        implementsList.Add(DescribeSuperSymbol(interfaceMember));
                    }
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                symbol = symbol.ToDisplayString(),
                kind = symbol.Kind.ToString(),
                overrides,
                implements = implementsList
            },
            suggestedNextTools: overrides.Count > 0 || implementsList.Count > 0
                ? new[]
                {
                    "get_method_source on a base/interface member to read its contract",
                    "get_documentation to see the inherited docs"
                }
                : new[] { "This member neither overrides nor implements anything" },
            totalCount: overrides.Count + implementsList.Count,
            returnedCount: overrides.Count + implementsList.Count);
    }

    private static ISymbol? GetOverriddenSymbol(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null
    };

    private static IEnumerable<ISymbol> GetExplicitInterfaceImplementations(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ExplicitInterfaceImplementations.Cast<ISymbol>(),
        IPropertySymbol property => property.ExplicitInterfaceImplementations.Cast<ISymbol>(),
        IEventSymbol @event => @event.ExplicitInterfaceImplementations.Cast<ISymbol>(),
        _ => Enumerable.Empty<ISymbol>()
    };

    private object DescribeSuperSymbol(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location != null)
        {
            var lineSpan = location.GetLineSpan();
            return new
            {
                signature = symbol.ToDisplayString(),
                containingType = symbol.ContainingType?.ToDisplayString(),
                location = new { filePath = FormatPath(lineSpan.Path), line = lineSpan.StartLinePosition.Line, column = lineSpan.StartLinePosition.Character },
                inMetadata = false
            };
        }
        return new
        {
            signature = symbol.ToDisplayString(),
            containingType = symbol.ContainingType?.ToDisplayString(),
            location = (object?)null,
            inMetadata = true
        };
    }
}
