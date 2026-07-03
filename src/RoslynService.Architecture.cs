using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// check_architecture: enforce dependency rules like {from: "MyApp.Domain*",
// deny: "MyApp.Infrastructure*"} over the real type-dependency graph (the
// same collector find_god_objects uses, with one representative syntax
// location per edge). Read-only: reports violations, changes nothing.
public partial class RoslynService
{
    public async Task<object> CheckArchitectureAsync(
        List<ArchitectureRule> rules,
        string level = "namespace",
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (rules.Count == 0 || rules.Any(r => string.IsNullOrWhiteSpace(r.From) || string.IsNullOrWhiteSpace(r.Deny)))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "rules must be a non-empty array of { from, deny } glob patterns",
                hint: "Example: rules: [{ \"from\": \"MyApp.Domain*\", \"deny\": \"MyApp.Infrastructure*\" }]",
                context: new { ruleCount = rules.Count });
        }

        var normalizedLevel = level?.ToLowerInvariant() ?? "namespace";
        if (normalizedLevel is not ("namespace" or "project"))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"level must be 'namespace' or 'project' (got '{level}')",
                context: new { level });
        }

        // Every source type in the solution, remembering which project each
        // came from (needed for project-level rules; a multi-targeted type
        // keeps its first project).
        var allTypes = new List<INamedTypeSymbol>();
        var projectOf = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            foreach (var type in GetAllNamedTypes(compilation))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;
                var key = (INamedTypeSymbol)type.OriginalDefinition;
                if (projectOf.ContainsKey(key)) continue;
                allTypes.Add(type);
                projectOf[key] = project.Name;
            }
        }

        // Include edges to referenced/metadata types so deny rules can target
        // package and BCL namespaces, not just in-solution source types.
        var edges = await CollectTypeDependencyEdgesAsync(allTypes, includeMetadataTargets: true, cancellationToken);

        string ScopeOf(INamedTypeSymbol type) => normalizedLevel == "project"
            ? projectOf.GetValueOrDefault((INamedTypeSymbol)type.OriginalDefinition, type.ContainingAssembly?.Name ?? "")
            : type.ContainingNamespace?.ToDisplayString() ?? "";

        var violations = new List<object>();
        var edgesChecked = 0;
        foreach (var (source, outbound) in edges)
        {
            var sourceScope = ScopeOf(source);
            foreach (var (target, location) in outbound)
            {
                edgesChecked++;
                var targetScope = ScopeOf(target);
                foreach (var rule in rules)
                {
                    if (!MatchesGlobPattern(sourceScope, rule.From)) continue;
                    if (!MatchesGlobPattern(targetScope, rule.Deny)) continue;
                    violations.Add(new
                    {
                        fromType = source.ToDisplayString(),
                        toType = target.ToDisplayString(),
                        rule = new { from = rule.From, deny = rule.Deny },
                        location = location is { } loc && loc.IsInSource
                            ? new
                            {
                                filePath = FormatPath(loc.GetLineSpan().Path),
                                line = loc.GetLineSpan().StartLinePosition.Line
                            }
                            : null as object
                    });
                    break; // one violation entry per edge, first matching rule
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                level = normalizedLevel,
                rulesEvaluated = rules.Count,
                edgesChecked,
                violationCount = violations.Count,
                violations
            },
            suggestedNextTools: violations.Count > 0
                ? new[]
                {
                    "find_references on a violating fromType to map the full dependency",
                    "Introduce an abstraction in the allowed layer and invert the dependency"
                }
                : new[] { "No violations: the dependency rules hold" },
            totalCount: violations.Count,
            returnedCount: violations.Count);
    }
}
