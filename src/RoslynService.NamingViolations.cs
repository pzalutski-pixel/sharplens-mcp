using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// find_naming_violations: the naming audit the compiler's IDE1006 analyzer performs,
// honoring the user's own .editorconfig dotnet_naming_* rules (EditorConfigNamingRuleSet:
// the compiler's rule order, required modifiers, every capitalization, severity none
// silencing what it claims), falling back to conventional defaults (PascalCase types,
// methods, local functions and properties; camelCase parameters and locals; _camelCase
// private instance fields — const and static fields and const locals exempt) when a file
// has no naming rules. The symbols checked are the ones IDE1006 checks: namespaces,
// types, type parameters, methods, properties, fields, events, local functions, every
// parameter (including those of overrides, interface implementations, delegates,
// indexers, operators and class primary constructors) and every local — but not the
// names of overriding or interface-implementing members, a record's primary-constructor
// parameters, the entry point, or discards. Names are compared without an '@' escape.
// Rules resolve per syntax tree, honoring nested .editorconfigs. Each violation is
// reported once whatever the number of target frameworks that compile it.
public partial class RoslynService
{
    private const int MaxNamingViolationResults = 1000;

    public async Task<object> FindNamingViolationsAsync(
        string? projectName = null,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxNamingViolationResults) is { } invalid)
            return CreateErrorResponse(invalid);

        var (scope, scopeError) = ResolveProjectScope(projectName);
        if (scope == null) return scopeError!;

        // Naming rules resolve per syntax tree: each tree's analyzer-config options (root +
        // nested .editorconfigs, already merged by Roslyn) decide its rule set, falling back
        // to the conventional defaults when a tree carries no dotnet_naming_* keys.
        var treeRuleCache = new Dictionary<SyntaxTree, (EditorConfigNamingRuleSet RuleSet, bool DefaultsMode)>();
        var appliedSources = new HashSet<string>();
        var appliedRules = new HashSet<string>(StringComparer.Ordinal);
        var ignoredRules = new Dictionary<string, string>(StringComparer.Ordinal);

        var raw = new List<(NamingCandidate Candidate, EditorConfigNamingRule Rule, string Reason, ResultLocation Location, SourceSpanKey Span, string? TargetFramework)>();

        foreach (var project in scope)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Base compilation, deliberately: this audit covers the source the developer
            // authored and can rename; candidates are kept to the project's own documents,
            // so generated code is never reported.
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);
            var documentTrees = new HashSet<SyntaxTree>();
            foreach (var document in project.Documents)
            {
                if (await document.GetSyntaxTreeAsync(cancellationToken) is { } tree) documentTrees.Add(tree);
            }

            var candidates = new List<NamingCandidate>();
            CollectDeclaredSymbols(compilation, candidates, cancellationToken);
            foreach (var tree in documentTrees)
            {
                CollectBodySymbols(compilation.GetSemanticModel(tree), await tree.GetRootAsync(cancellationToken), candidates, cancellationToken);
            }

            foreach (var candidate in candidates)
            {
                if (candidate.Location.SourceTree is not { } tree || !documentTrees.Contains(tree)) continue;
                if (candidate.Name.Length == 0 || candidate.Name.All(c => c == '_')) continue;

                var (ruleSet, defaultsMode) = ResolveForTree(tree);
                // The defaults target conventional mutable state: const and static fields
                // and const locals follow other conventions.
                if (defaultsMode && (candidate.Kind is "field" or "local")
                    && (candidate.Modifiers.Contains("const") || (candidate.Kind == "field" && candidate.Modifiers.Contains("static"))))
                {
                    continue;
                }

                var rule = ruleSet.Rules.FirstOrDefault(r => r.AppliesTo(candidate.Kind, candidate.Accessibility, candidate.Modifiers));
                if (rule == null || rule.IsSilenced) continue;
                if (rule.Check(candidate.Name) is not { } reason) continue;

                var at = ResultLocation.From(candidate.Location)!.Value;
                raw.Add((candidate, rule, reason, at with { FilePath = FormatPath(at.FilePath) },
                    SourceSpanKey.From(candidate.Location)!.Value, targetFramework));
            }
        }

        var violations = CanonicalOrder.ByLocation(
            TargetFrameworkDeduplication.By(
                raw, v => (v.Span, v.Candidate.Name), EqualityComparer<(SourceSpanKey, string)>.Default, v => v.TargetFramework),
            v => v.Item.Location);
        var displayed = violations.Take(maxResults).Select(v => new
        {
            symbol = v.Item.Candidate.Display,
            name = v.Item.Candidate.Name,
            kind = v.Item.Candidate.Kind,
            rule = v.Item.Rule.Name,
            expected = v.Item.Rule.ExpectedDescription(),
            reason = v.Item.Reason,
            location = new { filePath = v.Item.Location.FilePath, line = v.Item.Location.Line, column = v.Item.Location.Column },
            targetFrameworks = FrameworksOrNull(v.TargetFrameworks)
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                rulesSource = appliedSources.Count == 0 ? "defaults"
                    : appliedSources.Count > 1 ? "mixed"
                    : appliedSources.First(),
                rulesEvaluated = appliedRules.Count,
                unsupportedRules = ignoredRules
                    .Select(u => new { ruleName = u.Key, reason = u.Value }).ToList(),
                violationCount = violations.Count,
                violations = displayed
            },
            ResultCounts.Capped(violations.Count, displayed.Count),
            suggestedNextTools: violations.Count > 0
                ? new[] { "rename_symbol (by typeName+memberName) to fix a violation solution-wide" }
                : new[] { "No naming violations under the active rule set" });

        // Resolve (and cache) the naming rule set governing a given syntax tree,
        // aggregating the sources/rules actually applied for the response.
        (EditorConfigNamingRuleSet RuleSet, bool DefaultsMode) ResolveForTree(SyntaxTree tree)
        {
            if (treeRuleCache.TryGetValue(tree, out var cached)) return cached;

            (EditorConfigNamingRuleSet RuleSet, bool DefaultsMode) resolved = (DefaultNamingRules, true);
            var document = _solution!.GetDocument(tree);
            if (document != null)
            {
                var options = document.Project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(tree);
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
                    if (parsed.Rules.Count > 0 || parsed.Ignored.Count > 0)
                        resolved = (parsed, false);
                }
            }

            treeRuleCache[tree] = resolved;
            appliedSources.Add(resolved.DefaultsMode ? "defaults" : "editorconfig");
            foreach (var rule in resolved.RuleSet.Rules) appliedRules.Add(rule.Name);
            foreach (var (name, reason) in resolved.RuleSet.Ignored) ignoredRules[name] = reason;
            return resolved;
        }
    }

    // The symbols declared at type and namespace level: namespaces, types, their type
    // parameters and members, and the parameters of every method-like member.
    private static void CollectDeclaredSymbols(Compilation compilation, List<NamingCandidate> into, CancellationToken ct)
    {
        var entryPoint = compilation.GetEntryPoint(ct);
        Walk(compilation.Assembly.GlobalNamespace);

        void Walk(INamespaceSymbol ns)
        {
            if (!ns.IsGlobalNamespace) Add(ns, "namespace");
            foreach (var type in ns.GetTypeMembers()) AddType(type);
            foreach (var child in ns.GetNamespaceMembers()) Walk(child);
        }

        void AddType(INamedTypeSymbol type)
        {
            if (type.IsImplicitlyDeclared) return;
            Add(type, type.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Struct => "struct",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => type.TypeKind.ToString().ToLowerInvariant()
            });
            foreach (var typeParameter in type.TypeParameters) Add(typeParameter, "type_parameter");
            if (type.DelegateInvokeMethod is { } invoke) AddParameters(invoke.Parameters);

            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                switch (member)
                {
                    case INamedTypeSymbol nested:
                        AddType(nested);
                        break;
                    case IMethodSymbol method:
                        AddMethod(method);
                        break;
                    case IPropertySymbol property:
                        if (!property.IsIndexer && !NameIsDictated(property)) Add(property, "property");
                        AddParameters(property.Parameters);
                        break;
                    // Enum members are not checked, as the compiler does not check them.
                    case IFieldSymbol field when type.TypeKind != TypeKind.Enum:
                        Add(field, "field");
                        break;
                    case IEventSymbol eventSymbol:
                        if (!NameIsDictated(eventSymbol)) Add(eventSymbol, "event");
                        break;
                }
            }
        }

        void AddMethod(IMethodSymbol method)
        {
            switch (method.MethodKind)
            {
                case MethodKind.Ordinary:
                    if (!NameIsDictated(method) && !SymbolEqualityComparer.Default.Equals(method, entryPoint)) Add(method, "method");
                    foreach (var typeParameter in method.TypeParameters) Add(typeParameter, "type_parameter");
                    AddParameters(method.Parameters);
                    break;
                case MethodKind.Constructor:
                    // A record's primary-constructor parameters are its properties' names.
                    if (!(IsPrimaryConstructor(method) && method.ContainingType.IsRecord)) AddParameters(method.Parameters);
                    break;
                case MethodKind.UserDefinedOperator:
                case MethodKind.Conversion:
                    AddParameters(method.Parameters);
                    break;
            }
        }

        void AddParameters(IEnumerable<IParameterSymbol> parameters)
        {
            foreach (var parameter in parameters) Add(parameter, "parameter");
        }

        void Add(ISymbol symbol, string kind)
        {
            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            if (location == null) return;
            into.Add(new NamingCandidate(NamingDisplay(symbol), kind, symbol.Name, AccessibilityKeyOf(symbol), ModifiersOf(symbol), location));
        }
    }

    // The symbols declared inside bodies: locals (declarations, for/using/fixed, foreach,
    // catch, out/pattern/deconstruction designations), local functions with their
    // parameters and type parameters, and lambda parameters.
    private static void CollectBodySymbols(SemanticModel model, SyntaxNode root, List<NamingCandidate> into, CancellationToken ct)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax { Parent.Parent: LocalDeclarationStatementSyntax or ForStatementSyntax or UsingStatementSyntax or FixedStatementSyntax } declarator:
                    Add(model.GetDeclaredSymbol(declarator, ct), "local");
                    break;
                case ForEachStatementSyntax forEach:
                    Add(model.GetDeclaredSymbol(forEach, ct), "local");
                    break;
                case CatchDeclarationSyntax { Identifier.RawKind: not 0 } catchDeclaration:
                    Add(model.GetDeclaredSymbol(catchDeclaration, ct), "local");
                    break;
                case SingleVariableDesignationSyntax designation:
                    Add(model.GetDeclaredSymbol(designation, ct), "local");
                    break;
                case LocalFunctionStatementSyntax localFunction when model.GetDeclaredSymbol(localFunction, ct) is IMethodSymbol function:
                    Add(function, "local_function");
                    foreach (var typeParameter in function.TypeParameters) Add(typeParameter, "type_parameter");
                    foreach (var parameter in function.Parameters) Add(parameter, "parameter");
                    break;
                case AnonymousFunctionExpressionSyntax lambda when model.GetSymbolInfo(lambda, ct).Symbol is IMethodSymbol lambdaSymbol:
                    foreach (var parameter in lambdaSymbol.Parameters) Add(parameter, "parameter");
                    break;
            }
        }

        void Add(ISymbol? symbol, string kind)
        {
            if (symbol == null || symbol.Locations.FirstOrDefault(l => l.IsInSource) is not { } location) return;
            into.Add(new NamingCandidate(NamingDisplay(symbol), kind, symbol.Name, AccessibilityKeyOf(symbol), ModifiersOf(symbol), location));
        }
    }

    // An overriding or interface-implementing member's name is dictated by the member it
    // overrides or implements.
    private static bool NameIsDictated(ISymbol member)
    {
        if (member.IsOverride) return true;
        if (member is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
            or IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 }
            or IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 }) return true;
        var type = member.ContainingType;
        return type != null && type.AllInterfaces
            .SelectMany(i => i.GetMembers(member.Name))
            .Any(i => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(i), member));
    }

    // How a violation names its symbol: locals and parameters by what they are, everything
    // else by its display string.
    private static string NamingDisplay(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => $"local '{local.Name}'",
        IParameterSymbol parameter => $"{parameter.ContainingSymbol.ToDisplayString()} parameter '{parameter.Name}'",
        _ => symbol.ToDisplayString()
    };

    // The modifiers a naming rule's required_modifiers match against, as the compiler
    // reads them from the symbol (a const field is static too).
    private static IReadOnlySet<string> ModifiersOf(ISymbol symbol)
    {
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol is IMethodSymbol { IsAsync: true }) modifiers.Add("async");
        if (symbol is IFieldSymbol { IsReadOnly: true }) modifiers.Add("readonly");
        if (symbol is IFieldSymbol { IsConst: true } or ILocalSymbol { IsConst: true }) modifiers.Add("const");
        return modifiers;
    }

    // The fallback conventions: PascalCase types, methods, local functions and properties;
    // camelCase parameters and locals; _camelCase private fields (FindNamingViolationsAsync
    // exempts const and static fields and const locals in this mode).
    private static readonly EditorConfigNamingRuleSet DefaultNamingRules = new(
        EditorConfigNamingRuleSet.Order(new[]
        {
            DefaultRule("default_types", new[] { "class", "struct", "interface", "enum", "delegate" }, null, "pascal_case", ""),
            DefaultRule("default_methods", new[] { "method", "local_function" }, null, "pascal_case", ""),
            DefaultRule("default_properties", new[] { "property" }, null, "pascal_case", ""),
            DefaultRule("default_parameters_and_locals", new[] { "parameter", "local" }, null, "camel_case", ""),
            DefaultRule("default_private_fields", new[] { "field" }, new[] { "private" }, "camel_case", "_"),
        }),
        Array.Empty<(string, string)>());

    private static EditorConfigNamingRule DefaultRule(
        string name, string[] kinds, string[]? accessibilities, string capitalization, string prefix) =>
        new(name, kinds, accessibilities ?? EditorConfigNamingRule.AllAccessibilities, Array.Empty<string>(),
            capitalization, prefix, "", "", "warning");

    // A primary constructor's symbol declares to the type declaration itself (record,
    // or C# 12 class/struct with a parameter list), not a ConstructorDeclarationSyntax.
    private static bool IsPrimaryConstructor(IMethodSymbol constructor) =>
        constructor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax);

    // The accessibility value a naming rule's applicable_accessibilities matches; "local"
    // for a symbol without accessibility (locals, parameters, local functions, type
    // parameters).
    private static string AccessibilityKeyOf(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected_internal",
        Accessibility.ProtectedAndInternal => "private_protected",
        _ => "local"
    };
}
