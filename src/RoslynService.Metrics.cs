using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// get_complexity_metrics: cyclomatic, cognitive, nesting, LOC, and parameter
// counts per method and per file. Members are named as C# declares them (a constructor
// by its type name, "operator +", "this[].get") and located at their name, not at an
// attribute above it.
public partial class RoslynService
{
    private static readonly IReadOnlyList<string> ComplexityMetricNames =
        new[] { "cyclomatic", "nesting", "loc", "parameters", "cognitive" };

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

        // Every requested metric must be a known one (matched case-insensitively); an empty
        // list could only return nothing.
        var requestedMetrics = new List<string>();
        if (metrics != null)
        {
            if (metrics.Count == 0)
            {
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"metrics must name at least one of: {string.Join(", ", ComplexityMetricNames)}",
                    hint: "Omit metrics for all of them, or list the ones you need.",
                    context: new { parameter = "metrics", allowedValues = ComplexityMetricNames });
            }
            foreach (var metric in metrics)
            {
                if (ParameterValidation.Choice("metrics", metric, ComplexityMetricNames, out var canonical) is { } invalid)
                    return CreateErrorResponse(invalid);
                if (!requestedMetrics.Contains(canonical!)) requestedMetrics.Add(canonical!);
            }
        }
        else
        {
            requestedMetrics.AddRange(ComplexityMetricNames);
        }

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
                return CreateSuccessResponse(
                    data: new
                    {
                        scope = accessor != null ? "accessor" : "property",
                        name = ComplexityMemberName(memberNode),
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
                    name = ComplexityMemberName(methodNode),
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
                    name = ComplexityMemberName(m),
                    containingType = symbol?.ContainingType?.Name,
                    line = DeclarationNameToken(m).GetLocation().GetLineSpan().StartLinePosition.Line,
                    metrics = methodMetrics
                };
            })
            .ToList();

        // Calculate file-level totals. Max() is guarded against an empty method set
        // (interfaces, enums, DTOs).
        var fileTotals = new Dictionary<string, object>();
        double Avg(string key) => methods.Count > 0
            ? Math.Round(methods.Sum(m => m.metrics.TryGetValue(key, out var v) ? (int)v : 0) / (double)methods.Count, 2)
            : 0;
        int Max(string key) => methods.Count > 0
            ? methods.Max(m => m.metrics.TryGetValue(key, out var v) ? (int)v : 0)
            : 0;
        foreach (var metric in requestedMetrics)
        {
            switch (metric)
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
                filePath = FormatPath(document.FilePath),
                methodCount = methods.Count,
                fileTotals,
                methods
            },
            ResultCounts.Complete(methods.Count));
    }

    // The requested metrics (canonical names) of one member.
    private Dictionary<string, object> CalculateComplexityMetrics(SyntaxNode node, List<string> requestedMetrics)
    {
        var result = new Dictionary<string, object>();

        foreach (var metric in requestedMetrics)
        {
            switch (metric)
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

    // The single cyclomatic counter shared by get_complexity_metrics and find_untested_code's
    // complexity (EstimateCyclomaticAsync), so the two tools never report different numbers
    // for the same member. One plus one per decision point: if, ?:, ?., ??, ??=, every
    // loop, catch and catch filter, case label and non-discard switch-expression arm, `when`
    // clause, and each && / || / pattern and / pattern or.
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
                case CatchFilterClauseSyntax: // catch (…) when (…)
                case WhenClauseSyntax: // case … when …, switch-expression arm … when …
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
                case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression): // ??=
                    complexity++;
                    break;
                case BinaryPatternSyntax: // pattern `and` / `or`
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

    // The deepest stack of nested control structures. An `else if` continues its if/else
    // chain at the chain's level: it is not one level deeper than the `if` it follows.
    private void CalculateNestingDepthRecursive(SyntaxNode node, int currentDepth, ref int maxDepth)
    {
        foreach (var child in node.ChildNodes())
        {
            int newDepth = currentDepth;

            if ((child is IfStatementSyntax && child.Parent is not ElseClauseSyntax) ||
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

    // A member's name as C# declares it, used by BOTH the cursor and file-scope paths so
    // the two never name one member differently: a constructor by its type name, a
    // destructor "~Type", "operator +", "implicit operator int", an indexer "this[]", an
    // accessor "<property, indexer or event>.<keyword>".
    private static string ComplexityMemberName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
        DestructorDeclarationSyntax d => $"~{d.Identifier.ValueText}",
        OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}",
        ConversionOperatorDeclarationSyntax co => $"{co.ImplicitOrExplicitKeyword.Text} operator {co.Type}",
        LocalFunctionStatementSyntax lf => lf.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        IndexerDeclarationSyntax => "this[]",
        AccessorDeclarationSyntax a => $"{AccessorOwnerName(a)}.{a.Keyword.Text}",
        _ => "member"
    };

    // The property, indexer ("this[]") or event an accessor belongs to.
    private static string AccessorOwnerName(AccessorDeclarationSyntax accessor) =>
        accessor.Ancestors().OfType<BasePropertyDeclarationSyntax>().FirstOrDefault() switch
        {
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            IndexerDeclarationSyntax => "this[]",
            EventDeclarationSyntax eventDeclaration => eventDeclaration.Identifier.ValueText,
            _ => "?"
        };

    // The token that names a member declaration — where its location is reported.
    private static SyntaxToken DeclarationNameToken(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier,
        ConstructorDeclarationSyntax c => c.Identifier,
        DestructorDeclarationSyntax d => d.Identifier,
        OperatorDeclarationSyntax o => o.OperatorKeyword,
        ConversionOperatorDeclarationSyntax co => co.OperatorKeyword,
        LocalFunctionStatementSyntax lf => lf.Identifier,
        PropertyDeclarationSyntax p => p.Identifier,
        IndexerDeclarationSyntax i => i.ThisKeyword,
        AccessorDeclarationSyntax a => a.Keyword,
        _ => node.GetFirstToken()
    };

    private int CalculateCognitiveComplexity(SyntaxNode node)
    {
        int complexity = 0;
        CalculateCognitiveComplexityRecursive(node, 0, ref complexity);
        return complexity;
    }

    // Sonar's cognitive complexity: +1 per break in linear flow (if, else if, else, ?:,
    // switch statement or expression, loop, catch, goto, each run of like boolean or
    // pattern operators) plus, for the nesting structures (if, ?:, switch, loop, catch),
    // the current nesting level. Those structures nest what they contain, and so do
    // lambdas, anonymous methods and local functions (no increment of their own).
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
                case SwitchExpressionSyntax:
                case CatchClauseSyntax:
                    increment = 1 + nestingLevel; // Base + nesting penalty
                    newNesting = nestingLevel + 1;
                    break;
                case ConditionalExpressionSyntax: // ?:
                    increment = 1 + nestingLevel;
                    newNesting = nestingLevel + 1;
                    break;
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                    newNesting = nestingLevel + 1;
                    break;
                case GotoStatementSyntax:
                    increment = 1;
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
                // Pattern `and` / `or` runs, counted the same way.
                case BinaryPatternSyntax pattern
                    when pattern.Parent is not BinaryPatternSyntax parentPattern || parentPattern.Kind() != pattern.Kind():
                    increment = 1;
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
