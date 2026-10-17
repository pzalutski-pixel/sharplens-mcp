using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// One throw statement or throw expression in a body CallEdgeCollector walked.
//  - ExceptionType: the static type of the thrown expression (before its implicit
//    conversion to System.Exception); null for a bare `throw;`.
//  - Syntax: the throw statement or expression.
//  - RethrownFrom: the catch clause whose caught exception the throw rethrows - the
//    nearest enclosing catch of a bare `throw;`, or the catch that declares e in
//    `throw e;` - so what is thrown is whatever that catch caught, not a new exception of
//    ExceptionType. Null for any other throw.
internal readonly record struct ThrowSite(ITypeSymbol? ExceptionType, SyntaxNode Syntax, SyntaxNode? RethrownFrom)
{
    public bool IsRethrow => RethrownFrom != null;
}
