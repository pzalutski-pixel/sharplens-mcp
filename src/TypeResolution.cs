using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The outcome of resolving a by-name type reference (RoslynService.ResolveTypeByNameAsync).
// Exactly one of Type and Error is set when the name resolves or fails with a structured
// error (AMBIGUOUS_TYPE, or TYPE_NOT_FOUND listing partial matches); both are null when
// nothing matches at all. ShadowedMatches lists the fully-qualified framework types whose
// name matches as exactly as the source type that won — never silent, so a tool can tell
// the caller which other type the same name could have meant.
internal sealed record TypeResolution(
    string TypeName,
    INamedTypeSymbol? Type,
    object? Error,
    IReadOnlyList<string> ShadowedMatches)
{
    // The sentence a tool adds to its response when the resolved type shadows others,
    // or null when nothing is shadowed.
    public string? ShadowNotice => Type == null || ShadowedMatches.Count == 0
        ? null
        : $"'{TypeName}' resolved to {Type.ToDisplayString()}; also matches {string.Join(", ", ShadowedMatches)} — pass the fully-qualified name to target it.";
}
