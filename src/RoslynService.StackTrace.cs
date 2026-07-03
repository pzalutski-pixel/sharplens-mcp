using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace SharpLensMcp;

// resolve_stack_trace: paste a .NET stack trace, get frames mapped to
// file/line/symbol with source snippets, compiler name mangling undone.
// Handles both modern traces (file:line present, async frames already
// demangled by the runtime) and legacy/mangled shapes: async state machines
// (Type+<Method>d__N.MoveNext), lambdas (<Method>b__N_M, including
// <>c/<>c__DisplayClass containers), and local functions
// (<Method>g__Local|N_M). Frames that resolve to nothing in the solution
// (BCL, third-party) pass through unmodified with resolved: false.
public partial class RoslynService
{
    private static readonly Regex StackFrameRegex = new(
        @"^\s*at\s+(?<sig>.+?)(\s+in\s+(?<file>.+):line\s+(?<line>\d+))?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex AsyncFrameRegex = new(
        @"^(?<type>.+?)[+.]<(?<method>[^>]+)>d__\d+\.MoveNext$", RegexOptions.Compiled);

    private static readonly Regex LocalFunctionFrameRegex = new(
        @"^(?<prefix>.+?)[+.]?<(?<method>[^>]+)>g__(?<local>[^|]+)\|[\d_]+$", RegexOptions.Compiled);

    private static readonly Regex LambdaFrameRegex = new(
        @"^(?<prefix>.+?)[+.]?<(?<method>[^>]+)>b__[\d_]+$", RegexOptions.Compiled);

