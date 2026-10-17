using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// find_interceptors: surface [InterceptsLocation] call rerouting — the hidden
// control flow behind ASP.NET minimal APIs, EF compiled models, and
// hand-written interceptors. Scans every tree of the AUGMENTED compilation
// (generated trees included — that's where most interceptors live) and
// matches the attribute by simple name, because the attribute type is
// typically file-local or generator-emitted rather than a shared reference.
// The name is read through any qualification (Ns.InterceptsLocation,
// global::InterceptsLocation), and the arguments by name when the call names them,
// else by position. A multi-targeted project's attributes are reported once, all in
// canonical location order before the cap.
public partial class RoslynService
{
    private const int MaxInterceptorResults = 1000;

    public async Task<object> FindInterceptorsAsync(
        string? projectName = null, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxInterceptorResults) is { } invalid) return CreateErrorResponse(invalid);

        var (projects, projectError) = ResolveProjectScope(projectName);
        if (projectError != null) return projectError;

        var found = new List<(ResultLocation location, AttributeSyntax attribute, SyntaxTree tree, bool inGeneratedCode)>();
        var seen = new HashSet<SourceSpanKey>();

        foreach (var project in projects!)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            var projectDocumentPaths = new HashSet<string>(
                project.Documents.Where(d => d.FilePath != null).Select(d => d.FilePath!),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inGeneratedCode = !projectDocumentPaths.Contains(tree.FilePath);
                var root = await tree.GetRootAsync(cancellationToken);

                foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
                {
                    if (SimpleNameOf(attribute.Name) is not ("InterceptsLocation" or "InterceptsLocationAttribute")) continue;
                    if (!seen.Add(new SourceSpanKey(tree.FilePath, attribute.Span))) continue;
                    var lineSpan = attribute.GetLocation().GetLineSpan();
                    found.Add((new ResultLocation(FormatPath(lineSpan.Path), lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character),
                        attribute, tree, inGeneratedCode));
                }
            }
        }

        var ordered = CanonicalOrder.ByLocation(found, f => f.location);
        var interceptors = new List<object>();
        foreach (var (_, attribute, tree, inGeneratedCode) in ordered.Take(maxResults))
        {
            interceptors.Add(await DescribeInterceptorAsync(attribute, tree, inGeneratedCode, cancellationToken));
        }

        return CreateSuccessResponse(
            data: new { interceptors },
            ResultCounts.Capped(ordered.Count, interceptors.Count),
            suggestedNextTools: interceptors.Count > 0
                ? new[] { "get_generated_code to read a generated interceptor's full body" }
                : new[] { "No [InterceptsLocation] attributes anywhere in scope - no call rerouting in play" });
    }

    // The rightmost identifier of an attribute name, through any qualification.
    private static string? SimpleNameOf(NameSyntax name) => name switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        AliasQualifiedNameSyntax aliasQualified => aliasQualified.Name.Identifier.Text,
        SimpleNameSyntax simple => simple.Identifier.Text,
        _ => null
    };

    private async Task<object> DescribeInterceptorAsync(
        AttributeSyntax attribute, SyntaxTree tree, bool inGeneratedCode, CancellationToken cancellationToken)
    {
        var method = attribute.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        var containingType = method?.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        var interceptorMethod = method == null
            ? "(unknown)"
            : containingType != null
                ? $"{containingType.Identifier.Text}.{method.Identifier.Text}"
                : method.Identifier.Text;
        var methodLineSpan = (method?.Identifier.GetLocation() ?? attribute.GetLocation()).GetLineSpan();

        // Legacy form: (filePath, line, character) with 1-based positions;
        // data form: (version, data) with an opaque encoded location. Each argument is
        // the one its name: selects, else the one at its position.
        object target;
        var arguments = attribute.ArgumentList?.Arguments ?? default;
        ExpressionSyntax? Argument(string name, int position) =>
            arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name)?.Expression
            ?? (position < arguments.Count && arguments[position].NameColon == null ? arguments[position].Expression : null);

        if (arguments.Count == 3
            && Argument("filePath", 0) is LiteralExpressionSyntax { Token.Value: string targetFile }
            && Argument("line", 1) is LiteralExpressionSyntax { Token.Value: int oneBasedLine }
            && Argument("character", 2) is LiteralExpressionSyntax { Token.Value: int oneBasedColumn })
        {
            string? targetLineText = null;
            if (TryFindDocument(targetFile) is { } targetDocument)
            {
                var text = await targetDocument.GetTextAsync(cancellationToken);
                if (oneBasedLine - 1 >= 0 && oneBasedLine - 1 < text.Lines.Count)
                {
                    targetLineText = text.Lines[oneBasedLine - 1].ToString().Trim();
                }
            }
            target = new
            {
                form = "legacy",
                // The attribute's file argument passes through as authored (it may be
                // relative); FormatPath would mangle a relative literal.
                file = targetFile,
                // A malformed (0 or negative) literal must not produce a negative
                // 0-based position; clamp to 0.
                line = Math.Max(0, oneBasedLine - 1),
                column = Math.Max(0, oneBasedColumn - 1),
                lineText = targetLineText
            };
        }
        else if (arguments.Count == 2
                 && Argument("data", 1) is LiteralExpressionSyntax { Token.Value: string data })
        {
            var version = Argument("version", 0) is LiteralExpressionSyntax { Token.Value: int v } ? v : (int?)null;
            target = new { form = "data", version, raw = data };
        }
        else
        {
            target = new { form = "unknown", raw = attribute.ArgumentList?.ToString() ?? "" };
        }

        return new
        {
            interceptorMethod,
            interceptorLocation = new
            {
                // Normalize the path for both source and generated trees; the
                // inGeneratedCode flag already distinguishes the two.
                filePath = FormatPath(tree.FilePath),
                line = methodLineSpan.StartLinePosition.Line
            },
            target,
            inGeneratedCode
        };
    }
}
