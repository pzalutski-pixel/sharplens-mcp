using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// The outcome of resolving typeName(+memberName, +overloadIndex)
// (RoslynService.ResolveMemberByNameAsync): the symbol or a structured error, plus the
// type resolution it went through, whose ShadowNotice a tool surfaces when the type
// name also matched framework types.
internal sealed record MemberResolution(ISymbol? Symbol, object? Error, TypeResolution? Type);
