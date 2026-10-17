using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One by-name type resolution pass over a set of compilations (base, then
// generator-augmented). PartialMatches holds the source types whose name contains the
// query, set only when no tier matched exactly; ResolveTypeByNameAsync turns them into
// the TYPE_NOT_FOUND suggestions once every compilation set has been tried.
internal sealed record TypeResolutionPass(TypeResolution Resolution, List<INamedTypeSymbol> PartialMatches)
{
    public INamedTypeSymbol? Type => Resolution.Type;
    public object? Error => Resolution.Error;
}
