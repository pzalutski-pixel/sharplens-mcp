using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The one rule every cross-compilation matcher must get right.
//
// An ISymbol is an instance scoped to a single Compilation. The same logical
// type or member surfaced from two different compilations — a generator-augmented
// compilation vs the workspace solution, or a git-baseline compilation vs the
// current one — is two distinct instances, so SymbolEqualityComparer reports them
// unequal. Comparison across compilations must instead go through a stable STRING
// identity computed from the OriginalDefinition.
//
// Two canonical forms, picked by what is being compared:
//   - ReferenceId — the documentation-comment reference id (e.g. "M:N.T.M(System.Int32)").
//     Identifies any symbol, so it is the form for matching an arbitrary referenced
//     symbol (find_references / find_callers into generated code). Null for symbols
//     that have no doc id (type parameters, anonymous types).
//   - TypeKey — the OriginalDefinition display string. Used where only TYPES are
//     compared (instantiation / pattern / throw / catch searches): it is never null,
//     so an unresolved or error type still yields a comparable key.
//
// diff_api_surface deliberately uses NEITHER: it keys on
// DocumentationCommentId.CreateDeclarationId with return-type-aware handling, because
// an API diff must classify a return-type change as CHANGED rather than treat it as
// the same identity. That concern is local to RoslynService.ApiSurface.
internal static class CrossCompilationSymbol
{
    // Stable identity for matching an arbitrary symbol across compilations.
    // Null when the symbol has no documentation-comment id.
    public static string? ReferenceId(ISymbol symbol) =>
        symbol.OriginalDefinition.GetDocumentationCommentId();

    // True when the candidate denotes the same declaration as the target's
    // pre-computed ReferenceId. A null target id, or a candidate with no id of
    // its own, is not a match.
    public static bool SameReference(ISymbol candidate, string? targetReferenceId) =>
        targetReferenceId != null
        && candidate.OriginalDefinition.GetDocumentationCommentId() is { } candidateId
        && candidateId == targetReferenceId;

    // Stable type identity for matching types across compilations. Never null.
    public static string TypeKey(ITypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString();
}
