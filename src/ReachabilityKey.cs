using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// Identity of a symbol in the coverage walk (find_tests / find_untested_code), stable
// across compilations. A test project whose references differ from the production
// project's sees that project through a retargeting assembly, so the method a test body
// binds to is a distinct instance from the production compilation's own symbol for the
// same declaration; SymbolEqualityComparer reports them unequal.
//
// The key applies the solution's one cross-compilation identity rule to the symbol's
// OriginalDefinition: CrossCompilationSymbol.SolutionKey, falling back to
// SymbolEqualityComparer for a definition without one (a local function, lambda or
// anonymous type, which only its declaring project binds, or an error type). Two
// symbols therefore share a ReachabilityKey exactly when CrossCompilationSymbolComparer
// equates their definitions; see CrossCompilationSymbol for the rule.
internal readonly struct ReachabilityKey : IEquatable<ReachabilityKey>
{
    private readonly string? _solutionKey;
    private readonly ISymbol? _fallback;

    private ReachabilityKey(string? solutionKey, ISymbol? fallback)
    {
        _solutionKey = solutionKey;
        _fallback = fallback;
    }

    public static ReachabilityKey For(ISymbol symbol)
    {
        var definition = symbol.OriginalDefinition;
        return CrossCompilationSymbol.SolutionKey(definition) is { } solutionKey
            ? new ReachabilityKey(solutionKey, null)
            : new ReachabilityKey(null, definition);
    }

    public bool Equals(ReachabilityKey other) =>
        _solutionKey != null || other._solutionKey != null
            ? string.Equals(_solutionKey, other._solutionKey, StringComparison.Ordinal)
            : SymbolEqualityComparer.Default.Equals(_fallback, other._fallback);

    public override bool Equals(object? obj) => obj is ReachabilityKey other && Equals(other);

    public override int GetHashCode() =>
        _solutionKey != null
            ? StringComparer.Ordinal.GetHashCode(_solutionKey)
            : SymbolEqualityComparer.Default.GetHashCode(_fallback);

    public override string ToString() => _solutionKey ?? _fallback?.ToDisplayString() ?? "";
}
