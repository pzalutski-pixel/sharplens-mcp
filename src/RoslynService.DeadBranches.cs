using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SharpLensMcp;

// find_dead_branches: unreachable basic blocks per method body, from the real
// control-flow graph (ControlFlowGraph.Create + IsReachable) rather than
// syntax heuristics — so code after return/throw, constant-false branches,
// and code after non-breaking infinite loops all surface uniformly. A block whose only
// content is its branch value (a `return 2;` or `throw` after a return) is reported at the
// statement that carries the value. A file is analyzed in every target framework that
// compiles it, each unreachable block reported once with the frameworks it is dead in.
// Top-level bodies only: a local function's or lambda's interior flow is its own graph
// and is not walked.
public partial class RoslynService
{
    private const int MaxDeadBranchResults = 500;

    public async Task<object> FindDeadBranchesAsync(
        string? filePath = null,
        string? projectName = null,
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if ((filePath == null) == (projectName == null))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "Supply exactly one of filePath (scan one file) or projectName (scan a project).",
                context: new { filePath, projectName });
        }
        if (ParameterValidation.Limit("maxResults", maxResults, MaxDeadBranchResults) is { } invalid)
            return CreateErrorResponse(invalid);

        List<Document> documents;
        if (filePath != null)
        {
            var document = TryFindDocument(filePath);
            if (document == null)
            {
                return CreateErrorResponse(
                    ErrorCodes.FileNotInSolution,
                    $"File not found in solution: {filePath}",
                    hint: "Check the file path or reload the solution",
                    context: new { filePath });
            }
            documents = DocumentsSharingPath(document);
        }
        else
        {
            var (scope, scopeError) = ResolveProjectScope(projectName);
            if (scope == null) return scopeError!;
            documents = scope.SelectMany(p => p.Documents).ToList();
        }

        var found = new List<(string Method, ResultLocation Location, SourceSpanKey Span, string Snippet, string? TargetFramework)>();
        var methods = new HashSet<SourceSpanKey>();

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var root = semanticModel == null ? null : await semanticModel.SyntaxTree.GetRootAsync(cancellationToken);
            if (semanticModel == null || root == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(document.Project);

            foreach (var declaration in root.DescendantNodes().Where(n =>
                         n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax
                         // Expression-bodied properties/indexers (`int Foo => …`) have no
                         // accessor node but DO have a control-flow graph.
                         || n is PropertyDeclarationSyntax { ExpressionBody: { } }
                         || n is IndexerDeclarationSyntax { ExpressionBody: { } }))
            {
                var graph = TryCreateControlFlowGraph(semanticModel, declaration, cancellationToken);
                if (graph == null) continue;
                methods.Add(new SourceSpanKey(declaration.SyntaxTree.FilePath, declaration.Span));

                foreach (var block in graph.Blocks)
                {
                    if (block.IsReachable) continue;
                    var syntax = DeadBlockSyntax(block);
                    if (syntax == null) continue;

                    var location = syntax.GetLocation();
                    var at = ResultLocation.From(location)!.Value;
                    found.Add((
                        DescribeBodyOwner(declaration, semanticModel, cancellationToken),
                        at with { FilePath = FormatPath(at.FilePath) },
                        new SourceSpanKey(syntax.SyntaxTree.FilePath, syntax.Span),
                        TruncateForSnippet(syntax.ToString(), 160),
                        targetFramework));
                }
            }
        }

        var merged = CanonicalOrder.ByLocation(
            TargetFrameworkDeduplication.ByLocation(found, f => f.Span, f => f.TargetFramework),
            m => m.Item.Location);
        var returned = merged.Take(maxResults).ToList();

        return CreateSuccessResponse(
            data: new
            {
                scope = filePath != null ? "file" : "project",
                methodsAnalyzed = methods.Count,
                deadBranches = returned.Select(m => new
                {
                    method = m.Item.Method,
                    location = new { filePath = m.Item.Location.FilePath, line = m.Item.Location.Line, column = m.Item.Location.Column },
                    snippet = m.Item.Snippet,
                    targetFrameworks = FrameworksOrNull(m.TargetFrameworks)
                }).ToList()
            },
            ResultCounts.Capped(merged.Count, returned.Count),
            suggestedNextTools: returned.Count > 0
                ? new[]
                {
                    "Delete the unreachable statements (they can never execute), or fix the condition that strands them",
                    "analyze_control_flow on a flagged method for the full reachability picture"
                }
                : new[] { "No unreachable blocks: every statement in scope can execute" });
    }

    // The source an unreachable block stands for: its first operation, or — for a block
    // that holds nothing but its branch value (`return 2;`, `throw new X();`) — the
    // statement carrying that value. Null for a block with neither (an empty join point).
    private static SyntaxNode? DeadBlockSyntax(BasicBlock block)
    {
        if (!block.Operations.IsEmpty) return block.Operations[0].Syntax;
        if (block.BranchValue is not { } value) return null;
        return value.Syntax.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault()
            ?? value.Syntax.AncestorsAndSelf().OfType<ArrowExpressionClauseSyntax>().FirstOrDefault()?.Expression
            ?? value.Syntax;
    }

    private static ControlFlowGraph? TryCreateControlFlowGraph(
        SemanticModel semanticModel, SyntaxNode declaration, CancellationToken cancellationToken)
    {
        // For an expression-bodied property/indexer the body operation hangs off the
        // arrow clause, not the declaration node.
        var operationNode = declaration switch
        {
            PropertyDeclarationSyntax { ExpressionBody: { } arrow } => (SyntaxNode)arrow,
            IndexerDeclarationSyntax { ExpressionBody: { } arrow } => arrow,
            _ => declaration
        };
        return semanticModel.GetOperation(operationNode, cancellationToken) switch
        {
            IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody, cancellationToken),
            IConstructorBodyOperation constructorBody => ControlFlowGraph.Create(constructorBody, cancellationToken),
            IBlockOperation block => ControlFlowGraph.Create(block, cancellationToken),
            _ => null
        };
    }

    // "Type.Member" as C# declares the member (a constructor by its type name, "operator
    // +", an accessor "Property.get" / "this[].get" / "Event.add").
    private static string DescribeBodyOwner(
        SyntaxNode declaration, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        var member = ComplexityMemberName(declaration);
        if (declaration is AccessorDeclarationSyntax) return member;
        var containingType = declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
            ?? semanticModel.GetDeclaredSymbol(declaration, cancellationToken)?.ContainingType?.Name;
        return containingType == null ? member : $"{containingType}.{member}";
    }
}
