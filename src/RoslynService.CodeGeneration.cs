using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SharpLensMcp;

public partial class RoslynService
{

    /// <summary>
    /// Get complexity metrics for a method or file.
    /// </summary>
    public async Task<object> GetComplexityMetricsAsync(
        string filePath,
        int? line = null,
        int? column = null,
        List<string>? metrics = null)
    {
        EnsureSolutionLoaded();

        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        var semanticModel = await document.GetSemanticModelAsync();

        if (syntaxTree == null || semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file",
                context: new { filePath }
            );
        }

        var root = await syntaxTree.GetRootAsync();

        // Default to all metrics if none specified
        var requestedMetrics = metrics ?? new List<string>
        {
            "cyclomatic", "nesting", "loc", "parameters", "cognitive"
        };

        // If line is specified, find the method at that position
        if (line.HasValue)
        {
            var position = GetPosition(syntaxTree, line.Value, column ?? 0);
            var node = root.FindToken(position).Parent;

            // Find containing method
            var methodNode = node?.AncestorsAndSelf()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            if (methodNode == null)
            {
                // Try property accessor
                var accessor = node?.AncestorsAndSelf()
                    .OfType<AccessorDeclarationSyntax>()
                    .FirstOrDefault();

                if (accessor == null)
                {
                    return CreateErrorResponse(
                        ErrorCodes.SymbolNotFound,
                        "No method or property accessor found at this position",
                        hint: "Position cursor inside a method body",
                        context: new { filePath, line, column }
                    );
                }

                // Analyze accessor
                var accessorMetrics = CalculateComplexityMetrics(accessor, requestedMetrics);
                return CreateSuccessResponse(
                    data: new
                    {
                        scope = "accessor",
                        name = accessor.Parent is PropertyDeclarationSyntax prop
                            ? $"{prop.Identifier.Text}.{accessor.Keyword.Text}"
                            : accessor.Keyword.Text,
                        metrics = accessorMetrics
                    },
                    suggestedNextTools: new[]
                    {
                        accessorMetrics.TryGetValue("cyclomatic", out var accCC) && (int)accCC > 10
                            ? "Consider refactoring - cyclomatic complexity > 10"
                            : null
                    }.Where(s => s != null).ToArray()!
                );
            }

            var methodMetrics = CalculateComplexityMetrics(methodNode, requestedMetrics);
            var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode);

