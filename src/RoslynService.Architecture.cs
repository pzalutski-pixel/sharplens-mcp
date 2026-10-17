using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// check_architecture: enforce dependency rules like {from: "MyApp.Domain*",
// deny: "MyApp.Infrastructure*"} over the real type-dependency graph (the same collector
// find_god_objects uses, CollectTypeDependencyEdgesAsync: every type-to-type edge of a
// type's own declaration, with one representative syntax location per edge). Each source
// type is checked once, a multi-targeted project's types under the project's plain name.
// Violations are listed by source type, then target type. Read-only: reports
// violations, changes nothing.
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

        // Every source type in the solution, remembering which logical project declares
        // it (needed for project-level rules). Keyed by cross-compilation identity: an
        // edge target is the type as its SOURCE's compilation binds it, which for a type
        // from another project is a distinct (retargeted) instance of the same
        // declaration, and must still resolve to its declaring project. A multi-targeted
        // project's types are collected once per target framework (each compiles its own
        // #if branches) and merge into one source type.
        var logicalProjects = GetLogicalProjects();
        var allTypes = new List<INamedTypeSymbol>();
        var projectOf = new Dictionary<INamedTypeSymbol, string>(CrossCompilationSymbolComparer.Instance);
        foreach (var project in logicalProjects.Projects.SelectMany(p => p.Projects))
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            foreach (var type in GetAllNamedTypes(compilation))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;
                allTypes.Add(type);
                projectOf.TryAdd((INamedTypeSymbol)type.OriginalDefinition, logicalProjects.Of(project).Name);
            }
        }

        // Include edges to referenced/metadata types so deny rules can target
        // package and BCL namespaces, not just in-solution source types.
        var edges = await CollectTypeDependencyEdgesAsync(allTypes, includeMetadataTargets: true, cancellationToken);

        string ScopeOf(INamedTypeSymbol type) => normalizedLevel == "project"
            ? projectOf.GetValueOrDefault((INamedTypeSymbol)type.OriginalDefinition, type.ContainingAssembly?.Name ?? "")
            : type.ContainingNamespace?.ToDisplayString() ?? "";

        var violations = new List<(string fromType, string toType, ArchitectureRule rule, Location? location)>();
        var edgesChecked = 0;
        foreach (var (source, outbound) in edges)
        {
            var sourceScope = ScopeOf(source);
            foreach (var (target, location) in outbound)
            {
                edgesChecked++;
                var targetScope = ScopeOf(target);
                var rule = rules.FirstOrDefault(r =>
                    MatchesGlobPattern(sourceScope, r.From) && MatchesGlobPattern(targetScope, r.Deny));
                // One violation entry per edge, the first matching rule.
                if (rule != null) violations.Add((source.ToDisplayString(), target.ToDisplayString(), rule, location));
            }
        }

        var ordered = violations
            .OrderBy(v => v.fromType, StringComparer.Ordinal)
            .ThenBy(v => v.toType, StringComparer.Ordinal)
            .Select(v => new
            {
                fromType = v.fromType,
                toType = v.toType,
                rule = new { from = v.rule.From, deny = v.rule.Deny },
                location = v.location is { IsInSource: true } loc
                    ? new
                    {
                        filePath = FormatPath(loc.GetLineSpan().Path),
                        line = loc.GetLineSpan().StartLinePosition.Line
                    }
                    : null as object
            })
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                level = normalizedLevel,
                rulesEvaluated = rules.Count,
                edgesChecked,
                violationCount = ordered.Count,
                violations = ordered
            },
            ResultCounts.Complete(ordered.Count),
            suggestedNextTools: ordered.Count > 0
                ? new[]
                {
                    "find_references on a violating fromType to map the full dependency",
                    "Introduce an abstraction in the allowed layer and invert the dependency"
                }
                : new[] { "No violations: the dependency rules hold" });
    }
}
