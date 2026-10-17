using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// The dependency graphs of dependency_graph and find_circular_dependencies, and the shape
// both tools report a graph and its cycles in.
//   - Project graph: one node per logical project (a multi-targeted project is one node
//     carrying its target frameworks), in logical-project order; an edge where any member
//     project references a member of another. Node ids are unique and valid Mermaid
//     identifiers, so same-named projects (two project files named Lib) and names that
//     sanitize alike (My.Lib, My_Lib) stay distinct nodes.
//   - Namespace graph: one node per namespace declaring a type in the solution, ordered
//     ordinally (the global namespace is "<global namespace>"); an edge N -> M where code
//     in N uses a type declared in M or a member of one — through a qualified name, an
//     alias, a static or extension member alike. A using directive is not a dependency by
//     itself: an unused one adds nothing, and a using static or alias directive never makes
//     a type a node.
public partial class RoslynService
{
    private const string GlobalNamespaceName = "<global namespace>";

    private DependencyGraph BuildProjectGraph()
    {
        var model = GetLogicalProjects();
        var projects = model.Projects;
        var ids = MermaidNodeIds(projects.Select(p => p.Name).ToList());
        var indexOf = projects.Select((project, index) => (project, index)).ToDictionary(p => p.project, p => p.index);

        var nodes = projects
            .Select((project, index) => new DependencyNode(
                ids[index],
                project.Name,
                project.FilePath == null ? null : FormatPath(project.FilePath),
                project.IsMultiTargeted ? project.TargetFrameworks : Array.Empty<string>()))
            .ToList();

        var edges = new HashSet<(int From, int To)>();
        foreach (var logical in projects)
        {
            foreach (var member in logical.Projects)
            {
                foreach (var reference in member.ProjectReferences)
                {
                    if (_solution!.GetProject(reference.ProjectId) is { } target)
                    {
                        edges.Add((indexOf[logical], indexOf[model.Of(target)]));
                    }
                }
            }
        }

        return new DependencyGraph(
            nodes,
            edges.OrderBy(e => e.From).ThenBy(e => e.To).Select(e => new DependencyEdge(ids[e.From], ids[e.To])).ToList());
    }

