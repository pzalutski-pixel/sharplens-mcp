using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// find_naming_violations: naming-convention audit honoring the user's own
// .editorconfig dotnet_naming_* rules, falling back to conventional defaults
// (PascalCase types/methods/properties, camelCase parameters/locals,
// _camelCase private instance fields) when none are configured. v1 scope,
// stated in the schema: kind + accessibility filters, pascal_case/camel_case
// (incl. word_separator), required prefix/suffix; rules using required_modifiers
// or other capitalizations are reported unsupported; severity=none disables a
// rule; among several matching rules the most specific (most kind/accessibility
// filters) wins. Rules resolve per syntax tree, honoring nested .editorconfigs.
public partial class RoslynService
{
    public async Task<object> FindNamingViolationsAsync(
        string? projectName = null,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (maxResults is < 1 or > 1000)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxResults must be between 1 and 1000 (got {maxResults})",
                context: new { maxResults });
        }

        // An unknown project NAME is an invalid argument (routed through the shared
        // ProjectNotFoundError, like the sibling tools). A null name on a solution with
        // no projects is not an error — it falls through to an empty result rather than
        // calling ProjectNotFoundError(null).
        if (projectName != null && !_solution!.Projects.Any(
                p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectNotFoundError(projectName);
        }

        var projects = (projectName == null
                ? _solution!.Projects
                : _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Naming rules resolve per syntax tree: each tree's analyzer-config
        // options (root + nested .editorconfigs, already merged by Roslyn) decide
        // its rule set, falling back to the conventional defaults when a tree
        // carries no dotnet_naming_* keys. Resolutions are cached per tree and
        // their sources aggregated for the report.
        var treeRuleCache = new Dictionary<SyntaxTree, (EditorConfigNamingRuleSet ruleSet, bool defaultsMode)>();
        var appliedSources = new HashSet<string>();
        var appliedRules = new Dictionary<string, EditorConfigNamingRule>();
        var appliedUnsupported = new Dictionary<string, string>();

        // Collected with a sort key (the symbol display) so the set kept at the
        // maxResults boundary is deterministic — sorted and truncated after the full
        // walk rather than capped in the (walk-order) middle.
        var violations = new List<(string sortKey, object payload)>();
        var totalFound = 0;

        void Record(string symbolDisplay, string kind, string name,
            string accessibility, Location? location)
        {
            var (ruleSet, _) = ResolveForTree(location?.SourceTree);

            // Among every rule that applies to this symbol, the most specific
            // (most kind/accessibility filters) wins; ties break by name for a
            // deterministic choice instead of dictionary-iteration order.
            var rule = ruleSet.Rules
                .Where(r => (r.Kinds.Count == 0 || r.Kinds.Contains(kind))
                    && (r.Accessibilities.Count == 0 || r.Accessibilities.Contains(accessibility)))
                .OrderByDescending(r => r.Specificity)
                .ThenBy(r => r.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (rule == null || rule.IsSatisfiedBy(name)) return;

            totalFound++;
            var lineSpan = location is { } loc && loc.IsInSource ? loc.GetLineSpan() : default;
            violations.Add((symbolDisplay, new
            {
                symbol = symbolDisplay,
                kind,
                rule = rule.Name,
                expected = rule.ExpectedDescription(),
                location = location is { IsInSource: true }
                    ? new { filePath = FormatPath(lineSpan.Path), line = lineSpan.StartLinePosition.Line }
                    : null as object
            }));
        }

        foreach (var project in projects)
        {
            // Base compilation, deliberately: this audit covers the source the
            // developer authored and can rename (its next-tool hint is rename_symbol,
            // which only works on source symbols). The base compilation's trees are
            // exactly the project's own documents — no generated trees — and a
            // symbol's name reads identically from the base or augmented compilation,
            // so the augmented one would only add generated-code names the developer
            // cannot rename. (Siblings like find_async_issues use the augmented
            // compilation because a generated async-void is a real bug; a generated
            // name is not.)
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            foreach (var type in GetAllNamedTypes(compilation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type.IsImplicitlyDeclared) continue;
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;

                Record(type.ToDisplayString(), EditorConfigKindOf(type), type.Name,
                    AccessibilityKeyOf(type.DeclaredAccessibility),
                    type.Locations.First(l => l.IsInSource));

                foreach (var member in type.GetMembers())
                {
                    if (member.IsImplicitlyDeclared || member.IsOverride) continue;
                    if (member is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
                        or IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 }
                        or IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 }) continue;

                    var location = member.Locations.FirstOrDefault(l => l.IsInSource);
                    if (location == null) continue;

                    switch (member)
                    {
                        case IMethodSymbol { MethodKind: MethodKind.Ordinary } method:
                            Record(method.ToDisplayString(), "method", method.Name,
                                AccessibilityKeyOf(method.DeclaredAccessibility), location);
                            RecordParameters(method);
                            break;
                        case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                            // Primary-constructor parameters (records and C# 12 classes/structs)
                            // seed or become properties and follow PascalCase, not the camelCase
                            // parameter convention — they are not violations.
                            if (!IsPrimaryConstructor(constructor)) RecordParameters(constructor);
                            break;
                        case IPropertySymbol { IsIndexer: false } property:
                            Record(property.ToDisplayString(), "property", property.Name,
                                AccessibilityKeyOf(property.DeclaredAccessibility), location);
                            break;
                        case IFieldSymbol field:
                            // Defaults target conventional mutable instance state:
                            // const and static fields follow different conventions
                            // and stay out of the fallback _camelCase rule.
                            if ((field.IsConst || field.IsStatic)
                                && ResolveForTree(location.SourceTree).defaultsMode) break;
                            Record(field.ToDisplayString(), "field", field.Name,
                                AccessibilityKeyOf(field.DeclaredAccessibility), location);
                            break;
                        case IEventSymbol eventSymbol:
                            Record(eventSymbol.ToDisplayString(), "event", eventSymbol.Name,
                                AccessibilityKeyOf(eventSymbol.DeclaredAccessibility), location);
                            break;
                    }
                }
            }

            // Locals are syntax-only: their kind and name need no symbol.
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root == null) continue;
                foreach (var node in root.DescendantNodes())
                {
                    switch (node)
                    {
                        // `var x = ...;` / `using var x = ...;`
                        case VariableDeclaratorSyntax declarator
                            when declarator.Parent?.Parent is LocalDeclarationStatementSyntax:
                            RecordLocal(declarator.Identifier);
                            break;
                        // `foreach (var x in ...)` — the loop variable is a token, not a designation.
                        case ForEachStatementSyntax forEach:
                            RecordLocal(forEach.Identifier);
                            break;
                        // `out var x`, `is T x` pattern captures, deconstruction
                        // (`var (a, b) = ...`), and `foreach (var (a, b) in ...)` elements.
                        case SingleVariableDesignationSyntax designation:
                            RecordLocal(designation.Identifier);
                            break;
                    }
                }
            }
        }

        // Deterministic truncation: order by the symbol display, then keep maxResults.
        var displayedViolations = violations
            .OrderBy(v => v.sortKey, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(v => v.payload)
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                rulesSource = appliedSources.Count == 0 ? "defaults"
                    : appliedSources.Count > 1 ? "mixed"
                    : appliedSources.First(),
                rulesEvaluated = appliedRules.Count,
                unsupportedRules = appliedUnsupported
                    .Select(u => new { ruleName = u.Key, reason = u.Value }).ToList(),
                violationCount = totalFound,
                violations = displayedViolations
            },
            suggestedNextTools: totalFound > 0
                ? new[] { "rename_symbol (by typeName+memberName) to fix a violation solution-wide" }
                : new[] { "No naming violations under the active rule set" },
            totalCount: totalFound,
            returnedCount: displayedViolations.Count);

        void RecordParameters(IMethodSymbol method)
        {
            foreach (var parameter in method.Parameters)
            {
                var location = parameter.Locations.FirstOrDefault(l => l.IsInSource);
                if (location == null) continue;
                Record($"{method.ToDisplayString()} parameter '{parameter.Name}'", "parameter",
                    parameter.Name, "local", location);
            }
        }

        void RecordLocal(SyntaxToken identifier) =>
            Record($"local '{identifier.Text}'", "local", identifier.Text,
                "local", identifier.GetLocation());

        // Resolve (and cache) the naming rule set governing a given syntax tree,
        // aggregating the sources/rules actually applied for the response.
        (EditorConfigNamingRuleSet ruleSet, bool defaultsMode) ResolveForTree(SyntaxTree? tree)
        {
            if (tree == null) return (DefaultNamingRules, true);
            if (treeRuleCache.TryGetValue(tree, out var cached)) return cached;

            (EditorConfigNamingRuleSet ruleSet, bool defaultsMode) resolved = (DefaultNamingRules, true);
            var document = _solution!.GetDocument(tree);
            if (document != null)
            {
                var options = document.Project.AnalyzerOptions
                    .AnalyzerConfigOptionsProvider.GetOptions(tree);
                var namingPairs = new List<KeyValuePair<string, string>>();
                foreach (var key in options.Keys)
                {
                    if (!key.StartsWith("dotnet_naming_", StringComparison.OrdinalIgnoreCase)) continue;
                    if (options.TryGetValue(key, out var value))
                        namingPairs.Add(new KeyValuePair<string, string>(key, value));
                }
                if (namingPairs.Count > 0)
                {
                    var parsed = EditorConfigNamingRuleSet.Parse(namingPairs);
                    if (parsed.Rules.Count > 0 || parsed.Unsupported.Count > 0)
                        resolved = (parsed, false);
                }
            }

            treeRuleCache[tree] = resolved;
            appliedSources.Add(resolved.defaultsMode ? "defaults" : "editorconfig");
            foreach (var rule in resolved.ruleSet.Rules) appliedRules[rule.Name] = rule;
            foreach (var (name, reason) in resolved.ruleSet.Unsupported) appliedUnsupported[name] = reason;
            return resolved;
        }
    }

    // The fallback conventions: PascalCase types/methods/properties, camelCase
    // parameters/locals, _camelCase private instance fields.
    private static readonly EditorConfigNamingRuleSet DefaultNamingRules = new(
        new[]
        {
            new EditorConfigNamingRule("default_types",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "class", "struct", "interface", "enum", "delegate" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "pascal_case", null, null),
            new EditorConfigNamingRule("default_methods",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "method" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "pascal_case", null, null),
            new EditorConfigNamingRule("default_properties",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "property" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "pascal_case", null, null),
            new EditorConfigNamingRule("default_parameters_and_locals",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "parameter", "local" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "camel_case", null, null),
            new EditorConfigNamingRule("default_private_fields",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "field" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "private" },
                "camel_case", "_", null)
        },
        Array.Empty<(string, string)>());

    // A primary constructor's symbol declares to the type declaration itself (record,
    // or C# 12 class/struct with a parameter list), not a ConstructorDeclarationSyntax.
    private static bool IsPrimaryConstructor(IMethodSymbol constructor) =>
        constructor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax);

    private static string EditorConfigKindOf(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Class => "class",
        TypeKind.Struct => "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => type.TypeKind.ToString().ToLowerInvariant()
    };

    private static string AccessibilityKeyOf(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected_internal",
        Accessibility.ProtectedAndInternal => "private_protected",
        _ => accessibility.ToString().ToLowerInvariant()
    };
}