            return CreateSuccessResponse(
                data: new
                {
                    scope = "method",
                    name = methodSymbol?.Name ?? methodNode.Identifier.Text,
                    containingType = methodSymbol?.ContainingType?.Name,
                    metrics = methodMetrics
                },
                suggestedNextTools: new[]
                {
                    methodMetrics.TryGetValue("cyclomatic", out var cc) && (int)cc > 10
                        ? "Consider refactoring - cyclomatic complexity > 10"
                        : null,
                    methodMetrics.TryGetValue("nesting", out var nest) && (int)nest > 4
                        ? "Consider refactoring - nesting depth > 4"
                        : null
                }.Where(s => s != null).ToArray()!
            );
        }

        // Analyze whole file - get metrics for all methods
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Select(m =>
            {
                var symbol = semanticModel.GetDeclaredSymbol(m);
                var methodMetrics = CalculateComplexityMetrics(m, requestedMetrics);
                return new
                {
                    name = symbol?.Name ?? m.Identifier.Text,
                    containingType = symbol?.ContainingType?.Name,
                    line = m.GetLocation().GetLineSpan().StartLinePosition.Line,
                    metrics = methodMetrics
                };
            })
            .ToList();

        // Calculate file-level totals
        var fileTotals = new Dictionary<string, object>();
        foreach (var metric in requestedMetrics)
        {
            if (metric == "cyclomatic")
            {
                var total = methods.Sum(m => m.metrics.TryGetValue("cyclomatic", out var v) ? (int)v : 0);
                var avg = methods.Count > 0 ? (double)total / methods.Count : 0;
                fileTotals["avgCyclomatic"] = Math.Round(avg, 2);
                fileTotals["maxCyclomatic"] = methods.Max(m => m.metrics.TryGetValue("cyclomatic", out var v) ? (int)v : 0);
            }
            else if (metric == "nesting")
            {
                fileTotals["maxNesting"] = methods.Count > 0
                    ? methods.Max(m => m.metrics.TryGetValue("nesting", out var v) ? (int)v : 0)
                    : 0;
            }
            else if (metric == "loc")
            {
                fileTotals["totalLoc"] = methods.Sum(m => m.metrics.TryGetValue("loc", out var v) ? (int)v : 0);
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                scope = "file",
                filePath,
                methodCount = methods.Count,
                fileTotals,
                methods
            },
            totalCount: methods.Count,
            returnedCount: methods.Count
        );
    }

    private Dictionary<string, object> CalculateComplexityMetrics(SyntaxNode node, List<string> requestedMetrics)
    {
        var result = new Dictionary<string, object>();

        foreach (var metric in requestedMetrics)
        {
            switch (metric.ToLowerInvariant())
            {
                case "cyclomatic":
                    result["cyclomatic"] = CalculateCyclomaticComplexity(node);
                    break;
                case "nesting":
                    result["nesting"] = CalculateNestingDepth(node);
                    break;
                case "loc":
                    result["loc"] = CalculateLinesOfCode(node);
                    break;
                case "parameters":
                    result["parameters"] = CountParameters(node);
                    break;
                case "cognitive":
                    result["cognitive"] = CalculateCognitiveComplexity(node);
                    break;
            }
        }

        return result;
    }

    private int CalculateCyclomaticComplexity(SyntaxNode node)
    {
        // Start with 1 (the method itself is one path)
        int complexity = 1;

        foreach (var descendant in node.DescendantNodes())
        {
            switch (descendant)
            {
                case IfStatementSyntax:
                case ConditionalExpressionSyntax: // ?:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case CatchClauseSyntax:
                case ConditionalAccessExpressionSyntax: // ?.
                    complexity++;
                    break;
                case BinaryExpressionSyntax binary when
                    binary.Kind() == SyntaxKind.LogicalAndExpression ||
                    binary.Kind() == SyntaxKind.LogicalOrExpression ||
                    binary.Kind() == SyntaxKind.CoalesceExpression: // ??
                    complexity++;
                    break;
            }
        }

        return complexity;
    }

    private int CalculateNestingDepth(SyntaxNode node)
    {
        int maxDepth = 0;
        CalculateNestingDepthRecursive(node, 0, ref maxDepth);
        return maxDepth;
    }

    private void CalculateNestingDepthRecursive(SyntaxNode node, int currentDepth, ref int maxDepth)
    {
        foreach (var child in node.ChildNodes())
        {
            int newDepth = currentDepth;

            if (child is IfStatementSyntax ||
                child is WhileStatementSyntax ||
                child is ForStatementSyntax ||
                child is ForEachStatementSyntax ||
                child is SwitchStatementSyntax ||
                child is TryStatementSyntax ||
                child is LockStatementSyntax ||
                child is UsingStatementSyntax)
            {
                newDepth = currentDepth + 1;
                maxDepth = Math.Max(maxDepth, newDepth);
            }

            CalculateNestingDepthRecursive(child, newDepth, ref maxDepth);
        }
    }

    private int CalculateLinesOfCode(SyntaxNode node)
    {
        var text = node.ToFullString();
        var lines = text.Split('\n')
            .Where(line =>
            {
                var trimmed = line.Trim();
                // Exclude empty lines and comment-only lines
                return !string.IsNullOrWhiteSpace(trimmed) &&
                       !trimmed.StartsWith("//") &&
                       !trimmed.StartsWith("/*") &&
                       !trimmed.StartsWith("*");
            })
            .Count();
        return lines;
    }

    private int CountParameters(SyntaxNode node)
    {
        if (node is MethodDeclarationSyntax method)
        {
            return method.ParameterList.Parameters.Count;
        }
        return 0;
    }

    private int CalculateCognitiveComplexity(SyntaxNode node)
    {
        // Cognitive complexity is similar to cyclomatic but penalizes nesting
        int complexity = 0;
        CalculateCognitiveComplexityRecursive(node, 0, ref complexity);
        return complexity;
    }

    private void CalculateCognitiveComplexityRecursive(SyntaxNode node, int nestingLevel, ref int complexity)
    {
        foreach (var child in node.ChildNodes())
        {
            int increment = 0;
            int newNesting = nestingLevel;

            switch (child)
            {
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case SwitchStatementSyntax:
                case CatchClauseSyntax:
                    increment = 1 + nestingLevel; // Base + nesting penalty
                    newNesting = nestingLevel + 1;
                    break;
                case ConditionalExpressionSyntax: // ?:
                    increment = 1 + nestingLevel;
                    break;
                case BinaryExpressionSyntax binary when
                    binary.Kind() == SyntaxKind.LogicalAndExpression ||
                    binary.Kind() == SyntaxKind.LogicalOrExpression:
                    increment = 1; // No nesting penalty for && and ||
                    break;
                case ElseClauseSyntax:
                    increment = 1; // else adds complexity
                    break;
            }

            complexity += increment;
            CalculateCognitiveComplexityRecursive(child, newNesting, ref complexity);
        }
    }

    /// <summary>
    /// Add null check guard clauses to method parameters.
    /// </summary>
    public async Task<object> AddNullChecksAsync(
        string filePath,
        int line,
        int column,
        bool preview = true)
    {
        EnsureSolutionLoaded();

        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        var semanticModel = await document.GetSemanticModelAsync();

        if (syntaxTree == null || semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file",
                context: new { filePath }
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var position = GetPosition(syntaxTree, line, column);
        var node = root.FindToken(position).Parent;

        // Find the method
        var methodNode = node?.AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault();

        if (methodNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method found at this position",
                hint: "Position cursor on a method declaration",
                context: new { filePath, line, column }
            );
        }

        // Get parameters that are reference types and could be null
        var nullableParams = methodNode.ParameterList.Parameters
            .Where(p =>
            {
                var paramSymbol = semanticModel.GetDeclaredSymbol(p);
                if (paramSymbol == null) return false;

                var type = paramSymbol.Type;
                // Check if it's a reference type or nullable value type
                return type.IsReferenceType ||
                       type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            })
            .Select(p => p.Identifier.Text)
            .ToList();

        if (nullableParams.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    message = "No nullable parameters found that need null checks",
                    methodName = methodNode.Identifier.Text
                }
            );
        }

        // Generate null check code
        var nullChecks = new StringBuilder();
        foreach (var param in nullableParams)
        {
            nullChecks.AppendLine($"        ArgumentNullException.ThrowIfNull({param});");
        }

        // Find where to insert (after opening brace of method body)
        var body = methodNode.Body;
        if (body == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Method has no body (possibly expression-bodied)",
                hint: "This tool works with block-bodied methods",
                context: new { methodName = methodNode.Identifier.Text }
            );
        }

        var openBrace = body.OpenBraceToken;
        var insertPosition = openBrace.Span.End;

        // Get the text and create the modified version
        var sourceText = await document.GetTextAsync();
        var newText = sourceText.Replace(new TextSpan(insertPosition, 0), "\n" + nullChecks.ToString());

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    methodName = methodNode.Identifier.Text,
                    parametersWithNullChecks = nullableParams,
                    generatedCode = nullChecks.ToString().Trim(),
                    changes = new[]
                    {
                        new
                        {
                            filePath,
                            insertAfterLine = openBrace.GetLocation().GetLineSpan().StartLinePosition.Line,
                            newCode = nullChecks.ToString().Trim()
                        }
                    }
                },
                suggestedNextTools: new[] { "add_null_checks with preview=false to apply" }
            );
        }

        // Apply the change
        await File.WriteAllTextAsync(filePath, newText.ToString());

        // Reload solution to pick up changes
        _documentCache.Clear();
        _compilationCache.Clear();

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName = methodNode.Identifier.Text,
                parametersWithNullChecks = nullableParams,
                generatedCode = nullChecks.ToString().Trim()
            },
            suggestedNextTools: new[] { "get_diagnostics to verify changes" }
        );
    }

    /// <summary>
    /// Generate Equals, GetHashCode, and operators for a type.
    /// </summary>
    public async Task<object> GenerateEqualityMembersAsync(
        string filePath,
        int line,
        int column,
        bool includeOperators = true,
        bool preview = true)
    {
        EnsureSolutionLoaded();

        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        var semanticModel = await document.GetSemanticModelAsync();

        if (syntaxTree == null || semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file",
                context: new { filePath }
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var position = GetPosition(syntaxTree, line, column);
        var node = root.FindToken(position).Parent;

        // Find the type declaration
        var typeNode = node?.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (typeNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "No type declaration found at this position",
                hint: "Position cursor on a class or struct declaration",
                context: new { filePath, line, column }
            );
        }

        var typeSymbol = semanticModel.GetDeclaredSymbol(typeNode) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get type symbol",
                context: new { typeName = typeNode.Identifier.Text }
            );
        }

        var typeName = typeSymbol.Name;

        // Get all fields and auto-properties to include in equality
        var members = typeSymbol.GetMembers()
            .Where(m =>
                (m is IFieldSymbol field && !field.IsStatic && !field.IsConst) ||
                (m is IPropertySymbol prop && !prop.IsStatic && prop.GetMethod != null))
            .Select(m => m.Name)
            .ToList();

        if (members.Count == 0)
        {
            return CreateSuccessResponse(
                data: new
                {
                    message = "No instance fields or properties found to compare",
                    typeName
                }
            );
        }

        // Generate Equals method
        var equalsCode = new StringBuilder();
        equalsCode.AppendLine($@"
    public override bool Equals(object? obj)
    {{
        return obj is {typeName} other && Equals(other);
    }}

    public bool Equals({typeName}? other)
    {{
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return {string.Join(" && ", members.Select(m => $"{m} == other.{m}"))};
    }}

    public override int GetHashCode()
    {{
        return HashCode.Combine({string.Join(", ", members.Take(8))});
    }}");

        if (includeOperators)
        {
            equalsCode.AppendLine($@"
    public static bool operator ==({typeName}? left, {typeName}? right)
    {{
        return Equals(left, right);
    }}

    public static bool operator !=({typeName}? left, {typeName}? right)
    {{
        return !Equals(left, right);
    }}");
        }

        // Find where to insert (before closing brace of type)
        var closeBrace = typeNode.CloseBraceToken;
        var insertPosition = closeBrace.SpanStart;

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    typeName,
                    membersCompared = members,
                    includeOperators,
                    generatedCode = equalsCode.ToString().Trim()
                },
                suggestedNextTools: new[] { "generate_equality_members with preview=false to apply" }
            );
        }

        // Apply the change
        var sourceText = await document.GetTextAsync();
        var newText = sourceText.Replace(new TextSpan(insertPosition, 0), equalsCode.ToString());
        await File.WriteAllTextAsync(filePath, newText.ToString());

        // Clear cache
        _documentCache.Clear();

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                typeName,
                membersCompared = members,
                includeOperators
            },
            suggestedNextTools: new[] { "get_diagnostics to verify changes" }
        );
    }


}