    private async Task<DependencyGraph> BuildNamespaceGraphAsync(CancellationToken cancellationToken)
    {
        var projects = GetLogicalProjects().Projects.SelectMany(p => p.Projects).ToList();
        var compilations = new List<Compilation>();
        foreach (var project in projects)
        {
            if (await GetProjectCompilationAsync(project, cancellationToken) is { } compilation) compilations.Add(compilation);
        }

        // The namespaces declaring a solution type, and the names a use of a solution type
        // or member can be spelled with (type and member names, and every alias), so only
        // those names are bound.
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var bindableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var compilation in compilations)
        {
            foreach (var type in GetAllNamedTypes(compilation))
            {
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                    || !type.Locations.Any(l => l.IsInSource)) continue;
                namespaces.Add(NamespaceName(type.ContainingNamespace));
                bindableNames.Add(type.Name);
                bindableNames.UnionWith(type.MemberNames);
            }
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = await tree.GetRootAsync(cancellationToken);
                foreach (var alias in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                {
                    if (alias.Alias?.Name.Identifier.ValueText is { } aliasName) bindableNames.Add(aliasName);
                }
            }
        }

        var edges = new HashSet<(string From, string To)>();
        foreach (var compilation in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await tree.GetRootAsync(cancellationToken);
                var model = compilation.GetSemanticModel(tree);
                foreach (var name in root.DescendantNodes(n => n is not UsingDirectiveSyntax).OfType<SimpleNameSyntax>())
                {
                    if (!bindableNames.Contains(name.Identifier.ValueText)) continue;
                    if (UsedSourceType(model.GetSymbolInfo(name, cancellationToken).Symbol) is not { } used) continue;
                    var from = EnclosingNamespaceName(name, model, cancellationToken);
                    var to = NamespaceName(used.ContainingNamespace);
                    if (from != to) edges.Add((from, to));
                }
            }
        }

        var nodes = namespaces
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new DependencyNode(n, n, null, Array.Empty<string>()))
            .ToList();
        return new DependencyGraph(
            nodes,
            edges.Where(e => namespaces.Contains(e.From) && namespaces.Contains(e.To))
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .Select(e => new DependencyEdge(e.From, e.To))
                .ToList());
    }

    // The solution type a name's symbol uses: the type itself, or the type declaring a
    // member (the static class of an extension method). Null for anything else, and for a
    // type without source (framework and package types are never graph nodes).
    private static INamedTypeSymbol? UsedSourceType(ISymbol? symbol)
    {
        var type = symbol switch
        {
            INamedTypeSymbol named => named,
            IMethodSymbol method => (method.ReducedFrom ?? method).ContainingType,
            IPropertySymbol or IFieldSymbol or IEventSymbol => symbol.ContainingType,
            _ => null
        };
        var definition = type?.OriginalDefinition;
        return definition != null && definition.Locations.Any(l => l.IsInSource) ? definition : null;
    }

    private static string EnclosingNamespaceName(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } declaration
            && model.GetDeclaredSymbol(declaration, cancellationToken) is INamespaceSymbol declared
            ? NamespaceName(declared)
            : GlobalNamespaceName;

    private static string NamespaceName(INamespaceSymbol? ns) =>
        ns == null || ns.IsGlobalNamespace ? GlobalNamespaceName : ns.ToDisplayString();

    // A unique, valid Mermaid node id per name, in order: characters other than letters,
    // digits and '_' become '_', a leading digit is prefixed with '_', Mermaid's reserved
    // word "end" becomes "end_", and a name whose id is already taken gets the first free
    // "_2", "_3", ... suffix.
    private static List<string> MermaidNodeIds(IReadOnlyList<string> names)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>();
        foreach (var name in names)
        {
            var sanitized = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            if (sanitized.Length == 0 || char.IsAsciiDigit(sanitized[0])) sanitized = "_" + sanitized;
            if (sanitized == "end") sanitized = "end_";
            var id = sanitized;
            for (var suffix = 2; !used.Add(id); suffix++) id = $"{sanitized}_{suffix}";
            ids.Add(id);
        }
        return ids;
    }

    // A graph as Mermaid flowchart text: every node declared with its name as the label,
    // then every edge.
    private static string RenderMermaid(DependencyGraph graph) =>
        string.Join("\n", new[] { "graph TD" }
            .Concat(graph.Nodes.Select(n => $"  {n.Id}[\"{n.Name.Replace("\"", "#quot;")}\"]"))
            .Concat(graph.Edges.Select(e => $"  {e.From} --> {e.To}")));

    // The nodes of a graph as the tools report them: { id, name }, plus a project's
    // filePath and, when multi-targeted, its targetFrameworks.
    private static List<Dictionary<string, object?>> FormatGraphNodes(DependencyGraph graph) =>
        graph.Nodes.Select(n =>
        {
            var node = new Dictionary<string, object?> { ["id"] = n.Id, ["name"] = n.Name };
            if (n.FilePath != null) node["filePath"] = n.FilePath;
            return WithTargetFrameworks(node, n.TargetFrameworks);
        }).ToList();

    private static List<object> FormatGraphEdges(DependencyGraph graph) =>
        graph.Edges.Select(e => (object)new { from = e.From, to = e.To }).ToList();

    // The counts of a cycle report: complete, or bounded when the cycle search hit its cap.
    private static ResultCounts CycleCounts(CycleAnalysis analysis) =>
        analysis.Truncated
            ? ResultCounts.Bounded(analysis.Cycles.Count, truncated: true)
            : ResultCounts.Complete(analysis.Cycles.Count);
}
