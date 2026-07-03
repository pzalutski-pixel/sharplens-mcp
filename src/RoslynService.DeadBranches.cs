using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SharpLensMcp;

// find_dead_branches: unreachable basic blocks per method body, from the real
// control-flow graph (ControlFlowGraph.Create + IsReachable) rather than
// syntax heuristics — so code after return/throw, constant-false branches,
// and code after non-breaking infinite loops all surface uniformly. Top-level
// bodies only: a local function's or lambda's interior flow is its own graph
// and is not walked in v1.
public partial class RoslynService
{
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
        if (maxResults is < 1 or > 500)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxResults must be between 1 and 500 (got {maxResults})",
                context: new { maxResults });
        }

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
            documents = new List<Document> { document };
        }
        else
        {
            var project = _solution!.Projects.FirstOrDefault(
                p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            if (project == null)
            {
                return ProjectNotFoundError(projectName!);
            }
            documents = project.Documents.ToList();
        }

        var deadBranches = new List<object>();
        var methodsAnalyzed = 0;
        var totalFound = 0;

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            var root = semanticModel == null ? null : await semanticModel.SyntaxTree.GetRootAsync(cancellationToken);
            if (semanticModel == null || root == null) continue;

            foreach (var declaration in root.DescendantNodes().Where(n =>
                         n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax
                         // Expression-bodied properties/indexers (`int Foo => …`) have no
                         // accessor node but DO have a control-flow graph.
                         || n is PropertyDeclarationSyntax { ExpressionBody: { } }
                         || n is IndexerDeclarationSyntax { ExpressionBody: { } }))
            {
                var graph = TryCreateControlFlowGraph(semanticModel, declaration, cancellationToken);
                if (graph == null) continue;
                methodsAnalyzed++;

                foreach (var block in graph.Blocks)
                {
                    if (block.IsReachable || block.Operations.IsEmpty) continue;

                    totalFound++;
                    if (deadBranches.Count >= maxResults) continue;

                    var firstOperation = block.Operations[0].Syntax;
                    var lineSpan = firstOperation.GetLocation().GetLineSpan();
                    var snippetText = firstOperation.ToString();
                    deadBranches.Add(new
                    {
                        method = DescribeBodyOwner(declaration, semanticModel, cancellationToken),
                        location = new
                        {
                            filePath = FormatPath(lineSpan.Path),
                            line = lineSpan.StartLinePosition.Line
                        },
                        snippet = TruncateForSnippet(snippetText, 160)
                    });
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                scope = filePath != null ? "file" : "project",
                methodsAnalyzed,
                deadBranches
            },
            suggestedNextTools: deadBranches.Count > 0
                ? new[]
                {
                    "Delete the unreachable statements (they can never execute), or fix the condition that strands them",
                    "analyze_control_flow on a flagged method for the full reachability picture"
                }
                : new[] { "No unreachable blocks: every statement in scope can execute" },
            totalCount: totalFound,
            returnedCount: deadBranches.Count);
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

    private static string DescribeBodyOwner(
        SyntaxNode declaration, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (declaration is AccessorDeclarationSyntax accessor)
        {
            var propertyName = accessor.Ancestors().OfType<BasePropertyDeclarationSyntax>().FirstOrDefault() switch
            {
                PropertyDeclarationSyntax property => property.Identifier.Text,
                IndexerDeclarationSyntax => "this[]",
                EventDeclarationSyntax eventDecl => eventDecl.Identifier.Text,
                _ => "?"
            };
            return $"{propertyName}.{accessor.Keyword.Text}";
        }

        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
        return symbol != null
            ? $"{symbol.ContainingType?.Name}.{symbol.Name}".TrimStart('.')
            : declaration is BaseMethodDeclarationSyntax
                ? declaration.ToString().Split('(')[0].Split(' ').Last()
                : "?";
    }
}
