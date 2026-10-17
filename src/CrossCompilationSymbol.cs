using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The one rule every cross-compilation matcher must get right.
//
// An ISymbol is an instance scoped to a single Compilation. The same logical
// type or member surfaced from two different compilations is two distinct
// instances, so SymbolEqualityComparer reports them unequal. Three sources of
// such pairs:
//   - a generator-augmented compilation vs the workspace solution;
//   - a git-baseline compilation vs the current one;
//   - RETARGETING: whenever a project's references differ from those of a project
//     it references (a package version skew, or a newer target framework), its
//     compilation sees the referenced project through a retargeting assembly, so a
//     symbol bound inside the consuming project's code is a different instance from
//     the same declaration in the declaring project's own compilation. Any
//     multi-project solution can have this; no generators are involved.
// Comparison across compilations must instead go through a stable STRING
// identity computed from the OriginalDefinition.
//
// Three string forms of a symbol, of which SolutionKey is THE identity:
//   - SolutionKey — the cross-compilation identity of the solution. It equates what
//     SymbolEqualityComparer would equate if both instances came from one compilation,
//     tuple element names aside. A source declaration is its ReferenceId qualified by
//     its assembly name (two projects may each declare N.Program, and those stay
//     distinct, while the per-target-framework compilations of one multi-targeted
//     project share an assembly name, so their declarations coincide); a metadata
//     declaration is its ReferenceId alone (the same BCL type surfaces under different
//     facade assemblies across target frameworks); a constructed symbol appends its
//     type arguments' keys (List<int>.Add and List<string>.Add stay distinct). Null
//     for a symbol only ever bound inside its declaring project — a local function,
//     lambda or anonymous type, or a member of one — and for error types; for those,
//     symbol equality is the identity, so the per-target-framework compilations of a
//     multi-targeted project each bind their own, distinct instance of such a symbol.
//     CrossCompilationSymbolComparer applies this form, falling back to symbol equality
//     where it is null.
//   - ReferenceId — the documentation-comment reference id of the OriginalDefinition
//     (e.g. "M:N.T.M(System.Int32)"). A lossy rendering: it does not tell apart
//     same-named declarations of two assemblies, the same-named local functions of
//     one type (each "M:N.T.Local"), or the lambdas of one type (all "M:N.T."), and
//     every anonymous type reads "T:". Null for a symbol without a documentation id.
//   - TypeKey — the OriginalDefinition display string of a type. Never null, so an
//     unresolved or error type still yields a string; lossy across assemblies the
//     same way.
//
// The rule: every site that pools symbols surfaced by several compilations into one
// set or dictionary (call-graph nodes, the type-dependency graph, coverage nodes),
// de-duplicates per-compilation results (a multi-targeted project loads as one
// compilation per target framework), or matches a symbol bound in one compilation
// against one bound in another keys on SolutionKey through
// CrossCompilationSymbolComparer — never on a second hand-rolled rule such as a bare
// ReferenceId or a display string. ReachabilityKey, the coverage walk's node identity,
// is this rule applied to a symbol's OriginalDefinition.
//
// diff_api_surface deliberately uses none of these: it keys on
// DocumentationCommentId.CreateDeclarationId with return-type-aware handling, because
// an API diff must classify a return-type change as CHANGED rather than treat it as
// the same identity. That concern is local to RoslynService.ApiSurface.
internal static class CrossCompilationSymbol
{
    // The documentation-comment reference id of the symbol's definition (a lossy
    // rendering; see the header). Null when the symbol has no documentation-comment id.
    public static string? ReferenceId(ISymbol symbol) =>
        symbol.OriginalDefinition.GetDocumentationCommentId();

    // True when the candidate's definition has the target's pre-computed ReferenceId.
    // A null target id, or a candidate with no id of its own, is not a match.
    public static bool SameReference(ISymbol candidate, string? targetReferenceId) =>
        targetReferenceId != null
        && candidate.OriginalDefinition.GetDocumentationCommentId() is { } candidateId
        && candidateId == targetReferenceId;

    // The display string of the type's definition (a lossy rendering; see the header).
    // Never null.
    public static string TypeKey(ITypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString();

    // The cross-compilation identity of a symbol within one solution. Null when the
    // symbol, or a type argument constructing it, is compilation-local or an error type.
    public static string? SolutionKey(ISymbol symbol) => symbol switch
    {
        IErrorTypeSymbol => null,
        ITypeParameterSymbol typeParameter => TypeParameterKey(typeParameter),
        IArrayTypeSymbol array => SolutionKey(array.ElementType) is { } elementKey
            ? $"{elementKey}[{array.Rank}]"
            : null,
        IPointerTypeSymbol pointer => SolutionKey(pointer.PointedAtType) is { } pointedAtKey
            ? $"{pointedAtKey}*"
            : null,
        INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol =>
            ConstructedKey(symbol),
        _ => null
    };

    // A type parameter is its declaring method's or type's declaration key plus its
    // ordinal, so the T of two different generic declarations stay distinct.
    private static string? TypeParameterKey(ITypeParameterSymbol typeParameter)
    {
        ISymbol? owner = (ISymbol?)typeParameter.DeclaringMethod ?? typeParameter.DeclaringType;
        return owner != null && DeclarationKey(owner) is { } ownerKey
            ? $"{ownerKey}!{typeParameter.Ordinal}"
            : null;
    }

    // The declaration key followed by the keys of the type arguments that construct
    // the symbol. A definition's type arguments are its own type parameters, so a
    // definition and a construction over its own type parameters share a key.
    private static string? ConstructedKey(ISymbol symbol)
    {
        if (DeclarationKey(symbol) is not { } declarationKey) return null;
        var typeArguments = TypeArgumentChain(symbol);
        if (typeArguments.Count == 0) return declarationKey;

        var argumentKeys = new string[typeArguments.Count];
        for (var i = 0; i < typeArguments.Count; i++)
        {
            if (SolutionKey(typeArguments[i]) is not { } argumentKey) return null;
            argumentKeys[i] = argumentKey;
        }
        return $"{declarationKey}{{{string.Join(",", argumentKeys)}}}";
    }

    // The definition's ReferenceId, qualified by its assembly name when the
    // definition is declared in source. Null for a compilation-local definition.
    private static string? DeclarationKey(ISymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        if (IsCompilationLocal(definition)) return null;
        if (definition.GetDocumentationCommentId() is not { } referenceId) return null;
        return definition.Locations.Any(l => l.IsInSource)
            ? $"{definition.ContainingAssembly?.Name}|{referenceId}"
            : referenceId;
    }

    // Declarations only ever bound inside the project that declares them: a local
    // function or lambda (bound only in its containing body) and an anonymous type
    // with its members (each assembly synthesizes its own).
    private static bool IsCompilationLocal(ISymbol definition) =>
        definition is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }
        || definition is INamedTypeSymbol { IsAnonymousType: true }
        || definition.ContainingType is { IsAnonymousType: true };

    // The type arguments that construct the symbol: a generic method's own, then
    // those of the symbol itself (when it is a type) and of each containing type,
    // innermost first.
    private static List<ITypeSymbol> TypeArgumentChain(ISymbol symbol)
    {
        var arguments = new List<ITypeSymbol>();
        if (symbol is IMethodSymbol method) arguments.AddRange(method.TypeArguments);
        for (var type = symbol as INamedTypeSymbol ?? symbol.ContainingType; type != null; type = type.ContainingType)
        {
            arguments.AddRange(type.TypeArguments);
        }
        return arguments;
    }
}
