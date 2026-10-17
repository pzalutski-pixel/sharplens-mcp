using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace SharpLensMcp;

// resolve_stack_trace: paste a .NET stack trace, get frames mapped to file/line/symbol
// with source snippets, compiler name mangling undone. Frame lines are read in the shapes
// runtimes print them:
//   - .NET: "at Ns.Type.Method(String s) in path:line N";
//   - Mono / Unity: "at Ns.Type.Method (System.String s) [0x00000] in path:N";
//   - localized runtimes: the "at", "in" and "line" words in the runtime's language
//     ("bei ... in ...:Zeile N", "à ... dans ...:ligne N").
// A line number too large for an int is no line. Names are demangled: async state
// machines (Type+<Method>d__N.MoveNext), lambdas and closures (<Method>b__N_M in a "<>c" or
// "<>c__DisplayClass" container, joined by "+" or "."), async lambdas
// (<<Method>b__N_M>d.MoveNext), local functions (<Method>g__Local|N_M), demangled iterator
// frames (Method(args)+MoveNext()), static constructors (Type..cctor), nested types
// (Outer+Inner or Outer.Inner) and explicit interface implementations
// (Type.Ns.IFace.Member). The method is chosen among overloads by generic arity,
// parameter count and the parameter types the frame names, and by the reported line when
// the frame carries one. Frames that resolve to nothing in the solution (BCL, third-party)
// pass through with resolved: false. Each frame carries its type resolution's shadow
// notice.
public partial class RoslynService
{
    // A frame line in English: "at <signature>[ [0x...]][ in <file>:line <n>]".
    private static readonly Regex StackFrameRegex = new(
        @"^\s*at\s+(?<sig>.+?)(?:\s+\[0x[0-9a-fA-F]+\])?(?:\s+in\s+(?<file>.+?):(?:line\s+)?(?<line>\d+))?\s*$",
        RegexOptions.Compiled);

    // A frame line in any language: one leading word, a dotted member name with an
    // argument list, and optionally an IL offset and "<word> <file>:[<word> ]<n>". The
    // dotted name and the parentheses keep exception messages from matching.
    private static readonly Regex LocalizedStackFrameRegex = new(
        @"^\s*\S+\s+(?<sig>[^\s(]*\.[^\s(.]+\s?\(.*?\)(?:\+MoveNext\(\))?)(?:\s+\[0x[0-9a-fA-F]+\])?(?:\s+\S+\s+(?<file>.+?):(?:[^\s:\\/\d]+\s?)?(?<line>\d+))?\s*$",
        RegexOptions.Compiled);

    // A demangled iterator frame: "Method(args)+MoveNext()".
    private static readonly Regex IteratorFrameRegex = new(
        @"^(?<call>.+\))\+MoveNext\(\)$", RegexOptions.Compiled);

    private static readonly Regex AsyncFrameRegex = new(
        @"^(?<type>.+?)[+.]<(?<method>[^<>]+)>d(?:__\d+)?\.MoveNext$", RegexOptions.Compiled);

    private static readonly Regex AsyncLambdaFrameRegex = new(
        @"^(?<prefix>.+?)[+.]<<(?<method>[^<>]+)>b__[\d_]+>d(?:__\d+)?\.MoveNext$", RegexOptions.Compiled);

    private static readonly Regex LocalFunctionFrameRegex = new(
        @"^(?<prefix>.+?)[+.]?<(?<method>[^>]+)>g__(?<local>[^|]+)\|[\d_]+$", RegexOptions.Compiled);

    private static readonly Regex LambdaFrameRegex = new(
        @"^(?<prefix>.+?)[+.]?<(?<method>[^>]+)>b__[\d_]+$", RegexOptions.Compiled);

    // The compiler's closure containers, "<>c" and "<>c__DisplayClassN_M", in either
    // spelling of the nesting separator.
    private static readonly Regex CompilerContainerRegex = new(
        @"[+.]<>c(__DisplayClass[\d_]+)?", RegexOptions.Compiled);

    private const int MaxStackTraceContextLines = 20;

