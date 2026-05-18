using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace SharpLensMcp;

// Quality/audit tools: untested-surface detection, god-object detection,
// and the get_project_health composite. These are intended for audit-time
// invocation, not per-edit agent loops — several do O(N) or O(N^2)
// solution-wide work.
public partial class RoslynService
{
    // Test-attribute identity is (namespace, simple name). Matching by name alone
    // would treat `MyApp.Attributes.FactAttribute` as xUnit's [Fact] — false positive
    // that would mark non-test methods as tests and inflate the reachable set.
    private static readonly HashSet<(string ns, string name)> TestAttributeFqns = new()
    {
        // xUnit (both v2 and v3 declare the public attribute classes in the `Xunit` namespace).
        ("Xunit", "FactAttribute"),
        ("Xunit", "TheoryAttribute"),
        // NUnit
        ("NUnit.Framework", "TestAttribute"),
        ("NUnit.Framework", "TestCaseAttribute"),
        ("NUnit.Framework", "TestCaseSourceAttribute"),
        // MSTest
        ("Microsoft.VisualStudio.TestTools.UnitTesting", "TestMethodAttribute"),
        ("Microsoft.VisualStudio.TestTools.UnitTesting", "DataTestMethodAttribute"),
    };

    // Find public/internal surface that no test method transitively reaches.
    // Detects xUnit/NUnit/MSTest test methods by attribute, walks the call graph,
    // then diffs the production project's surface against the reachable set.
    public async Task<object> FindUntestedCodeAsync(
        string? projectName = null,
        bool includeProperties = true,
        bool includeInternal = false,
        int maxResults = 50)
    {
        EnsureSolutionLoaded();

        UntestedCodeData data;
        try
        {
            data = await FindUntestedCodeDataAsync(projectName, includeProperties, includeInternal, maxResults);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                ex.Message,
                context: new { projectName });
        }

