using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// get_complexity_metrics: cyclomatic, cognitive, nesting, LOC, and parameter
// counts per method and per file. Metrics analysis, kept apart from the codegen
// and edit tools it formerly shared a file with.
public partial class RoslynService
{
    /// <summary>
    /// Get complexity metrics for a method or file.
    /// </summary>
    public async Task<object> GetComplexityMetricsAsync(
        string filePath,
        int? line = null,
        int? column = null,
        List<string>? metrics = null,
        CancellationToken cancellationToken = default)
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

        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);

        if (syntaxTree == null || semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file",
                context: new { filePath }
            );
        }

        var root = await syntaxTree.GetRootAsync(cancellationToken);

        // Default to all metrics if none specified
        var requestedMetrics = metrics ?? new List<string>
        {
            "cyclomatic", "nesting", "loc", "parameters", "cognitive"
        };

        // If line is specified, find the method at that position
        if (line.HasValue)
        {
            var (pos, posError) = await ResolvePositionAsync(filePath, line.Value, column ?? 0, cancellationToken);
            if (pos == null) return posError!;
            var node = pos.Token.Parent;

            // The nearest method, constructor, operator, or local function — the same
            // member kinds the file scope enumerates.
            var methodNode = node?.AncestorsAndSelf()
                .FirstOrDefault(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);

            if (methodNode == null)
            {
                // A property/event accessor, or an expression-bodied property/indexer
                // (no accessor list, so it is matched on the declaration itself).
                var accessor = node?.AncestorsAndSelf()
                    .OfType<AccessorDeclarationSyntax>()
                    .FirstOrDefault();
                var expressionBodied = accessor != null ? null : node?.AncestorsAndSelf()
                    .FirstOrDefault(n => (n is PropertyDeclarationSyntax pd && pd.ExpressionBody != null)
                                      || (n is IndexerDeclarationSyntax id && id.ExpressionBody != null));

                var memberNode = accessor ?? expressionBodied;
                if (memberNode == null)
                {
                    return CreateErrorResponse(
                        ErrorCodes.SymbolNotFound,
                        "No method, constructor, operator, local function, accessor, or expression-bodied property found at this position",
                        hint: "Position cursor inside a member body",
                        context: new { filePath, line, column }
                    );
                }

                var memberMetrics = CalculateComplexityMetrics(memberNode, requestedMetrics);
                var memberSymbol = accessor != null ? null : semanticModel.GetDeclaredSymbol(memberNode, cancellationToken);
                return CreateSuccessResponse(
                    data: new
                    {
                        scope = accessor != null ? "accessor" : "property",
                        name = ComplexityMemberName(memberNode, memberSymbol),
                        metrics = memberMetrics
                    },
                    suggestedNextTools: new[]
                    {
                        memberMetrics.TryGetValue("cyclomatic", out var accCC) && (int)accCC > 10
                            ? "Consider refactoring - cyclomatic complexity > 10"
                            : null
                    }.Where(s => s != null).ToArray()!
                );
            }

            var methodMetrics = CalculateComplexityMetrics(methodNode, requestedMetrics);
            var methodSymbol = semanticModel.GetDeclaredSymbol(methodNode, cancellationToken);

            return CreateSuccessResponse(
                data: new
                {
                    scope = "method",
                    name = ComplexityMemberName(methodNode, methodSymbol),
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

        // Analyze whole file — all method-like members: methods, constructors,
        // operators/conversions (BaseMethodDeclaration), property/event accessors, and
        // expression-bodied properties/indexers (which have no accessor list, so they are
        // matched directly; their `{ get => ... }` form is already covered by the accessor
        // case). Local functions are NOT listed separately: their branches are already
        // counted inside the containing member's metrics, so a separate entry would
        // double-count the file totals.
        var methods = root.DescendantNodes()
            .Where(n => (n is BaseMethodDeclarationSyntax bm && (bm.Body != null || bm.ExpressionBody != null))
                     || (n is AccessorDeclarationSyntax acc && (acc.Body != null || acc.ExpressionBody != null))
                     || (n is PropertyDeclarationSyntax pd && pd.ExpressionBody != null)
                     || (n is IndexerDeclarationSyntax id && id.ExpressionBody != null))
            .Select(m =>
            {
                var symbol = semanticModel.GetDeclaredSymbol(m, cancellationToken);
                var methodMetrics = CalculateComplexityMetrics(m, requestedMetrics);
                return new
                {
                    name = ComplexityMemberName(m, symbol),
                    containingType = symbol?.ContainingType?.Name,
                    line = m.GetLocation().GetLineSpan().StartLinePosition.Line,
                    metrics = methodMetrics
                };
            })
            .ToList();

        // Calculate file-level totals
        var fileTotals = new Dictionary<string, object>();
        // Match the metric name case-insensitively, exactly as the per-method
        // CalculateComplexityMetrics does (it switches on metric.ToLowerInvariant());
        // a case-sensitive == here would silently drop file totals for "Cyclomatic".
        // Max() is guarded against an empty method set (interfaces, enums, DTOs).
        double Avg(string key) => methods.Count > 0
            ? Math.Round(methods.Sum(m => m.metrics.TryGetValue(key, out var v) ? (int)v : 0) / (double)methods.Count, 2)
            : 0;
        int Max(string key) => methods.Count > 0
            ? methods.Max(m => m.metrics.TryGetValue(key, out var v) ? (int)v : 0)
            : 0;
        foreach (var metric in requestedMetrics)
        {
            switch (metric.ToLowerInvariant())
            {
                case "cyclomatic":
                    fileTotals["avgCyclomatic"] = Avg("cyclomatic");
                    fileTotals["maxCyclomatic"] = Max("cyclomatic");
                    break;
                case "nesting":
                    fileTotals["maxNesting"] = Max("nesting");
                    break;
                case "loc":
                    fileTotals["totalLoc"] = methods.Sum(m => m.metrics.TryGetValue("loc", out var v) ? (int)v : 0);
                    break;
                case "cognitive":
                    fileTotals["avgCognitive"] = Avg("cognitive");
                    fileTotals["maxCognitive"] = Max("cognitive");
                    break;
                case "parameters":
                    // avg + max, symmetric with the other count metrics (cyclomatic/cognitive).
                    fileTotals["avgParameters"] = Avg("parameters");
                    fileTotals["maxParameters"] = Max("parameters");
                    break;
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

    // The single cyclomatic counter shared by get_complexity_metrics and the
    // find_god_objects / find_untested_code estimate (EstimateCyclomaticAsync), so
    // the two tools never report different numbers for the same method.
    internal static int CalculateCyclomaticComplexity(SyntaxNode node)
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
                case DoStatementSyntax: // do { } while (…)
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case CatchClauseSyntax:
                case ConditionalAccessExpressionSyntax: // ?.
                    complexity++;
                    break;
                // A switch-expression arm is a branch like a case label; the catch-all
                // discard arm is the fall-through (mirrors not counting `default:`).
                case SwitchExpressionArmSyntax arm when arm.Pattern is not DiscardPatternSyntax:
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
                child is DoStatementSyntax ||
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
        // Distinct source lines spanned by the node's actual code tokens. Comments and
        // blank lines carry no tokens, so they are excluded without a per-line string
        // heuristic (the old one mis-excluded code lines beginning with '*', e.g.
        // `*ptr = x;`, and counted block-comment continuation lines inconsistently).
        var lines = new HashSet<int>();
        foreach (var token in node.DescendantTokens())
        {
            if (token.Span.IsEmpty) continue;
            var span = token.GetLocation().GetLineSpan();
            for (var l = span.StartLinePosition.Line; l <= span.EndLinePosition.Line; l++)
            {
                lines.Add(l);
            }
        }
        return lines.Count;
    }

    private int CountParameters(SyntaxNode node) => node switch
    {
        BaseMethodDeclarationSyntax m => m.ParameterList.Parameters.Count,
        LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters.Count,
        IndexerDeclarationSyntax id => id.ParameterList.Parameters.Count,
        _ => 0
    };

    // A display name for a member when no declared symbol is available — methods,
    // constructors, operators, conversions, destructors, and local functions.
    private static string MemberSyntaxName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}",
        ConversionOperatorDeclarationSyntax => "conversion operator",
        DestructorDeclarationSyntax d => $"~{d.Identifier.Text}",
        LocalFunctionStatementSyntax lf => lf.Identifier.Text,
        _ => "member"
    };

    // The member name used by BOTH the cursor and file-scope complexity paths, so the
    // two never report the same member differently. An accessor reads as
    // "Property.get"/"Property.set"; everything else uses the symbol name.
    private static string ComplexityMemberName(SyntaxNode node, ISymbol? symbol) => node switch
    {
        AccessorDeclarationSyntax acc => acc.Parent?.Parent is PropertyDeclarationSyntax prop
            ? $"{prop.Identifier.Text}.{acc.Keyword.Text}"
            : acc.Keyword.Text,
        _ => symbol?.Name ?? MemberSyntaxName(node)
    };

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
                // An `else if` is an IfStatement that is the ElseClause's statement. The
                // ElseClause already added its flat +1, so the chained if must NOT add
                // another +1+nesting (Sonar counts the if/else-if chain once) — and the
                // chain stays at the current nesting level.
                case IfStatementSyntax when child.Parent is ElseClauseSyntax:
                    break;
                case IfStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
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
                // One increment per RUN of the same boolean operator: `a && b && c` is a
                // single &&-sequence (+1), but `a && b || c` mixes operators (+2). Count
                // only the outermost node of each run (its parent isn't the same operator).
                case BinaryExpressionSyntax binary when
                    (binary.Kind() == SyntaxKind.LogicalAndExpression ||
                     binary.Kind() == SyntaxKind.LogicalOrExpression)
                    && (binary.Parent is not BinaryExpressionSyntax parent || parent.Kind() != binary.Kind()):
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
}
