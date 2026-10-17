using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// Shared position and name resolution for every addressed tool. Centralizes the
// document/semantic-model/syntax-tree pipeline; validates the path and line/column
// with structured errors rather than exceptions; resolves a position to the symbol
// its own token denotes (TryFindSymbolForRename); and resolves a type and member
// name to a declared member through the canonical member ordering
// (OverloadOrdering), addressing only the solution's own documents.
public partial class RoslynService
{
    internal sealed record ResolvedPosition(
        Document Document,
        SemanticModel Model,
        SyntaxTree Tree,
        SourceText Text,
        int Position,
        SyntaxToken Token);

    // Resolves (filePath, line, column) to a document position. Returns either
    // a ResolvedPosition or a structured error response — never throws for
    // caller-input problems.
    internal async Task<(ResolvedPosition? position, object? error)> ResolvePositionAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsNameablePath(filePath))
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "filePath cannot name a file: it is empty or contains a character no path may hold (such as NUL)",
                hint: "Pass the document's path as an absolute path or relative to the solution directory.",
                context: new { filePath }));
        }
        var document = TryFindDocument(filePath);
        if (document == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }));
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics",
                context: new { filePath, line, column }));
        }

        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (syntaxTree == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree",
                context: new { filePath, line, column }));
        }

        var text = await syntaxTree.GetTextAsync(cancellationToken);
        if (line < 0 || line >= text.Lines.Count)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"line {line} is out of range: the file has {text.Lines.Count} lines (zero-based, so valid lines are 0..{text.Lines.Count - 1})",
                hint: "Coordinates are ZERO-based: editor line N is line N-1 here.",
                context: new { filePath, line, column, lineCount = text.Lines.Count }));
        }

        var textLine = text.Lines[line];
        var lineLength = textLine.End - textLine.Start;
        if (column < 0 || column > lineLength)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"column {column} is out of range on line {line}: the line is {lineLength} characters long",
                hint: "Coordinates are ZERO-based: editor column N is column N-1 here.",
                context: new { filePath, line, column, lineLength }));
        }

        var position = textLine.Start + column;
        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var token = root.FindToken(position);

        return (new ResolvedPosition(document, semanticModel, syntaxTree, text, position, token), null);
    }

    // Validates a zero-based inclusive [startLine, endLine] range against the file, so a
    // region-addressed tool returns a structured error instead of throwing when the line
    // numbers are out of range or inverted. The range counterpart to ResolvePositionAsync's
    // single-position guard: indexing text.Lines out of range throws ArgumentOutOfRangeException,
    // and TextSpan.FromBounds throws when start > end — both would otherwise escape as -32603.
    // Returns a structured error response, or null when the range is valid.
    internal object? ValidateLineRange(SourceText text, int startLine, int endLine)
    {
        var lineCount = text.Lines.Count;
        if (startLine < 0 || endLine < 0 || startLine >= lineCount || endLine >= lineCount || startLine > endLine)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid line range: startLine={startLine}, endLine={endLine} (file has {lineCount} lines)",
                hint: "startLine and endLine are 0-based and must satisfy 0 <= startLine <= endLine < lineCount.",
                context: new { startLine, endLine, lineCount });
        }
        return null;
    }

    // A resolved address. Symbol is the member a name-based address denotes and
    // TypeResolution the type resolution it went through, whose ShadowNotice a tool
    // surfaces (both null for positional addressing, where the position itself is the
    // input).
    internal sealed record ResolvedAddress(
        string FilePath, int Line, int Column, ISymbol? Symbol = null, TypeResolution? TypeResolution = null);

    // Whether a syntax tree is one of the solution's own documents — user-authored
    // source a position can address — rather than source-generator output.
    internal bool IsSolutionSourceTree(SyntaxTree tree) =>
        !string.IsNullOrEmpty(tree.FilePath) && TryFindDocument(tree.FilePath) != null;

    // The canonical member set a name denotes on a type (OverloadOrdering): what every
    // overloadIndex means. includeInherited appends the inherited members C# lookup
    // sees after the declared ones, which keep their indices.
    internal OverloadSet GetMembersByName(INamedTypeSymbol type, string memberName, bool includeInherited) =>
        OverloadOrdering.MembersNamed(type, memberName, includeInherited, IsSolutionSourceTree);

    // The canonical set a member belongs to — its overloads, constructors, or itself
    // alone for a local function — and so its overloadIndex (OverloadSet.IndexOf).
    internal OverloadSet GetOverloadSet(ISymbol member, bool includeInherited) =>
        OverloadOrdering.OverloadsOf(member, includeInherited, IsSolutionSourceTree);

    // The source location to show or address a symbol at: the body-carrying part of
    // a partial member, else its earliest part, always within the solution's own
    // documents (never generator output). Null when no part is in the solution.
    internal Location? GetPreferredSourceLocation(ISymbol symbol) =>
        SymbolParts.PreferredLocation(symbol, IsSolutionSourceTree);

    // Resolves typeName(+memberName, +overloadIndex) to the symbol it denotes: a member
    // DECLARED on the named type, indexed in the canonical OverloadOrdering order
    // (constructors by ".ctor" or the type's simple name, explicit interface
    // implementations by their implemented member's name). A type-level error
    // (AMBIGUOUS_TYPE, TYPE_NOT_FOUND with partial-match suggestions) propagates from
    // ResolveTypeByNameAsync; an overloaded member without
    // an explicit overloadIndex is refused with the candidate list rather than
    // silently picking the first overload — the result feeds refactorings that
    // rewrite the whole solution. A member the type only inherits is refused with
    // its declaring types, where it is addressable. This is ResolveMemberByNameAsync
    // without the type resolution.
    internal async Task<(ISymbol? symbol, object? error)> ResolveSymbolByNameAsync(
        string typeName, string? memberName = null, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveMemberByNameAsync(typeName, memberName, overloadIndex, cancellationToken);
        return (resolution.Symbol, resolution.Error);
    }

    // ResolveSymbolByNameAsync plus the TypeResolution the type name went through.
    // When the type name resolved to a source type that shadows same-named framework
    // types, every member-level error names them (hint + context.shadowedTypes), so a
    // "member not found" on the wrong Timer says which other Timer was meant.
    internal async Task<MemberResolution> ResolveMemberByNameAsync(
        string typeName, string? memberName, int? overloadIndex,
        CancellationToken cancellationToken = default)
    {
        var typeResolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        if (typeResolution.Error != null) return new MemberResolution(null, typeResolution.Error, typeResolution);
        if (typeResolution.Type is not { } type)
        {
            return new MemberResolution(null, CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName }), typeResolution);
        }

        var (symbol, error) = ResolveMember(type, typeResolution, typeName, memberName, overloadIndex);
        return new MemberResolution(symbol, error, typeResolution);
    }

    // A member-level error hint followed by the type resolution's shadow notice, when
    // there is one.
    private static string? WithShadowNotice(string? hint, TypeResolution typeResolution) =>
        typeResolution.ShadowNotice is not { } notice ? hint
        : string.IsNullOrEmpty(hint) ? notice
        : $"{hint.TrimEnd('.')}. {notice}";

    private (ISymbol? symbol, object? error) ResolveMember(
        INamedTypeSymbol type, TypeResolution typeResolution,
        string typeName, string? memberName, int? overloadIndex)
    {
        var shadowedTypes = typeResolution.ShadowedMatches;
        if (memberName == null)
        {
            if (overloadIndex != null)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    "overloadIndex requires memberName",
                    hint: WithShadowNotice("Supply the memberName the overloadIndex selects among, or drop overloadIndex to target the type itself.", typeResolution),
                    context: new { typeName, overloadIndex, shadowedTypes }));
            }
            return (type, null);
        }

        var members = GetMembersByName(type, memberName, includeInherited: false).Members;

        if (members.Count == 0)
        {
            var declaringTypes = GetMembersByName(type, memberName, includeInherited: true).Members
                .Select(m => m.ContainingType.ToDisplayString())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (declaringTypes.Count > 0)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.SymbolNotFound,
                    $"Member '{memberName}' is not declared in '{type.ToDisplayString()}'; it is inherited from '{string.Join("', '", declaringTypes)}'",
                    hint: WithShadowNotice($"Address the member on the type that declares it: typeName '{declaringTypes[0]}'.", typeResolution),
                    context: new { typeName, memberName, declaringTypes, shadowedTypes }));
            }
            return (null, CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Member '{memberName}' not found in type '{typeName}'",
                hint: WithShadowNotice("Use get_type_members to see available members", typeResolution),
                context: new { typeName, memberName, shadowedTypes }));
        }

        if (members.Count > 1 && overloadIndex == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AmbiguousMatch,
                $"Member '{memberName}' has {members.Count} overloads in '{typeName}'",
                hint: WithShadowNotice("Repeat the call with the overloadIndex of the intended candidate.", typeResolution),
                context: new
                {
                    typeName,
                    memberName,
                    candidates = members
                        .Select((m, i) => new { overloadIndex = i, signature = m.ToDisplayString() })
                        .ToList(),
                    shadowedTypes
                }));
        }

        var index = overloadIndex ?? 0;
        if (index < 0 || index >= members.Count)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid overloadIndex {index}. Available: 0 to {members.Count - 1}",
                hint: WithShadowNotice(null, typeResolution),
                context: new { overloadIndex, availableOverloads = members.Count, shadowedTypes }));
        }

        return (members[index], null);
    }

    // Addressing-mode gate for tools that accept BOTH positional (filePath/line/
    // column) and name-based (typeName/memberName/overloadIndex) addressing.
    // Exactly one complete mode must be supplied; name-based addressing is
    // normalized to the declaration's identifier position so either mode flows
    // through the same position-based pipeline, and the address carries the symbol
    // the name resolved to and the type resolution behind it (ResolvedAddress.Symbol,
    // ResolvedAddress.TypeResolution). memberNameRequired is set by
    // tools that target a member (change_signature, add_null_checks) so a bare
    // typeName fails up front instead of resolving to the type declaration and
    // failing later with a confusing NOT_A_METHOD.
    //
    // This is the AUTHORITATIVE "exactly one mode" enforcement point. The tool
    // schemas express the either/or in prose rather than JSON-Schema oneOf,
    // because MCP clients honor oneOf inconsistently and a parameter the client
    // lets through must still be validated server-side. Both-or-neither returns
    // InvalidParameter here, exercised by the name-based-addressing tests.
    internal async Task<(ResolvedAddress? address, object? error)> ResolveAddressingAsync(
        string? filePath, int? line, int? column,
        string? typeName, string? memberName, int? overloadIndex,
        bool memberNameRequired = false,
        CancellationToken cancellationToken = default)
    {
        var positionalSupplied = filePath != null || line != null || column != null;
        var nameSupplied = typeName != null || memberName != null || overloadIndex != null;

        if (positionalSupplied && nameSupplied)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "Both addressing modes were supplied. Use either filePath/line/column (positional) or typeName with optional memberName/overloadIndex (name-based), not both.",
                context: new { filePath, line, column, typeName, memberName, overloadIndex }));
        }

        if (!positionalSupplied && !nameSupplied)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "No addressing was supplied. Use either filePath/line/column (positional) or typeName with optional memberName/overloadIndex (name-based)."));
        }

        if (positionalSupplied)
        {
            if (filePath == null || line == null || column == null)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    "Positional addressing requires all of filePath, line, and column.",
                    context: new
                    {
                        filePathSupplied = filePath != null,
                        lineSupplied = line != null,
                        columnSupplied = column != null
                    }));
            }
            return (new ResolvedAddress(filePath, line.Value, column.Value), null);
        }

        if (typeName == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "memberName and overloadIndex require typeName.",
                context: new { memberName, overloadIndex }));
        }

        if (memberNameRequired && memberName == null)
        {
            return (null, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "This tool targets a member: name-based addressing requires memberName alongside typeName.",
                context: new { typeName }));
        }

        var resolution = await ResolveMemberByNameAsync(typeName, memberName, overloadIndex, cancellationToken);
        if (resolution.Symbol is not { } symbol) return (null, resolution.Error);
        var typeResolution = resolution.Type!;
        var shadowedTypes = typeResolution.ShadowedMatches;

        // The body-carrying part of a partial member, else its earliest part, within
        // the solution's own documents: a source generator's part (a [GeneratedRegex]
        // or [LoggerMessage] implementation, a generated partial-type declaration) is
        // not a document a position can address.
        var location = GetPreferredSourceLocation(symbol);
        if (location?.SourceTree?.FilePath is not { Length: > 0 } sourcePath)
        {
            var generatedOnly = SymbolParts.SourceLocations(symbol).Count > 0;
            return (null, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                generatedOnly
                    ? $"'{symbol.ToDisplayString()}' is declared only in generated code, which is not a solution document"
                    : $"'{symbol.ToDisplayString()}' has no source location (defined in metadata)",
                hint: WithShadowNotice("Name-based addressing only works for symbols declared in solution source files.", typeResolution),
                context: new { typeName, memberName, shadowedTypes }));
        }

        // The address is consumed by position-based resolution, so it must denote the
        // named symbol again there. A member whose declaration position belongs to
        // another symbol (a record's primary constructor shares the type's identifier,
        // a positional record property its parameter's) cannot be addressed this way;
        // acting on the other symbol would target the wrong declaration.
        var start = location.GetLineSpan().StartLinePosition;
        var (atAddress, _, _) = await ResolveSymbolAtAsync(sourcePath, start.Line, start.Character, cancellationToken);
        if (atAddress == null || !SymbolParts.SameMember(atAddress, symbol))
        {
            return (null, CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"'{symbol.ToDisplayString()}' has no declaration position of its own: its position denotes '{atAddress?.ToDisplayString() ?? "no symbol"}'",
                hint: WithShadowNotice("This member is declared implicitly by another declaration (e.g. a record's primary constructor or positional property); target that declaration instead.", typeResolution),
                context: new { typeName, memberName, overloadIndex, shadowedTypes }));
        }

        return (new ResolvedAddress(sourcePath, start.Line, start.Character, symbol, typeResolution), null);
    }

    // Resolves (filePath, line, column) to the symbol the token at that position
    // denotes (TryFindSymbolForRename: the declaration a modifier or identifier
    // belongs to, a reference's target, a cref's target — never the enclosing or next
    // declaration). Returns (symbol, position, null) on success or
    // (null, maybe-position, error) on failure, the error's hint naming the nearest
    // position on the line that resolves.
    internal async Task<(ISymbol? symbol, ResolvedPosition? position, object? error)> ResolveSymbolAtAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        var (position, error) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (position == null)
        {
            return (null, null, error);
        }

        var (symbol, debugInfo, hint) = TryFindSymbolForRename(
            position.Tree, position.Model, position.Position, line, column);

        if (symbol == null)
        {
            return (null, position, CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No symbol found at position",
                hint: hint,
                context: new { filePath, line, column, debug = debugInfo }));
        }

        return (symbol, position, null);
    }
}