        return CreateSuccessResponse(
            data: new
            {
                productionProject = data.ProductionProject,
                testProjectsScanned = data.TestProjectsScanned,
                testMethodCount = data.TestMethodCount,
                reachableSymbolCount = data.ReachableSymbolCount,
                uncoveredSymbols = data.Symbols.Select(s => new
                {
                    fullName = s.FullName,
                    kind = s.Kind,
                    complexity = s.Complexity,
                    accessibility = s.Accessibility,
                    location = s.Location,
                    reason = "Not reachable from any test method"
                }).ToList()
            },
            suggestedNextTools: new[]
            {
                "get_call_graph (callers) to confirm a candidate has no test reaching it",
                "find_references to see all real usages, not just call sites"
            },
            totalCount: data.TotalCount,
            returnedCount: data.Symbols.Count);
    }

    // Typed-data variant used by GetProjectHealthAsync.
    internal async Task<UntestedCodeData> FindUntestedCodeDataAsync(
        string? projectName,
        bool includeProperties,
        bool includeInternal,
        int maxResults)
    {
        // Step 1: enumerate test methods across the whole solution.
        var testMethods = new List<IMethodSymbol>();
        var testProjectsScanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project);
            if (compilation == null) continue;

            var found = 0;
            foreach (var type in GetAllNamedTypes(compilation))
            {
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (IsTestMethod(method))
                    {
                        testMethods.Add(method);
                        found++;
                    }
                }
            }
            if (found > 0) testProjectsScanned.Add(project.Name);
        }

        // Step 2: BFS the reachable set from every test method.
        var reachable = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>();
        foreach (var tm in testMethods)
        {
            if (reachable.Add(tm.OriginalDefinition)) queue.Enqueue(tm);
        }
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var callee in await CollectReferencedSymbolsAsync(current))
            {
                if (callee is IMethodSymbol mc && reachable.Add(mc.OriginalDefinition))
                {
                    queue.Enqueue(mc);
                }
                else
                {
                    reachable.Add(callee.OriginalDefinition);
                }
            }
        }

        // Step 3: target project's surface diff.
        var targetProject = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects.FirstOrDefault()
            : _solution!.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        if (targetProject == null)
        {
            throw new ArgumentException($"Project not found: {projectName}");
        }

        var targetCompilation = await GetProjectCompilationAsync(targetProject);
        if (targetCompilation == null)
        {
            throw new ArgumentException($"Could not get compilation for project '{targetProject.Name}'");
        }

        var uncovered = new List<(ISymbol symbol, int complexity)>();
        foreach (var type in GetAllNamedTypes(targetCompilation))
        {
            if (!IsInTargetProject(type, targetProject)) continue;
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                if (!IsCandidate(member, includeProperties, includeInternal)) continue;
                if (member is IMethodSymbol m && IsTestMethod(m)) continue;
                if (reachable.Contains(member.OriginalDefinition)) continue;

                var complexity = member is IMethodSymbol ms ? await EstimateCyclomaticAsync(ms) : 1;
                uncovered.Add((member, complexity));
            }
        }

        uncovered.Sort((a, b) => b.complexity.CompareTo(a.complexity));
        var totalCount = uncovered.Count;
        var capped = uncovered.Take(maxResults).Select(u => new UncoveredSymbolEntry(
            FullName: u.symbol.ToDisplayString(),
            Kind: u.symbol.Kind.ToString(),
            Complexity: u.complexity,
            Accessibility: u.symbol.DeclaredAccessibility.ToString(),
            Location: GetSymbolLocation(u.symbol))).ToList();

        return new UntestedCodeData(
            ProductionProject: targetProject.Name,
            TestProjectsScanned: testProjectsScanned.ToList(),
            TestMethodCount: testMethods.Count,
            ReachableSymbolCount: reachable.Count,
            TotalCount: totalCount,
            Symbols: capped);
    }

    private static bool IsTestMethod(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (cls == null) continue;
            var ns = cls.ContainingNamespace?.ToDisplayString() ?? "";
            if (TestAttributeFqns.Contains((ns, cls.Name))) return true;
        }
        return false;
    }

    private static bool IsCandidate(ISymbol member, bool includeProperties, bool includeInternal)
    {
        // Accessibility filter
        var acc = member.DeclaredAccessibility;
        if (acc == Accessibility.Private || acc == Accessibility.ProtectedAndInternal) return false;
        if (!includeInternal && acc == Accessibility.Internal) return false;

        switch (member)
        {
            case IMethodSymbol m:
                if (m.MethodKind != MethodKind.Ordinary
                    && m.MethodKind != MethodKind.Constructor) return false;
                return true;
            case IPropertySymbol:
                return includeProperties;
            default:
                return false;
        }
    }

    private static bool IsInTargetProject(INamedTypeSymbol type, Project project)
    {
        if (type.DeclaringSyntaxReferences.IsEmpty) return false;
        var path = type.DeclaringSyntaxReferences[0].SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(path)) return false;
        return project.Documents.Any(d => d.FilePath != null &&
            string.Equals(d.FilePath, path, PathComparison));
    }

    // Conservative cyclomatic estimate from declaration syntax: 1 + count of branching nodes.
    private async Task<int> EstimateCyclomaticAsync(IMethodSymbol method)
    {
        var declRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef == null) return 1;
        var syntax = await declRef.GetSyntaxAsync();
        var branches = syntax.DescendantNodes().Count(n =>
            n is IfStatementSyntax
            || n is WhileStatementSyntax
            || n is ForStatementSyntax
            || n is ForEachStatementSyntax
            || n is CaseSwitchLabelSyntax
            || n is CasePatternSwitchLabelSyntax
            || n is ConditionalExpressionSyntax
            || n is CatchClauseSyntax);
        return 1 + branches;
    }

    // Collects every method/property/field symbol the given method's body references.
    // Used for BFS reachability in untested-code detection.
    //
    // IMPORTANT: Must use the SAME compilation (GetProjectCompilationAsync, which runs
    // source generators) as the rest of the tool. document.GetSemanticModelAsync() returns
    // the workspace's default model, which on generator-using projects has DIFFERENT symbol
    // identity than our augmented compilation — SymbolEqualityComparer.Default returns false
    // across compilations, breaking the reachable-set membership check downstream.
    private async Task<IEnumerable<ISymbol>> CollectReferencedSymbolsAsync(IMethodSymbol method)
    {
        var result = new List<ISymbol>();
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            var syntax = await declRef.GetSyntaxAsync();
            var document = _solution!.GetDocument(declRef.SyntaxTree);
            if (document == null) continue;

            var compilation = await GetProjectCompilationAsync(document.Project);
            if (compilation == null) continue;
            var semanticModel = compilation.GetSemanticModel(declRef.SyntaxTree);

            foreach (var inv in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var sym = semanticModel.GetSymbolInfo(inv).Symbol;
                if (sym != null) result.Add(sym.OriginalDefinition);
            }
            foreach (var mem in syntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var sym = semanticModel.GetSymbolInfo(mem).Symbol;
                if (sym != null) result.Add(sym.OriginalDefinition);
            }
            foreach (var oc in syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var sym = semanticModel.GetSymbolInfo(oc).Symbol;
                if (sym != null) result.Add(sym.OriginalDefinition);
            }
        }
        return result;
    }

    // Find types that exceed configurable coupling + size thresholds — likely god-objects.
    // O(types * references) by design; this is an audit-time tool, not for hot-paths.
    public async Task<object> FindGodObjectsAsync(
        string? projectName = null,
        int minEfferentCoupling = 20,
        int minMembers = 20,
        int maxResults = 20)
    {
        EnsureSolutionLoaded();
        var data = await FindGodObjectsDataAsync(projectName, minEfferentCoupling, minMembers, maxResults);
        return CreateSuccessResponse(
            data: new
            {
                threshold = new { efferent = minEfferentCoupling, members = minMembers },
                // Project records to anonymous-typed objects so the wire shape stays
                // camelCase regardless of serializer (System.Text.Json on stdio applies
                // camelCase; Newtonsoft.JObject.FromObject in tests does not).
                candidates = data.Candidates.Select(c => new
                {
                    typeName = c.TypeName,
                    efferentCoupling = c.EfferentCoupling,
                    afferentCoupling = c.AfferentCoupling,
                    memberCount = c.MemberCount,
                    score = c.Score,
                    location = c.Location
                }).ToList()
            },
            suggestedNextTools: new[]
            {
                "get_type_overview on a candidate to see its member breakdown",
                "find_references on a candidate to see who depends on it"
            },
            totalCount: data.TotalCount,
            returnedCount: data.Candidates.Count);
    }

    // Typed-data variant — used by GetProjectHealthAsync to compose without dynamic.
    internal async Task<GodObjectsData> FindGodObjectsDataAsync(
        string? projectName,
        int minEfferentCoupling,
        int minMembers,
        int maxResults)
    {
        var projectsToScan = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        // Step 1: enumerate every source type in scope. GetAllNamedTypes walks the
        // compilation's GlobalNamespace which includes types from referenced assemblies
        // (since the cross-project namespaces are merged at compile time). We MUST
        // restrict to types whose ContainingAssembly matches the current compilation's
        // own assembly — otherwise a project filter still pulls in referenced source
        // types (e.g. RoslynService from SharpLensMcp surfacing when scanning
        // SharpLensMcp.Tests). Without this, the projectName filter is a no-op for
        // intra-source-project types.
        var allTypes = new List<INamedTypeSymbol>();
        foreach (var project in projectsToScan)
        {
            var compilation = await GetProjectCompilationAsync(project);
            if (compilation == null) continue;
            foreach (var type in GetAllNamedTypes(compilation))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;
                allTypes.Add(type);
            }
        }

        // Step 2: per type, compute efferent (outbound) set in one pass; afferent (inbound)
        // by inversion. Storing OriginalDefinition keeps cross-compilation comparisons stable.
        var efferent = new Dictionary<INamedTypeSymbol, HashSet<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        foreach (var type in allTypes)
        {
            efferent[(INamedTypeSymbol)type.OriginalDefinition] =
                new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        }

        foreach (var type in allTypes)
        {
            var key = (INamedTypeSymbol)type.OriginalDefinition;
            var outboundSet = efferent[key];

            foreach (var member in type.GetMembers())
            {
                CollectTypeDependencies(member, outboundSet, key);
            }

            // Inspect bodies for usages. Precise pass only: only count types appearing in
            // semantically meaningful positions — object creation, typeof, cast targets,
            // method-call receivers, and local-variable type declarations. The previous
            // loose pass (every IdentifierNameSyntax that resolves to a type) inflated
            // counts by including nameof() args, generic type parameters in constraints,
            // and using-directive-brought-into-scope types that the class doesn't really
            // "depend on" in a coupling sense.
            foreach (var declRef in type.DeclaringSyntaxReferences)
            {
                var syntax = await declRef.GetSyntaxAsync();
                var document = _solution!.GetDocument(declRef.SyntaxTree);
                if (document == null) continue;
                var compilation = await GetProjectCompilationAsync(document.Project);
                if (compilation == null) continue;
                var semanticModel = compilation.GetSemanticModel(declRef.SyntaxTree);

                foreach (var node in syntax.DescendantNodes())
                {
                    switch (node)
                    {
                        case ObjectCreationExpressionSyntax oc:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(oc).Type, outboundSet, key);
                            break;
                        case ImplicitObjectCreationExpressionSyntax ioc:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(ioc).Type, outboundSet, key);
                            break;
                        case TypeOfExpressionSyntax t:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(t.Type).Type, outboundSet, key);
                            break;
                        case CastExpressionSyntax cast:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(cast.Type).Type, outboundSet, key);
                            break;
                        case VariableDeclarationSyntax vd when vd.Type is not null:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(vd.Type).Type, outboundSet, key);
                            break;
                        case InvocationExpressionSyntax inv:
                            // The receiver's type is a real dependency (we're calling its members).
                            if (semanticModel.GetSymbolInfo(inv).Symbol is IMethodSymbol calledMethod
                                && calledMethod.ContainingType is INamedTypeSymbol ct)
                            {
                                AddTypeFromSymbol(ct, outboundSet, key);
                            }
                            break;
                    }
                }
            }
        }

        // Step 3: invert for afferent counts.
        var afferentCount = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
        foreach (var (source, outboundSet) in efferent)
        {
            foreach (var target in outboundSet)
            {
                afferentCount[target] = afferentCount.GetValueOrDefault(target) + 1;
            }
        }

        // Step 4: candidates + scoring.
        var allCandidates = new List<(INamedTypeSymbol type, int efferent, int afferent, int members, double score)>();
        foreach (var type in allTypes)
        {
            var key = (INamedTypeSymbol)type.OriginalDefinition;
            var eff = efferent[key].Count;
            var aff = afferentCount.GetValueOrDefault(key);
            var memberCount = type.GetMembers().Count(m => !m.IsImplicitlyDeclared);

            if (eff < minEfferentCoupling || memberCount < minMembers) continue;

            var score = eff * 0.4 + memberCount * 0.4 + aff * 0.2;
            allCandidates.Add((type, eff, aff, memberCount, score));
        }

        allCandidates.Sort((a, b) => b.score.CompareTo(a.score));
        var totalCount = allCandidates.Count;
        var capped = allCandidates.Take(maxResults).Select(c => new GodObjectCandidate(
            TypeName: c.type.ToDisplayString(),
            EfferentCoupling: c.efferent,
            AfferentCoupling: c.afferent,
            MemberCount: c.members,
            Score: Math.Round(c.score, 2),
            Location: GetSymbolLocation(c.type))).ToList();

        return new GodObjectsData(totalCount, capped);
    }

    private static void CollectTypeDependencies(ISymbol member, HashSet<INamedTypeSymbol> outbound, INamedTypeSymbol owner)
    {
        switch (member)
        {
            case IMethodSymbol m:
                AddType(m.ReturnType, outbound, owner);
                foreach (var p in m.Parameters) AddType(p.Type, outbound, owner);
                break;
            case IPropertySymbol prop:
                AddType(prop.Type, outbound, owner);
                break;
            case IFieldSymbol field:
                AddType(field.Type, outbound, owner);
                break;
            case IEventSymbol evt:
                AddType(evt.Type, outbound, owner);
                break;
        }
    }

    private static void AddType(ITypeSymbol type, HashSet<INamedTypeSymbol> outbound, INamedTypeSymbol owner)
    {
        if (type is INamedTypeSymbol nts && IsSourceType(nts))
        {
            var def = (INamedTypeSymbol)nts.OriginalDefinition;
            if (!SymbolEqualityComparer.Default.Equals(def, owner))
                outbound.Add(def);
        }
    }

    // Adds a TypeInfo-derived ITypeSymbol (which may be null when semantic resolution fails)
    // to the outbound set with the same source-type + non-self filtering as AddType.
    private static void AddTypeFromSymbol(ITypeSymbol? type, HashSet<INamedTypeSymbol> outbound, INamedTypeSymbol owner)
    {
        if (type == null) return;
        AddType(type, outbound, owner);
    }

    private static bool IsSourceType(INamedTypeSymbol type) =>
        type.Locations.Any(l => l.IsInSource);

    // Composite per-project audit: aggregates diagnostics, unused-code, coupling, and
    // coverage signals via the typed *DataAsync helpers (no dynamic, no reflection).
    // Returns counts + top-N hotspots per dimension so the agent gets the lay of the land
    // in one round-trip.
    public async Task<object> GetProjectHealthAsync(
        string projectName,
        bool includeAnalyzers = true,
        int topN = 5)
    {
        EnsureSolutionLoaded();

        var project = _solution!.Projects.FirstOrDefault(
            p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Project not found: {projectName}",
                hint: "Use get_project_structure to list project names",
                context: new { projectName });
        }

        var diag = await GetDiagnosticsDataAsync(
            filePath: null,
            projectPath: project.FilePath,
            severity: null,
            includeHidden: false,
            runAnalyzers: includeAnalyzers);

        var topDiagsByCount = diag.Diagnostics
            .GroupBy(d => d.Id)
            .OrderByDescending(g => g.Count())
            .Take(topN)
            .Select(g => new { id = g.Key, count = g.Count() })
            .ToList();

        var unused = await FindUnusedCodeDataAsync(
            projectName: project.Name,
            includePrivate: false,
            includeInternal: false,
            symbolKindFilter: null,
            maxResults: topN);

        var unusedByKind = unused.Symbols
            .GroupBy(s => s.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => new { kind = g.Key, count = g.Count() })
            .ToList();

        var god = await FindGodObjectsDataAsync(
            projectName: project.Name,
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: topN);

        UntestedCodeData? untested = null;
        try
        {
            untested = await FindUntestedCodeDataAsync(
                projectName: project.Name,
                includeProperties: false,
                includeInternal: false,
                maxResults: topN);
        }
        catch (ArgumentException)
        {
            // Project has no test references resolvable — treat as zero coverage data
            // rather than failing the whole composite call.
        }
        var untestedCount = untested?.TotalCount ?? 0;
        var untestedHotspots = untested?.Symbols ?? new List<UncoveredSymbolEntry>();

        var summary = $"{diag.ErrorCount} errors, {diag.WarningCount} warnings, {god.TotalCount} god-object candidates, {untestedCount} uncovered public methods, {unused.TotalCount} unused symbols";

        return CreateSuccessResponse(
            data: new
            {
                projectName = project.Name,
                diagnostics = new
                {
                    errorCount = diag.ErrorCount,
                    warningCount = diag.WarningCount,
                    analyzerCount = diag.AnalyzerCount,
                    topByCount = topDiagsByCount
                },
                unusedCode = new
                {
                    count = unused.TotalCount,
                    topByKind = unusedByKind
                },
                coupling = new
                {
                    godObjectCandidates = god.TotalCount,
                    hotspots = god.Candidates.Select(c => new
                    {
                        typeName = c.TypeName,
                        efferentCoupling = c.EfferentCoupling,
                        afferentCoupling = c.AfferentCoupling,
                        memberCount = c.MemberCount,
                        score = c.Score,
                        location = c.Location
                    }).ToList()
                },
                coverage = new
                {
                    uncoveredPublicSurface = untestedCount,
                    hotspots = untestedHotspots.Select(s => new
                    {
                        fullName = s.FullName,
                        kind = s.Kind,
                        complexity = s.Complexity,
                        accessibility = s.Accessibility,
                        location = s.Location
                    }).ToList()
                },
                summary
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics for the full diagnostic list",
                "find_god_objects to expand the coupling hotspots",
                "find_untested_code to expand the coverage gap list",
                "find_unused_code to expand the unused-symbol list"
            });
    }
}