    public async Task<object> ResolveStackTraceAsync(
        string stackTrace, int contextLines = 2, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "stackTrace must contain at least one stack frame line",
                context: new { });
        }
        if (ParameterValidation.Limit("contextLines", contextLines, MaxStackTraceContextLines, minimum: 0) is { } invalid)
        {
            return CreateErrorResponse(invalid);
        }

        var frames = new List<object>();
        var resolvedCount = 0;

        foreach (var rawLine in stackTrace.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var match = StackFrameRegex.Match(line);
            if (!match.Success) match = LocalizedStackFrameRegex.Match(line);
            if (!match.Success)
            {
                // Exception messages, "--- End of stack trace ---" separators,
                // inner-exception headers: preserved so the output reads as the
                // full trace, but they are not frames.
                frames.Add(new
                {
                    raw = line.Trim(),
                    symbol = (string?)null,
                    frameKind = "text",
                    resolved = false,
                    ambiguousOverload = false,
                    location = (object?)null,
                    snippet = (string?)null,
                    notice = (string?)null
                });
                continue;
            }

            var file = match.Groups["file"].Success ? match.Groups["file"].Value : null;
            int? zeroBasedLine = match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out var oneBasedLine) && oneBasedLine > 0
                ? oneBasedLine - 1
                : null;
            var frame = await ResolveFrameAsync(
                line.Trim(), match.Groups["sig"].Value, file, zeroBasedLine, contextLines, cancellationToken);
            frames.Add(frame.entry);
            if (frame.resolved) resolvedCount++;
        }

        return CreateSuccessResponse(
            data: new
            {
                frames,
                frameCount = frames.Count,
                resolvedCount
            },
            suggestedNextTools: new[]
            {
                "get_method_source on a resolved symbol for the full implementation",
                "get_exception_flow on the topmost resolved frame to see what else can escape it"
            },
            totalCount: frames.Count,
            returnedCount: frames.Count);
    }

    private async Task<(object entry, bool resolved)> ResolveFrameAsync(
        string raw, string signature, string? tracedFile, int? tracedLine,
        int contextLines, CancellationToken cancellationToken)
    {
        var frameKind = "method";

        // "Method(args)+MoveNext()": a demangled iterator's MoveNext; the arguments are the
        // iterator method's own.
        if (IteratorFrameRegex.Match(signature) is { Success: true } iteratorMatch)
        {
            frameKind = "iterator";
            signature = iteratorMatch.Groups["call"].Value;
        }

        // Split "Ns.Type.Method(args)" into the dotted part and the argument list (a Mono
        // frame puts a space before the parenthesis).
        var parenIndex = signature.IndexOf('(');
        var dotted = (parenIndex >= 0 ? signature[..parenIndex] : signature).TrimEnd();
        IReadOnlyList<string>? parameterTypes = null;
        int? typeParamCount = null;
        if (parenIndex >= 0 && signature.EndsWith(')'))
        {
            parameterTypes = SplitTopLevel(signature[(parenIndex + 1)..^1]);
        }

        string typePart;
        string methodName;
        string? localName = null;

        if (AsyncLambdaFrameRegex.Match(dotted) is { Success: true } asyncLambdaMatch)
        {
            frameKind = "lambda";
            typePart = StripCompilerContainers(asyncLambdaMatch.Groups["prefix"].Value);
            methodName = asyncLambdaMatch.Groups["method"].Value;
            parameterTypes = null; // MoveNext() says nothing about the containing method
        }
        else if (AsyncFrameRegex.Match(dotted) is { Success: true } asyncMatch)
        {
            frameKind = "async";
            typePart = asyncMatch.Groups["type"].Value;
            methodName = asyncMatch.Groups["method"].Value;
            parameterTypes = null;
        }
        else if (LocalFunctionFrameRegex.Match(dotted) is { Success: true } localMatch)
        {
            frameKind = "localFunction";
            typePart = StripCompilerContainers(localMatch.Groups["prefix"].Value);
            methodName = localMatch.Groups["method"].Value;
            localName = localMatch.Groups["local"].Value;
            parameterTypes = null;
        }
        else if (LambdaFrameRegex.Match(dotted) is { Success: true } lambdaMatch)
        {
            frameKind = "lambda";
            typePart = StripCompilerContainers(lambdaMatch.Groups["prefix"].Value);
            methodName = lambdaMatch.Groups["method"].Value;
            parameterTypes = null;
        }
        else
        {
            var lastDot = dotted.LastIndexOf('.');
            if (lastDot <= 0)
            {
                return (new
                {
                    raw, symbol = (string?)null, frameKind = "unknown",
                    resolved = false, ambiguousOverload = false,
                    location = (object?)null, snippet = (string?)null, notice = (string?)null
                }, false);
            }
            typePart = dotted[..lastDot];
            methodName = dotted[(lastDot + 1)..];
            // A generic-method frame renders the method as `Method[T,U]`; drop the
            // arity marker and remember the type-parameter count to disambiguate by it.
            var bracketIndex = methodName.IndexOf('[');
            if (bracketIndex > 0 && methodName.EndsWith(']'))
            {
                typeParamCount = SplitTopLevel(methodName[(bracketIndex + 1)..^1]).Count;
                methodName = methodName[..bracketIndex];
            }
            // Type..ctor / Type..cctor: the constructor name carries its own leading dot.
            if (methodName is "ctor" or "cctor" && typePart.EndsWith('.'))
            {
                typePart = typePart.TrimEnd('.');
                methodName = "." + methodName;
            }
        }

        var typeDisplay = typePart.Replace('+', '.');
        // A constructor frame demangles to methodName ".ctor"/".cctor"; it already carries
        // its own leading dot, so joining with another '.' would double it (Type..ctor).
        // Every other frame needs the separating dot.
        var symbol = (methodName.StartsWith('.') ? $"{typeDisplay}{methodName}" : $"{typeDisplay}.{methodName}") +
                     (localName != null ? $".{localName}" : "");

        // Resolve the demangled name up front so a file:line frame can be validated against
        // the real declaration — a stale PDB can point at a line that no longer belongs to
        // this method.
        var tracedDocument = tracedFile != null ? TryFindDocument(tracedFile) : null;
        var (method, ambiguousOverload, notice) = await ResolveFrameMethodAsync(
            typePart, methodName, parameterTypes, typeParamCount, tracedDocument, tracedLine, cancellationToken);

        // A file:line frame points at the exact statement — trust it when the line falls
        // inside the resolved method's own declaration, or when there is no resolved
        // symbol to validate against. A stale line that lands outside the method falls
        // through to the declaration location below.
        if (tracedDocument != null && tracedLine != null
            && (method == null || MethodSpansLine(method, tracedDocument, tracedLine.Value)))
        {
            var snippet = await ExtractSnippetAsync(tracedDocument, tracedLine.Value, contextLines, cancellationToken);
            return (new
            {
                raw,
                symbol = (string?)symbol,
                frameKind,
                resolved = true,
                ambiguousOverload,
                location = (object?)new { filePath = FormatPath(tracedDocument.FilePath), line = tracedLine.Value },
                snippet,
                notice
            }, true);
        }

        // No usable/validated file info: resolve against the declaration.
        var location = method == null ? null : await LocateFrameTargetAsync(method, localName, cancellationToken);
        if (location == null)
        {
            return (new
            {
                raw, symbol = (string?)symbol, frameKind,
                resolved = false, ambiguousOverload = false,
                location = (object?)null, snippet = (string?)null, notice
            }, false);
        }

        var lineSpan = location.GetLineSpan();
        var document = _solution!.GetDocument(location.SourceTree);
        var declarationSnippet = document == null
            ? null
            : await ExtractSnippetAsync(document, lineSpan.StartLinePosition.Line, contextLines, cancellationToken);
        return (new
        {
            raw,
            symbol = (string?)symbol,
            frameKind,
            resolved = true,
            ambiguousOverload,
            location = (object?)new
            {
                filePath = FormatPath(lineSpan.Path),
                line = lineSpan.StartLinePosition.Line
            },
            snippet = declarationSnippet,
            notice
        }, true);
    }

    private static string StripCompilerContainers(string prefix) =>
        CompilerContainerRegex.Replace(prefix, "").TrimEnd('.');

    // The frame's method, with its type resolution's shadow notice. The type part is the
    // longest prefix of the dotted name that names a type: an explicit interface
    // implementation's member name is qualified by the interface ("Type.Ns.IFace.Member"),
    // so while a prefix names no type its last segment belongs to the member name. Once a
    // prefix names a type, the frame's method is looked up there alone (a BCL frame's type
    // resolves to metadata and has no in-source method).
    private async Task<(IMethodSymbol? method, bool ambiguous, string? notice)> ResolveFrameMethodAsync(
        string typePart, string methodName, IReadOnlyList<string>? parameterTypes, int? typeParamCount,
        Document? tracedDocument, int? tracedLine, CancellationToken cancellationToken)
    {
        var memberName = methodName;
        var candidateType = typePart;
        while (true)
        {
            var resolution = await ResolveTypeByNameAsync(candidateType, cancellationToken);
            if (resolution.Type is { } type)
            {
                var (method, ambiguous) = PickFrameMethod(type, memberName, parameterTypes, typeParamCount, tracedDocument, tracedLine);
                return (method, ambiguous, resolution.ShadowNotice);
            }
            var lastDot = candidateType.LastIndexOf('.');
            if (lastDot <= 0 || methodName.StartsWith('.')) return (null, false, null);
            memberName = $"{candidateType[(lastDot + 1)..]}.{memberName}";
            candidateType = candidateType[..lastDot];
        }
    }

    // The top-level comma-separated items of a frame's argument or type-argument list.
    // Splitting on every ',' over-splits a single item whose type carries its own commas —
    // a tuple `(int, int) p`, a generic `Dictionary<int, string> d`, an array rank — so
    // only commas at bracket depth zero separate items.
    private static IReadOnlyList<string> SplitTopLevel(string list)
    {
        var items = new List<string>();
        var trimmed = list.Trim();
        if (trimmed.Length == 0) return items;
        var depth = 0;
        var start = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is '(' or '<' or '[') depth++;
            else if (c is ')' or '>' or ']') { if (depth > 0) depth--; }
            else if (c == ',' && depth == 0)
            {
                items.Add(trimmed[start..i].Trim());
                start = i + 1;
            }
        }
        items.Add(trimmed[start..].Trim());
        return items;
    }

    // Resolves the frame's method among its overloads, narrowing by generic arity, then
    // by parameter count, then by the parameter types the frame names, then by the method
    // whose declaration spans the reported line. ambiguous: more than one in-source
    // overload remained (always so for async/lambda/local frames with no parameters and no
    // line to decide).
    private static (IMethodSymbol? method, bool ambiguous) PickFrameMethod(
        INamedTypeSymbol type, string methodName, IReadOnlyList<string>? parameterTypes, int? typeParamCount,
        Document? tracedDocument, int? tracedLine)
    {
        var candidates = (methodName switch
            {
                ".ctor" => type.InstanceConstructors.AsEnumerable(),
                ".cctor" => type.StaticConstructors.AsEnumerable(),
                _ => type.GetMembers(methodName).OfType<IMethodSymbol>()
            })
            .Where(m => m.Locations.Any(l => l.IsInSource))
            .ToList();
        if (candidates.Count == 0) return (null, false);

        List<IMethodSymbol> Narrow(Func<IMethodSymbol, bool> predicate)
        {
            var narrowed = candidates.Where(predicate).ToList();
            return narrowed.Count > 0 ? narrowed : candidates;
        }

        if (typeParamCount != null) candidates = Narrow(m => m.Arity == typeParamCount);
        if (parameterTypes != null)
        {
            candidates = Narrow(m => m.Parameters.Length == parameterTypes.Count);
            candidates = Narrow(m => m.Parameters.Length == parameterTypes.Count
                                     && m.Parameters.Zip(parameterTypes).All(p => FrameTypeMatches(p.Second, p.First.Type)));
        }
        if (candidates.Count > 1 && tracedDocument != null && tracedLine != null)
        {
            candidates = Narrow(m => MethodSpansLine(m, tracedDocument, tracedLine.Value));
        }
        return (candidates[0], candidates.Count > 1);
    }

    // Whether a frame's parameter text ("String s", "System.String s", "Int32[] xs",
    // "IEnumerable`1 items", "(int, int) pair", "Int32& x") names the parameter's type.
    // The frame prints the type by its simple metadata name (namespace-qualified on Mono);
    // a shape it cannot read never rules a candidate out.
    private static bool FrameTypeMatches(string parameterText, ITypeSymbol type)
    {
        var text = parameterText.Trim();
        // Drop the parameter name: the last space-separated token outside brackets.
        var depth = 0;
        var nameSeparator = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '(' or '<' or '[') depth++;
            else if (c is ')' or '>' or ']') { if (depth > 0) depth--; }
            else if (c == ' ' && depth == 0) nameSeparator = i;
        }
        var typeText = (nameSeparator > 0 ? text[..nameSeparator] : text).Trim();
        return TypeTextMatches(typeText, type);
    }

    private static bool TypeTextMatches(string typeText, ITypeSymbol type)
    {
        if (typeText.EndsWith('&')) typeText = typeText[..^1];
        if (typeText.Length == 0) return true;
        if (typeText.StartsWith('(') && typeText.EndsWith(')'))
        {
            return type is INamedTypeSymbol { IsTupleType: true } tuple
                   && tuple.TupleElements.Length == SplitTopLevel(typeText[1..^1]).Count;
        }
        if (typeText.EndsWith(']') && typeText.LastIndexOf('[') is var open and > 0)
        {
            return type is IArrayTypeSymbol array && TypeTextMatches(typeText[..open], array.ElementType);
        }
        if (typeText.EndsWith('*'))
        {
            return type is IPointerTypeSymbol pointer && TypeTextMatches(typeText[..^1], pointer.PointedAtType);
        }

        // A C#-spelled generic ("List<int>") is compared by its name before the type arguments.
        var genericOpen = typeText.IndexOf('<');
        var name = genericOpen > 0 ? typeText[..genericOpen] : typeText;
        name = name[(Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+')) + 1)..];
        if (KeywordTypeNames.TryGetValue(name, out var metadataName)) name = metadataName;
        return name == type.MetadataName || name == type.Name;
    }

    private static readonly Dictionary<string, string> KeywordTypeNames = new(StringComparer.Ordinal)
    {
        ["bool"] = "Boolean", ["byte"] = "Byte", ["sbyte"] = "SByte", ["char"] = "Char",
        ["decimal"] = "Decimal", ["double"] = "Double", ["float"] = "Single", ["int"] = "Int32",
        ["uint"] = "UInt32", ["long"] = "Int64", ["ulong"] = "UInt64", ["short"] = "Int16",
        ["ushort"] = "UInt16", ["object"] = "Object", ["string"] = "String", ["nint"] = "IntPtr",
        ["nuint"] = "UIntPtr"
    };

    // Whether the method has a declaration in `document` whose span contains the
    // given line — the validation that a file:line frame really belongs here.
    private static bool MethodSpansLine(IMethodSymbol method, Document document, int zeroBasedLine)
    {
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            var tree = declRef.SyntaxTree;
            if (document.FilePath == null || !string.Equals(tree.FilePath, document.FilePath, PathComparison))
                continue;
            var span = tree.GetLineSpan(declRef.Span);
            if (zeroBasedLine >= span.StartLinePosition.Line && zeroBasedLine <= span.EndLinePosition.Line)
                return true;
        }
        return false;
    }

    // The frame target's declaration location: the local function's own
    // declaration when the frame named one (falling back to the containing
    // method if it is not found), otherwise the method itself.
    private async Task<Location?> LocateFrameTargetAsync(
        IMethodSymbol method, string? localName, CancellationToken cancellationToken)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        if (location == null || localName == null) return location;

        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            var declaration = await declRef.GetSyntaxAsync(cancellationToken);
            var localFunction = declaration.DescendantNodes()
                .OfType<LocalFunctionStatementSyntax>()
                .FirstOrDefault(lf => lf.Identifier.Text == localName);
            if (localFunction != null) return localFunction.Identifier.GetLocation();
        }
        return location;
    }

    private static async Task<string?> ExtractSnippetAsync(
        Document document, int zeroBasedLine, int contextLines, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken);
        if (zeroBasedLine < 0 || zeroBasedLine >= text.Lines.Count) return null;
        var start = Math.Max(0, zeroBasedLine - contextLines);
        var end = Math.Min(text.Lines.Count - 1, zeroBasedLine + contextLines);
        return string.Join("\n", Enumerable.Range(start, end - start + 1)
            .Select(i => text.Lines[i].ToString()));
    }
}