    private static readonly Regex CompilerContainerRegex = new(
        @"\+<>c(__DisplayClass[\d_]+)?", RegexOptions.Compiled);

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
        if (contextLines is < 0 or > 20)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"contextLines must be between 0 and 20 (got {contextLines})",
                context: new { contextLines });
        }

        var frames = new List<object>();
        var resolvedCount = 0;

        foreach (var rawLine in stackTrace.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            var match = StackFrameRegex.Match(line);
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
                    snippet = (string?)null
                });
                continue;
            }

            var frame = await ResolveFrameAsync(
                line.Trim(),
                match.Groups["sig"].Value,
                match.Groups["file"].Success ? match.Groups["file"].Value : null,
                match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value) - 1 : null,
                contextLines,
                cancellationToken);
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
        // Split "Ns.Type.Method(args)" into the dotted part and the argument list.
        var parenIndex = signature.IndexOf('(');
        var dotted = parenIndex >= 0 ? signature[..parenIndex] : signature;
        int? arity = null;
        int? typeParamCount = null;
        if (parenIndex >= 0 && signature.EndsWith(')'))
        {
            arity = CountParameters(signature[(parenIndex + 1)..^1]);
        }

        var frameKind = "method";
        string typePart;
        string methodName;
        string? localName = null;

        if (AsyncFrameRegex.Match(dotted) is { Success: true } asyncMatch)
        {
            frameKind = "async";
            typePart = asyncMatch.Groups["type"].Value;
            methodName = asyncMatch.Groups["method"].Value;
            arity = null; // MoveNext() arity says nothing about the real method
        }
        else if (LocalFunctionFrameRegex.Match(dotted) is { Success: true } localMatch)
        {
            frameKind = "localFunction";
            typePart = CompilerContainerRegex.Replace(localMatch.Groups["prefix"].Value, "").TrimEnd('.');
            methodName = localMatch.Groups["method"].Value;
            localName = localMatch.Groups["local"].Value;
            arity = null;
        }
        else if (LambdaFrameRegex.Match(dotted) is { Success: true } lambdaMatch)
        {
            frameKind = "lambda";
            typePart = CompilerContainerRegex.Replace(lambdaMatch.Groups["prefix"].Value, "").TrimEnd('.');
            methodName = lambdaMatch.Groups["method"].Value;
            arity = null;
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
                    location = (object?)null, snippet = (string?)null
                }, false);
            }
            typePart = dotted[..lastDot];
            methodName = dotted[(lastDot + 1)..];
            // A generic-method frame renders the method as `Method[T,U]`; drop the
            // arity marker and remember the type-parameter count to disambiguate by it.
            var bracketIndex = methodName.IndexOf('[');
            if (bracketIndex > 0 && methodName.EndsWith(']'))
            {
                typeParamCount = CountParameters(methodName[(bracketIndex + 1)..^1]);
                methodName = methodName[..bracketIndex];
            }
            if (methodName == "ctor" && typePart.EndsWith('.'))
            {
                typePart = typePart.TrimEnd('.');
                methodName = ".ctor";
            }
        }

        var typeDisplay = typePart.Replace('+', '.');
        // A constructor frame demangles to methodName ".ctor"; it already carries
        // its own leading dot, so joining with another '.' would double it
        // (Type..ctor). Every other frame needs the separating dot.
        var symbol = (methodName.StartsWith('.') ? $"{typeDisplay}{methodName}" : $"{typeDisplay}.{methodName}") +
                     (localName != null ? $".{localName}" : "");

        // Resolve the demangled name up front so a file:line frame can be validated
        // against the real declaration — a stale PDB can point at a line that no
        // longer belongs to this method.
        var (type, _) = await FindTypeByNameAsync(typePart, cancellationToken);
        var (method, ambiguousOverload) = type == null
            ? ((IMethodSymbol?)null, false)
            : PickFrameMethod(type, methodName, arity, typeParamCount);

        // A file:line frame points at the exact statement — trust it when the line
        // falls inside the resolved method's own declaration, or when there is no
        // resolved symbol to validate against. A stale line that lands outside the
        // method falls through to the declaration location below.
        if (tracedFile != null && tracedLine != null && TryFindDocument(tracedFile) is { } tracedDocument
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
                snippet
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
                location = (object?)null, snippet = (string?)null
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
            snippet = declarationSnippet
        }, true);
    }

    // Count top-level parameters in a frame's argument list. Splitting on ','
    // over-counts a single parameter whose type carries its own commas — a tuple
    // `(int, int) p`, a generic `Dictionary<int, string> d`, an array rank. Only
    // commas at bracket depth zero separate parameters.
    private static int CountParameters(string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Length == 0) return 0;
        var depth = 0;
        var count = 1;
        foreach (var c in trimmed)
        {
            if (c is '(' or '<' or '[') depth++;
            else if (c is ')' or '>' or ']') { if (depth > 0) depth--; }
            else if (c == ',' && depth == 0) count++;
        }
        return count;
    }

    // Resolves the frame's method, reporting whether the choice was ambiguous —
    // i.e. more than one in-source overload remained after narrowing by generic
    // arity and parameter count (always so for async/lambda/local frames, whose
    // mangled names carry no parameter count).
    private static (IMethodSymbol? method, bool ambiguous) PickFrameMethod(
        INamedTypeSymbol type, string methodName, int? arity, int? typeParamCount)
    {
        var candidates = (methodName == ".ctor"
                ? type.InstanceConstructors.AsEnumerable()
                : type.GetMembers(methodName).OfType<IMethodSymbol>())
            .Where(m => m.Locations.Any(l => l.IsInSource))
            .ToList();
        if (candidates.Count == 0) return (null, false);

        // Narrow by generic arity first when the frame carried `[T,U]`.
        if (typeParamCount != null)
        {
            var byTypeParams = candidates.Where(m => m.Arity == typeParamCount).ToList();
            if (byTypeParams.Count > 0) candidates = byTypeParams;
        }

        if (arity != null)
        {
            var arityMatches = candidates.Where(m => m.Parameters.Length == arity).ToList();
            if (arityMatches.Count > 0) return (arityMatches[0], arityMatches.Count > 1);
        }
        return (candidates[0], candidates.Count > 1);
    }

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
