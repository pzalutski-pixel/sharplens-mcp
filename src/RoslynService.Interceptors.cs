using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// find_interceptors: surface [InterceptsLocation] call rerouting — the hidden
// control flow behind ASP.NET minimal APIs, EF compiled models, and
// hand-written interceptors. Scans every tree of the AUGMENTED compilation
// (generated trees included — that's where most interceptors live) and
// matches the attribute by simple name, because the attribute type is
// typically file-local or generator-emitted rather than a shared reference.
public partial class RoslynService
{
    public async Task<object> FindInterceptorsAsync(
        string? projectName = null, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (maxResults is < 1 or > 1000)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxResults must be between 1 and 1000 (got {maxResults})",
                context: new { maxResults });
        }

        if (projectName != null && !_solution!.Projects.Any(
                p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectNotFoundError(projectName);
        }

        var projects = (projectName == null
                ? _solution!.Projects
                : _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var interceptors = new List<object>();
        var seen = new HashSet<(string, int)>();
        var totalFound = 0;

        foreach (var project in projects)
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
                    var attributeName = attribute.Name is QualifiedNameSyntax qualified
                        ? qualified.Right.Identifier.Text
                        : (attribute.Name as SimpleNameSyntax)?.Identifier.Text;
                    if (attributeName is not ("InterceptsLocation" or "InterceptsLocationAttribute")) continue;
                    if (!seen.Add((tree.FilePath, attribute.SpanStart))) continue;

                    totalFound++;
                    if (interceptors.Count >= maxResults) continue;
                    interceptors.Add(await DescribeInterceptorAsync(
                        attribute, tree, inGeneratedCode, cancellationToken));
                }
            }
        }

        return CreateSuccessResponse(
            data: new { interceptors },
            suggestedNextTools: interceptors.Count > 0
                ? new[] { "get_generated_code to read a generated interceptor's full body" }
                : new[] { "No [InterceptsLocation] attributes anywhere in scope - no call rerouting in play" },
            totalCount: totalFound,
            returnedCount: interceptors.Count);
    }

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
        // data form: (version, data) with an opaque encoded location.
        object target;
        var arguments = attribute.ArgumentList?.Arguments;
        if (arguments is { Count: 3 }
            && arguments.Value[0].Expression is LiteralExpressionSyntax { Token.Value: string targetFile }
            && arguments.Value[1].Expression is LiteralExpressionSyntax { Token.Value: int oneBasedLine }
            && arguments.Value[2].Expression is LiteralExpressionSyntax { Token.Value: int oneBasedColumn })
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
        else if (arguments is { Count: 2 }
                 && arguments.Value[1].Expression is LiteralExpressionSyntax { Token.Value: string data })
        {
            var version = arguments.Value[0].Expression is LiteralExpressionSyntax { Token.Value: int v } ? v : (int?)null;
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
