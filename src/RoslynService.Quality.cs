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
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();
        if (projectName != null && !_solution!.Projects.Any(
                p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectNotFoundError(projectName);
        }

        UntestedCodeData data;
        try
        {
            data = await FindUntestedCodeDataAsync(projectName, includeProperties, includeInternal, maxResults, cancellationToken);
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
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        // Step 1: enumerate test methods across the whole solution.
        var testMethods = new List<IMethodSymbol>();
        var testProjectsScanned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            var found = 0;
            foreach (var type in GetAllNamedTypes(compilation))
            {
                // This project's own source types only: GetAllNamedTypes includes every
                // referenced assembly, so without the filter a test method declared in a
                // referenced test project is counted again here, and the BCL is scanned
                // for test attributes it cannot carry.
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;

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

        // Step 2: BFS the reachable set from every test method. `walked` gates
        // body traversal; `reachable` is the answer set and additionally carries
        // the override/interface declarations a reached method dispatches through.
        var reachable = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var walked = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<IMethodSymbol>();

        // A reached method also exercises the base methods it overrides and the
        // interface members it implements: those declarations are invoked through
        // this method's dispatch, so seed them reachable (a base virtual called
        // via `base.X()` is still enqueued for its own body through the normal
        // callee pass). Without this, a base virtual reached only via an override,
        // or an interface member reached only via its implementation, reads as
        // untested. find_tests shares this seeding so the two tools agree on what
        // a test covers.
        void MarkReached(IMethodSymbol method)
        {
            reachable.Add(method.OriginalDefinition);
            SeedRelatedDeclarations(method, reachable);
            if (walked.Add(method.OriginalDefinition)) queue.Enqueue(method);
        }

        foreach (var tm in testMethods) MarkReached(tm);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var callee in await CollectReferencedSymbolsAsync(current, cancellationToken))
            {
                if (callee is IMethodSymbol mc) MarkReached(mc);
                else reachable.Add(callee.OriginalDefinition);
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

        var targetCompilation = await GetProjectCompilationAsync(targetProject, cancellationToken);
        if (targetCompilation == null)
        {
            throw new ArgumentException($"Could not get compilation for project '{targetProject.Name}'");
        }

        var uncovered = new List<(ISymbol symbol, int complexity)>();
        foreach (var type in GetAllNamedTypes(targetCompilation))
        {
            // Cheap assembly check first so the BCL types in the merged global
            // namespace never reach the per-type file-path comparison.
            if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, targetCompilation.Assembly)) continue;
            if (!IsInTargetProject(type, targetProject)) continue;
            foreach (var member in type.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                if (!IsCandidate(member, includeProperties, includeInternal)) continue;
                if (member is IMethodSymbol m && IsTestMethod(m)) continue;
                if (reachable.Contains(member.OriginalDefinition)) continue;

                var complexity = member is IMethodSymbol ms ? await EstimateCyclomaticAsync(ms, cancellationToken) : 1;
                uncovered.Add((member, complexity));
            }
        }

        // Descending by complexity, then by display name as a stable tie-break so the
        // set kept at the maxResults boundary is deterministic for equal-complexity
        // members (List.Sort is not stable on its own).
        uncovered.Sort((a, b) =>
        {
            var byComplexity = b.complexity.CompareTo(a.complexity);
            return byComplexity != 0
                ? byComplexity
                : string.CompareOrdinal(a.symbol.ToDisplayString(), b.symbol.ToDisplayString());
        });
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

    // Conservative cyclomatic estimate from declaration syntax. Delegates to the
    // shared CalculateCyclomaticComplexity so this estimate and get_complexity_metrics
    // never diverge (the same method must yield the same number from both tools).
    private async Task<int> EstimateCyclomaticAsync(IMethodSymbol method, CancellationToken cancellationToken = default)
    {
        var declRef = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (declRef == null) return 1;
        var syntax = await declRef.GetSyntaxAsync(cancellationToken);
        return CalculateCyclomaticComplexity(syntax);
    }

    // Seeds the declarations a reached method dispatches through: every base
    // method in its override chain and every interface member it implements
    // (explicitly or implicitly). Shared by find_untested_code and find_tests so
    // both treat a base virtual reached only via an override — or an interface
    // member reached only via its implementation — as exercised by the test that
    // reaches the override. Only adds to the reachable set; body traversal of the
    // seeded declarations happens through the normal callee pass when they are
    // themselves invoked (e.g. via `base.X()`).
    private static void SeedRelatedDeclarations(IMethodSymbol method, HashSet<ISymbol> reachable)
    {
        for (var baseMethod = method.OverriddenMethod; baseMethod != null; baseMethod = baseMethod.OverriddenMethod)
        {
            reachable.Add(baseMethod.OriginalDefinition);
        }
        foreach (var impl in method.ExplicitInterfaceImplementations)
        {
            reachable.Add(impl.OriginalDefinition);
        }
        var containingType = method.ContainingType;
        if (containingType == null) return;
        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers())
            {
                if (ifaceMember is not IMethodSymbol and not IPropertySymbol) continue;
                if (SymbolEqualityComparer.Default.Equals(
                        containingType.FindImplementationForInterfaceMember(ifaceMember), method))
                {
                    reachable.Add(ifaceMember.OriginalDefinition);
                }
            }
        }
    }

    // Collects every method/property/field symbol the given method's body references.
    // Used for BFS reachability in untested-code detection.
    //
    // IMPORTANT: Must use the SAME compilation (GetProjectCompilationAsync, which runs
    // source generators) as the rest of the tool. document.GetSemanticModelAsync() returns
    // the workspace's default model, which on generator-using projects has DIFFERENT symbol
    // identity than our augmented compilation — SymbolEqualityComparer.Default returns false
    // across compilations, breaking the reachable-set membership check downstream.
    private async Task<IEnumerable<ISymbol>> CollectReferencedSymbolsAsync(
        IMethodSymbol method, CancellationToken cancellationToken = default)
    {
        var result = new List<ISymbol>();
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            var syntax = await declRef.GetSyntaxAsync(cancellationToken);
            var document = _solution!.GetDocument(declRef.SyntaxTree);
            if (document == null) continue;

            var compilation = await GetProjectCompilationAsync(document.Project, cancellationToken);
            if (compilation == null) continue;
            var semanticModel = compilation.GetSemanticModel(declRef.SyntaxTree);

            foreach (var inv in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var sym = semanticModel.GetSymbolInfo(inv, cancellationToken).Symbol;
                if (sym != null) result.Add(sym.OriginalDefinition);
            }
            // Every simple name that binds to a method, property, or field — this
            // subsumes member accesses (the `.Name` part) and additionally captures
            // method-group references (delegate passing) and bare unqualified
            // property/field reads, which carry no invocation or member-access node.
            foreach (var name in syntax.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (semanticModel.GetSymbolInfo(name, cancellationToken).Symbol is
                    (IMethodSymbol or IPropertySymbol or IFieldSymbol) and { } sym)
                {
                    result.Add(sym.OriginalDefinition);
                }
            }
            foreach (var oc in syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var sym = semanticModel.GetSymbolInfo(oc, cancellationToken).Symbol;
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
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();
        if (projectName != null && !_solution!.Projects.Any(
                p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)))
        {
            return ProjectNotFoundError(projectName);
        }
        var data = await FindGodObjectsDataAsync(projectName, minEfferentCoupling, minMembers, maxResults, cancellationToken);
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
        int maxResults,
        CancellationToken cancellationToken = default)
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
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            foreach (var type in GetAllNamedTypes(compilation))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;
                allTypes.Add(type);
            }
        }

        // Step 2: per type, compute the efferent (outbound) edge set in one pass;
        // afferent (inbound) by inversion.
        var efferent = await CollectTypeDependencyEdgesAsync(allTypes, includeMetadataTargets: false, cancellationToken);

        // Step 3: invert for afferent counts.
        var afferentCount = new Dictionary<INamedTypeSymbol, int>(SymbolEqualityComparer.Default);
        foreach (var (source, outboundSet) in efferent)
        {
            foreach (var target in outboundSet.Keys)
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

        // Descending by score, then by type name as a stable tie-break so the set kept
        // at the maxResults boundary is deterministic for equal-score types.
        allCandidates.Sort((a, b) =>
        {
            var byScore = b.score.CompareTo(a.score);
            return byScore != 0
                ? byScore
                : string.CompareOrdinal(a.type.ToDisplayString(), b.type.ToDisplayString());
        });
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

    // Shared per-type outbound-dependency collector: signature dependencies
    // (return/parameter/property/field/event types — no usable syntax location)
    // plus the precise body pass, which only counts types in semantically
    // meaningful positions — object creation, typeof, cast targets,
    // method-call receivers, and local-variable type declarations. A loose
    // pass (every IdentifierNameSyntax that resolves to a type) would inflate
    // counts with nameof() args, constraint type parameters, and merely
    // in-scope types. Each (source, target) edge keeps ONE representative
    // location: the first body usage seen, null for signature-only edges.
    // Consumers: find_god_objects (counts) and check_architecture (edges).
    internal async Task<Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, Location?>>>
        CollectTypeDependencyEdgesAsync(List<INamedTypeSymbol> allTypes, bool includeMetadataTargets,
            CancellationToken cancellationToken)
    {
        var edges = new Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, Location?>>(
            SymbolEqualityComparer.Default);
        foreach (var type in allTypes)
        {
            edges[(INamedTypeSymbol)type.OriginalDefinition] =
                new Dictionary<INamedTypeSymbol, Location?>(SymbolEqualityComparer.Default);
        }

        foreach (var type in allTypes)
        {
            var key = (INamedTypeSymbol)type.OriginalDefinition;
            var outbound = edges[key];

            // Inheritance is a type-to-type edge too. Gated to check_architecture so
            // find_god_objects' coupling count (signature + body usages only) is unchanged.
            // The implicit roots (object / ValueType / Enum / Delegate) are NOT edges:
            // every type inherits one, so recording them would make a rule like
            // {deny:'System*'} fire for every type. An EXPLICIT base/interface is a real edge.
            if (includeMetadataTargets)
            {
                var inheritanceLocation = type.Locations.FirstOrDefault(l => l.IsInSource);
                if (type.BaseType is { } baseType
                    && baseType.SpecialType is not (SpecialType.System_Object
                        or SpecialType.System_ValueType or SpecialType.System_Enum
                        or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate))
                    AddType(baseType, outbound, key, includeMetadataTargets, inheritanceLocation);
                foreach (var iface in type.Interfaces)
                    AddType(iface, outbound, key, includeMetadataTargets, inheritanceLocation);
            }

            foreach (var member in type.GetMembers())
            {
                CollectTypeDependencies(member, outbound, key, includeMetadataTargets);
            }

            foreach (var declRef in type.DeclaringSyntaxReferences)
            {
                var syntax = await declRef.GetSyntaxAsync(cancellationToken);
                var document = _solution!.GetDocument(declRef.SyntaxTree);
                if (document == null) continue;
                var compilation = await GetProjectCompilationAsync(document.Project, cancellationToken);
                if (compilation == null) continue;
                var semanticModel = compilation.GetSemanticModel(declRef.SyntaxTree);

                foreach (var node in syntax.DescendantNodes())
                {
                    switch (node)
                    {
                        case ObjectCreationExpressionSyntax oc:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(oc).Type, outbound, key, includeMetadataTargets, node.GetLocation());
                            break;
                        case ImplicitObjectCreationExpressionSyntax ioc:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(ioc).Type, outbound, key, includeMetadataTargets, node.GetLocation());
                            break;
                        case TypeOfExpressionSyntax t:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(t.Type).Type, outbound, key, includeMetadataTargets, node.GetLocation());
                            break;
                        case CastExpressionSyntax cast:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(cast.Type).Type, outbound, key, includeMetadataTargets, node.GetLocation());
                            break;
                        case VariableDeclarationSyntax vd when vd.Type is not null:
                            AddTypeFromSymbol(semanticModel.GetTypeInfo(vd.Type).Type, outbound, key, includeMetadataTargets, node.GetLocation());
                            break;
                        case InvocationExpressionSyntax inv:
                            // The coupling target is the RECEIVER's type — the type you
                            // actually depend on — not the (possibly base, or static
                            // extension-host) type that merely declares the method. For a
                            // genuinely static call the declaring ContainingType is right.
                            if (semanticModel.GetSymbolInfo(inv, cancellationToken).Symbol is IMethodSymbol calledMethod)
                            {
                                INamedTypeSymbol? edgeTarget = null;
                                if (inv.Expression is MemberAccessExpressionSyntax memberAccess
                                    && (!calledMethod.IsStatic || calledMethod.IsExtensionMethod || calledMethod.ReducedFrom != null))
                                {
                                    edgeTarget = semanticModel.GetTypeInfo(memberAccess.Expression, cancellationToken).Type as INamedTypeSymbol;
                                }
                                edgeTarget ??= calledMethod.ContainingType;
                                if (edgeTarget != null)
                                {
                                    AddTypeFromSymbol(edgeTarget, outbound, key, includeMetadataTargets, node.GetLocation());
                                }
                            }
                            break;
                    }
                }
            }
        }

        return edges;
    }

    private static void CollectTypeDependencies(
        ISymbol member, Dictionary<INamedTypeSymbol, Location?> outbound, INamedTypeSymbol owner,
        bool includeMetadataTargets)
    {
        switch (member)
        {
            case IMethodSymbol m:
                AddType(m.ReturnType, outbound, owner, includeMetadataTargets);
                foreach (var p in m.Parameters) AddType(p.Type, outbound, owner, includeMetadataTargets);
                break;
            case IPropertySymbol prop:
                AddType(prop.Type, outbound, owner, includeMetadataTargets);
                break;
            case IFieldSymbol field:
                AddType(field.Type, outbound, owner, includeMetadataTargets);
                break;
            case IEventSymbol evt:
                AddType(evt.Type, outbound, owner, includeMetadataTargets);
                break;
        }
    }

    // Records the edge; a later body usage upgrades a location-less
    // (signature-only) edge to one with a representative location. With
    // includeMetadataTargets, edges to referenced/metadata types are kept too (for
    // check_architecture's rules against package/BCL namespaces); otherwise only
    // in-solution source types count (find_god_objects' intra-codebase coupling).
    private static void AddType(
        ITypeSymbol type, Dictionary<INamedTypeSymbol, Location?> outbound, INamedTypeSymbol owner,
        bool includeMetadataTargets, Location? location = null)
    {
        if (type is INamedTypeSymbol nts)
        {
            if (includeMetadataTargets || IsSourceType(nts))
            {
                var def = (INamedTypeSymbol)nts.OriginalDefinition;
                if (!SymbolEqualityComparer.Default.Equals(def, owner)
                    && (!outbound.TryGetValue(def, out var existing) || (existing == null && location != null)))
                {
                    outbound[def] = location;
                }
            }
            // Generic type arguments are dependencies too (List<Foo> depends on Foo).
            // Gated to check_architecture so find_god_objects' coupling count is unchanged.
            if (includeMetadataTargets && nts.IsGenericType)
            {
                foreach (var arg in nts.TypeArguments)
                {
                    AddType(arg, outbound, owner, includeMetadataTargets, location);
                }
            }
        }
        else if (includeMetadataTargets && type is IArrayTypeSymbol arr)
        {
            AddType(arr.ElementType, outbound, owner, includeMetadataTargets, location);
        }
    }

    // Adds a TypeInfo-derived ITypeSymbol (which may be null when semantic resolution fails)
    // to the outbound set with the same filtering as AddType.
    private static void AddTypeFromSymbol(
        ITypeSymbol? type, Dictionary<INamedTypeSymbol, Location?> outbound, INamedTypeSymbol owner,
        bool includeMetadataTargets, Location? location)
    {
        if (type == null) return;
        AddType(type, outbound, owner, includeMetadataTargets, location);
    }

    private static bool IsSourceType(INamedTypeSymbol type) =>
        type.Locations.Any(l => l.IsInSource);

    // The canonical unknown-projectName error: a consistent INVALID_PARAMETER that
    // lists the valid project names, shared by every project-scoped tool.
    internal object ProjectNotFoundError(string projectName) => CreateErrorResponse(
        ErrorCodes.InvalidParameter,
        $"Project not found: {projectName}",
        hint: $"Available projects: {string.Join(", ", _solution!.Projects.Select(p => p.Name))}",
        context: new { projectName });

    // Composite per-project audit: aggregates diagnostics, unused-code, coupling, and
    // coverage signals via the typed *DataAsync helpers (no dynamic, no reflection).
    // Returns counts + top-N hotspots per dimension so the agent gets the lay of the land
    // in one round-trip.
    public async Task<object> GetProjectHealthAsync(
        string projectName,
        bool includeAnalyzers = true,
        int topN = 5,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var project = _solution!.Projects.FirstOrDefault(
            p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            return ProjectNotFoundError(projectName);
        }

        var diag = await GetDiagnosticsDataAsync(
            filePath: null,
            projectPath: project.FilePath,
            severity: null,
            includeHidden: false,
            runAnalyzers: includeAnalyzers,
            cancellationToken: cancellationToken);

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
            maxResults: topN,
            cancellationToken: cancellationToken);

        var unusedByKind = unused.Symbols
            .GroupBy(s => s.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => new { kind = g.Key, count = g.Count() })
            .ToList();

        var god = await FindGodObjectsDataAsync(
            projectName: project.Name,
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: topN,
            cancellationToken: cancellationToken);

        UntestedCodeData? untested = null;
        try
        {
            untested = await FindUntestedCodeDataAsync(
                projectName: project.Name,
                includeProperties: false,
                includeInternal: false,
                maxResults: topN,
                cancellationToken: cancellationToken);
        }
        catch (ArgumentException)
        {
            // Project has no test references resolvable — treat as zero coverage data
            // rather than failing the whole composite call.
        }
        var untestedCount = untested?.TotalCount ?? 0;
        var untestedHotspots = untested?.Symbols ?? new List<UncoveredSymbolEntry>();

        var asyncIssues = await FindAsyncIssuesDataAsync(project.Name, cancellationToken);
        var asyncByCategory = asyncIssues
            .GroupBy(i => i.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new { category = g.Key, count = g.Count() })
            .ToList();

        var summary = $"{diag.ErrorCount} errors, {diag.WarningCount} warnings, {god.TotalCount} god-object candidates, {untestedCount} uncovered public methods, {unused.TotalCount} unused symbols, {asyncIssues.Count} async issues";

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
                asyncHygiene = new
                {
                    count = asyncIssues.Count,
                    byCategory = asyncByCategory
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
