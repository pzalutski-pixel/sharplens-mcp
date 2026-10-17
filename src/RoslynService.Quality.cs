using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace SharpLensMcp;

// Quality/audit tools: untested-surface detection, god-object detection,
// and the get_project_health composite. These are intended for audit-time
// invocation, not per-edit agent loops — several do O(N) or O(N^2)
// solution-wide work.
public partial class RoslynService
{
    // Find public/internal surface that no test transitively reaches. Starts from every
    // runner-invoked entry point (test methods by each framework's discovery contract,
    // BDD step bindings and hooks, with the lifecycle code that runs around them), walks
    // the call graph, then diffs the production project's surface against the reached set.
    public async Task<object> FindUntestedCodeAsync(
        string? projectName = null,
        bool includeProperties = true,
        bool includeInternal = false,
        int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();
        if (ParameterValidation.Limit("maxResults", maxResults, MaxQualityResults) is { } invalid) return CreateErrorResponse(invalid);
        if (!string.IsNullOrWhiteSpace(projectName) && ResolveProjectScope(projectName).Error is { } scopeError)
        {
            return scopeError;
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
                entryPointCounts = new
                {
                    tests = data.EntryPointCounts.Tests,
                    stepBindings = data.EntryPointCounts.StepBindings,
                    hooks = data.EntryPointCounts.Hooks
                },
                reachableSymbolCount = data.ReachableSymbolCount,
                uncoveredSymbols = data.Symbols.Select(s => new
                {
                    fullName = s.FullName,
                    kind = s.Kind,
                    complexity = s.Complexity,
                    accessibility = s.Accessibility,
                    location = s.Location,
                    reason = s.Reason
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
        // Step 1: every entry point across the whole solution; a project is scanned
        // when it declares at least one.
        var entryPoints = await CollectTestEntryPointsAsync(cancellationToken);
        var testProjectsScanned = entryPoints.Select(e => e.ProjectName).Distinct(StringComparer.Ordinal).ToList();

        // Step 2: one walk from every entry point's roots at once. The reached set also
        // carries the base methods a reached override overrides and the interface
        // members a reached implementation implements (they are invoked through its
        // dispatch), so a base virtual reached only via an override, or an interface
        // member reached only via its implementation, does not read as untested.
        // find_tests walks the same graph, so the two tools agree on what a test covers.
        var graph = CreateCoverageGraph();
        var walk = await graph.WalkAsync(entryPoints.SelectMany(e => e.Roots), isTarget: null, cancellationToken);
        var reachable = walk.Reached;

        // Step 3: the target's surface diff. The target is a logical project (ResolveProjectScope):
        // a multi-targeted project's plain name covers every target framework, each with its
        // own #if branches, and a member declared under several frameworks is one declaration
        // (one ReachabilityKey), listed once. Without a projectName, the target is the logical
        // project of the solution's first project.
        var (targetProjects, productionProject) = UntestedCodeTarget(projectName);

        var uncovered = new List<(ISymbol symbol, int complexity, string reason)>();
        var listed = new HashSet<ReachabilityKey>();
        foreach (var targetProject in targetProjects)
        {
            var targetCompilation = await GetProjectCompilationAsync(targetProject, cancellationToken)
                ?? throw new ArgumentException($"Could not get compilation for project '{targetProject.Name}'");
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
                    var key = ReachabilityKey.For(member);
                    // Entry points are walk roots, so they are always in the reached set.
                    if (reachable.Contains(key) || !listed.Add(key)) continue;

                    // Unreached, but a test dispatches through a member it implements or
                    // overrides: the call may run another implementation (or a mock), so the
                    // member stays untested and is labelled rather than counted as covered.
                    var dispatchOnly = graph.RelatedDeclarations(graph.NodeFor(member))
                        .Any(declaration => walk.Dispatched.Contains(declaration.Key));
                    var complexity = member is IMethodSymbol ms ? await EstimateCyclomaticAsync(ms, cancellationToken) : 1;
                    uncovered.Add((member, complexity, dispatchOnly
                        ? UncoveredSymbolEntry.DispatchOnlyReason
                        : UncoveredSymbolEntry.UnreachableReason));
                }
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
            Location: GetSymbolLocation(u.symbol),
            Reason: u.reason)).ToList();

        return new UntestedCodeData(
            ProductionProject: productionProject,
            TestProjectsScanned: testProjectsScanned,
            TestMethodCount: entryPoints.Count(e => e.Kind == TestEntryPointKind.Test),
            EntryPointCounts: CountEntryPoints(entryPoints),
            ReachableSymbolCount: reachable.Count,
            TotalCount: totalCount,
            Symbols: capped);
    }

    // The projects find_untested_code diffs and the name it reports for them: the logical
    // project a projectName resolves to (every framework for a plain name, one for a
    // framework-qualified name), or without one the logical project of the solution's
    // first project.
    private (IReadOnlyList<Project> Projects, string Name) UntestedCodeTarget(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            var first = _solution!.Projects.FirstOrDefault()
                ?? throw new ArgumentException("The solution has no projects");
            var logical = GetLogicalProjects().Of(first);
            return (logical.Projects, logical.Name);
        }

        var (scope, error) = ResolveProjectScope(projectName);
        if (error != null || scope == null || scope.Count == 0)
        {
            throw new ArgumentException($"Project not found: {projectName}");
        }
        var name = scope.Count == 1 ? scope[0].Name : GetLogicalProjects().Of(scope[0]).Name;
        return (scope, name);
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

    private const int MaxQualityResults = 1000;
    private const int MaxGodObjectThreshold = 100000;

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
        if (ParameterValidation.Limit("maxResults", maxResults, MaxQualityResults) is { } invalidResults) return CreateErrorResponse(invalidResults);
        if (ParameterValidation.Limit("minEfferentCoupling", minEfferentCoupling, MaxGodObjectThreshold, minimum: 0) is { } invalidCoupling)
        {
            return CreateErrorResponse(invalidCoupling);
        }
        if (ParameterValidation.Limit("minMembers", minMembers, MaxGodObjectThreshold, minimum: 0) is { } invalidMembers)
        {
            return CreateErrorResponse(invalidMembers);
        }
        var (_, projectError) = ResolveProjectScope(projectName);
        if (projectError != null) return projectError;
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
            ResultCounts.Capped(data.TotalCount, data.Candidates.Count),
            suggestedNextTools: new[]
            {
                "get_type_overview on a candidate to see its member breakdown",
                "find_references on a candidate to see who depends on it"
            });
    }

    // Typed-data variant — used by GetProjectHealthAsync to compose without dynamic.
    // projectName scopes the reported types (ResolveProjectScope: a logical project's
    // every target framework, or one framework-qualified member); coupling is always
    // computed over the whole solution, so a scoped type's afferent coupling counts
    // dependents in every project.
    internal async Task<GodObjectsData> FindGodObjectsDataAsync(
        string? projectName,
        int minEfferentCoupling,
        int minMembers,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var (scopeProjects, scopeError) = ResolveProjectScope(projectName);
        if (scopeError != null) throw new ArgumentException($"Project not found: {projectName}", nameof(projectName));
        var scope = scopeProjects!.Select(p => p.Id).ToHashSet();

        // Step 1: every source type of the solution, each project's own types only (a
        // compilation's GlobalNamespace merges in every referenced assembly's types). A
        // multi-targeted project contributes one instance per target framework; they are
        // one type (cross-compilation identity), reported once, its edges and members the
        // union over the frameworks.
        var allTypes = new List<INamedTypeSymbol>();
        var instancesByType = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(CrossCompilationSymbolComparer.Instance);
        var scopedTypes = new List<INamedTypeSymbol>();
        var scopedSet = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        foreach (var project in GetLogicalProjects().Projects.SelectMany(p => p.Projects))
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            foreach (var type in GetAllNamedTypes(compilation))
            {
                if (!type.Locations.Any(l => l.IsInSource)) continue;
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;
                allTypes.Add(type);
                var key = (INamedTypeSymbol)type.OriginalDefinition;
                if (!instancesByType.TryGetValue(key, out var instances))
                {
                    instancesByType[key] = instances = new List<INamedTypeSymbol>();
                }
                instances.Add(type);
                if (scope.Contains(project.Id) && scopedSet.Add(key)) scopedTypes.Add(key);
            }
        }

        // Step 2: per type, the efferent (outbound) edge set in one pass; afferent
        // (inbound) by inversion.
        var efferent = await CollectTypeDependencyEdgesAsync(allTypes, includeMetadataTargets: false, cancellationToken);

        // Step 3: invert for afferent counts. An edge target from another project is the
        // type as the SOURCE's compilation binds it (a retargeted instance when the two
        // projects' references differ), so targets and the per-type lookup below meet
        // by cross-compilation identity (CrossCompilationSymbol).
        var afferentCount = new Dictionary<INamedTypeSymbol, int>(CrossCompilationSymbolComparer.Instance);
        foreach (var (_, outboundSet) in efferent)
        {
            foreach (var target in outboundSet.Keys)
            {
                afferentCount[target] = afferentCount.GetValueOrDefault(target) + 1;
            }
        }

        // Step 4: candidates + scoring. Members are those a reader sees on the type
        // (UserVisibleMembers: no accessors, no compiler-generated members), nested types
        // excluded — they are types of their own.
        var allCandidates = new List<(INamedTypeSymbol type, int efferent, int afferent, int members, double score)>();
        foreach (var key in scopedTypes)
        {
            var eff = efferent[key].Count;
            var aff = afferentCount.GetValueOrDefault(key);
            var memberCount = instancesByType[key]
                .SelectMany(instance => instance.GetMembers())
                .Where(m => UserVisibleMembers.IsListed(m) && MemberClassifier.Classify(m) != MemberKind.NestedType)
                .Distinct(CrossCompilationSymbolComparer.Instance)
                .Count();

            if (eff < minEfferentCoupling || memberCount < minMembers) continue;

            var score = eff * 0.4 + memberCount * 0.4 + aff * 0.2;
            allCandidates.Add((instancesByType[key][0], eff, aff, memberCount, score));
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

    // Shared per-type outbound-dependency collector: every type-to-type edge a type's own
    // declaration makes. Consumers: find_god_objects (counts) and check_architecture
    // (edges with locations).
    //   - Inheritance: the base type and interfaces its declarations name (never an
    //     implicit root such as object, nor an interface the compiler adds, such as a
    //     record's IEquatable<T>).
    //   - Attributes applied to the type, its members, accessors, parameters and return
    //     values, with the types their typeof() arguments name.
    //   - Signatures of the members it declares: return, parameter, property, field and
    //     event types and type-parameter constraints (compiler-generated members and
    //     void excluded).
    //   - Bodies, from the bound operation tree of every member body and initializer it
    //     declares: created types, the receiver type of an instance call or member access
    //     (the type actually depended on, not a base that declares the member), the
    //     containing type of a static member (enum values included) and an extension
    //     method's host, generic method type arguments, typeof/sizeof/default/array
    //     operands, casts and `as`, type tests and patterns, catch types, local variable
    //     types (foreach, using, out var and pattern declarations included) and lambda and
    //     local-function parameter types. nameof(...) only names a symbol and is skipped.
    // A nested type is a type of its own: its declaration is never walked as part of the
    // containing type's. Generic type arguments and array elements are edges too
    // (List<Foo> depends on Foo). With includeMetadataTargets, edges to referenced/metadata
    // types are kept (for check_architecture's rules against package/BCL namespaces);
    // otherwise only in-solution source types count (find_god_objects' intra-codebase
    // coupling). Each (source, target) edge keeps ONE representative location: its earliest
    // body usage, attribute or base-list entry in source order, null for a signature-only
    // edge.
    // Every dictionary here is keyed by cross-compilation identity: allTypes pools several
    // projects' compilations (a multi-targeted project's types once per target framework),
    // and an edge target is bound by its source's compilation, so the same type arrives as
    // distinct instances (CrossCompilationSymbol).
    internal async Task<Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, Location?>>>
        CollectTypeDependencyEdgesAsync(List<INamedTypeSymbol> allTypes, bool includeMetadataTargets,
            CancellationToken cancellationToken)
    {
        var edges = new Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, Location?>>(
            CrossCompilationSymbolComparer.Instance);
        foreach (var type in allTypes)
        {
            edges.TryAdd((INamedTypeSymbol)type.OriginalDefinition,
                new Dictionary<INamedTypeSymbol, Location?>(CrossCompilationSymbolComparer.Instance));
        }

        foreach (var type in allTypes)
        {
            var key = (INamedTypeSymbol)type.OriginalDefinition;
            var outbound = edges[key];
            void Add(ITypeSymbol? target, Location? location) =>
                AddType(target, outbound, key, includeMetadataTargets, location);

            foreach (var attribute in AttributesDeclaredBy(type))
            {
                var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation();
                if (location == null) continue; // compiler-synthesized
                Add(attribute.AttributeClass, location);
                foreach (var argument in attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(n => n.Value)))
                {
                    // A null array argument has no Values.
                    var values = argument.Kind != TypedConstantKind.Array ? new[] { argument }
                        : argument.IsNull ? Array.Empty<TypedConstant>()
                        : argument.Values.AsEnumerable();
                    foreach (var value in values)
                    {
                        if (value.Kind == TypedConstantKind.Type) Add(value.Value as ITypeSymbol, location);
                    }
                }
            }

            foreach (var typeParameter in type.TypeParameters)
            {
                foreach (var constraint in typeParameter.ConstraintTypes) Add(constraint, null);
            }
            foreach (var member in type.GetMembers())
            {
                CollectTypeDependencies(member, Add);
            }

            foreach (var declRef in type.DeclaringSyntaxReferences)
            {
                if (await declRef.GetSyntaxAsync(cancellationToken) is not BaseTypeDeclarationSyntax declaration) continue;
                var compilation = _solution!.GetDocument(declRef.SyntaxTree)?.Project is { } project
                    ? await GetProjectCompilationAsync(project, cancellationToken)
                    : null;
                if (compilation == null || !compilation.ContainsSyntaxTree(declRef.SyntaxTree)) continue;
                var semanticModel = compilation.GetSemanticModel(declRef.SyntaxTree);

                foreach (var baseType in declaration.BaseList?.Types ?? default)
                {
                    Add(semanticModel.GetTypeInfo(baseType.Type, cancellationToken).Type, baseType.GetLocation());
                }
                foreach (var root in BodyOperations(declaration, semanticModel, cancellationToken))
                {
                    CollectBodyDependencies(root, Add);
                }
            }
        }

        return edges;
    }

    // The attributes the type's own declaration applies: to the type, to the members it
    // declares (accessors included), to their parameters and to their return values. A
    // nested type's attributes are its own.
    private static IEnumerable<AttributeData> AttributesDeclaredBy(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes()) yield return attribute;
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member is INamedTypeSymbol) continue;
            foreach (var attribute in member.GetAttributes()) yield return attribute;
            var parameters = member switch
            {
                IMethodSymbol method => method.Parameters,
                IPropertySymbol property => property.Parameters,
                _ => ImmutableArray<IParameterSymbol>.Empty
            };
            foreach (var parameter in parameters)
            {
                foreach (var attribute in parameter.GetAttributes()) yield return attribute;
            }
            if (member is IMethodSymbol withReturn)
            {
                foreach (var attribute in withReturn.GetReturnTypeAttributes()) yield return attribute;
            }
        }
    }

    // The bound operation roots of a type declaration's own members: method, constructor,
    // operator and accessor bodies, expression-bodied properties and indexers, field,
    // property and enum-member initializers, and a primary constructor's base-type
    // arguments. Nested type declarations are skipped.
    private static IEnumerable<IOperation> BodyOperations(
        BaseTypeDeclarationSyntax declaration, SemanticModel model, CancellationToken cancellationToken)
    {
        IEnumerable<SyntaxNode?> Roots(MemberDeclarationSyntax member) => member switch
        {
            BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => Array.Empty<SyntaxNode?>(),
            BaseMethodDeclarationSyntax method => new SyntaxNode?[] { method },
            PropertyDeclarationSyntax property => (property.AccessorList?.Accessors.Cast<SyntaxNode?>() ?? Array.Empty<SyntaxNode?>())
                .Append(property.ExpressionBody).Append(property.Initializer),
            IndexerDeclarationSyntax indexer => (indexer.AccessorList?.Accessors.Cast<SyntaxNode?>() ?? Array.Empty<SyntaxNode?>())
                .Append(indexer.ExpressionBody),
            EventDeclarationSyntax eventDeclaration => eventDeclaration.AccessorList?.Accessors.Cast<SyntaxNode?>() ?? Array.Empty<SyntaxNode?>(),
            BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(v => (SyntaxNode?)v.Initializer),
            EnumMemberDeclarationSyntax enumMember => new SyntaxNode?[] { enumMember.EqualsValue },
            _ => Array.Empty<SyntaxNode?>()
        };

        var members = declaration switch
        {
            TypeDeclarationSyntax typeDeclaration => typeDeclaration.Members,
            EnumDeclarationSyntax enumDeclaration => enumDeclaration.Members.Cast<MemberDeclarationSyntax>(),
            _ => Enumerable.Empty<MemberDeclarationSyntax>()
        };
        var roots = members.SelectMany(Roots)
            .Concat(declaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>() ?? Enumerable.Empty<SyntaxNode>());
        foreach (var root in roots)
        {
            if (root != null && model.GetOperation(root, cancellationToken) is { } operation) yield return operation;
        }
    }

    // The types one bound body depends on (see CollectTypeDependencyEdgesAsync).
    private static void CollectBodyDependencies(IOperation root, Action<ITypeSymbol?, Location?> add)
    {
        static ITypeSymbol? ReceiverType(IOperation? instance) => instance switch
        {
            null or IInstanceReferenceOperation => null,
            IConversionOperation { IsImplicit: true } conversion => conversion.Operand.Type,
            _ => instance.Type
        };

        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var operation = stack.Pop();
            if (operation is INameOfOperation) continue;
            var location = operation.Syntax.GetLocation();
            switch (operation)
            {
                case IObjectCreationOperation creation:
                    add(creation.Type, location);
                    break;
                case IInvocationOperation invocation:
                    var method = invocation.TargetMethod;
                    if (invocation.Instance != null)
                    {
                        add(ReceiverType(invocation.Instance), location);
                    }
                    else if (method.IsExtensionMethod && invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0) is { } extended)
                    {
                        add(ReceiverType(extended.Value), location);
                        add(method.ContainingType, location);
                    }
                    else
                    {
                        add(method.ContainingType, location);
                    }
                    foreach (var typeArgument in method.TypeArguments) add(typeArgument, location);
                    break;
                case IMemberReferenceOperation memberReference:
                    add(memberReference.Member.IsStatic ? memberReference.Member.ContainingType : ReceiverType(memberReference.Instance), location);
                    if (memberReference is IMethodReferenceOperation methodReference)
                    {
                        foreach (var typeArgument in methodReference.Method.TypeArguments) add(typeArgument, location);
                    }
                    break;
                case ITypeOfOperation typeOf:
                    add(typeOf.TypeOperand, location);
                    break;
                case ISizeOfOperation sizeOf:
                    add(sizeOf.TypeOperand, location);
                    break;
                case IDefaultValueOperation { Syntax: DefaultExpressionSyntax } defaultValue:
                    add(defaultValue.Type, location);
                    break;
                case IArrayCreationOperation arrayCreation:
                    add(arrayCreation.Type, location);
                    break;
                case IConversionOperation { IsImplicit: false } conversion:
                    add(conversion.Type, location);
                    break;
                case IIsTypeOperation isType:
                    add(isType.TypeOperand, location);
                    break;
                case IDeclarationPatternOperation declarationPattern:
                    add(declarationPattern.MatchedType, location);
                    break;
                case ITypePatternOperation typePattern:
                    add(typePattern.MatchedType, location);
                    break;
                case IRecursivePatternOperation recursivePattern:
                    add(recursivePattern.MatchedType, location);
                    break;
                // catch (T) and catch (T e); a bare catch names no type.
                case ICatchClauseOperation { Syntax: CatchClauseSyntax { Declaration: not null } } catchClause:
                    add(catchClause.ExceptionType, location);
                    break;
                case IVariableDeclaratorOperation declarator:
                    add(declarator.Symbol.Type, location);
                    break;
                case ILocalReferenceOperation { IsDeclaration: true } localDeclaration:
                    add(localDeclaration.Local.Type, location);
                    break;
                case IAnonymousFunctionOperation lambda:
                    foreach (var parameter in lambda.Symbol.Parameters) add(parameter.Type, location);
                    break;
                case ILocalFunctionOperation localFunction:
                    add(localFunction.Symbol.ReturnType, location);
                    foreach (var parameter in localFunction.Symbol.Parameters) add(parameter.Type, location);
                    break;
            }
            // Children pushed last-first, so the walk visits the body in source order.
            foreach (var child in operation.ChildOperations.Reverse()) stack.Push(child);
        }
    }

    // The signature types of a member the type declares: a method's return and parameter
    // types and its type-parameter constraints, a property's or indexer's type and
    // parameters, a field's or event's type. A compiler-generated member (an implicit
    // constructor, a record's synthesized members, a backing field), an accessor (its
    // property carries the type) and a nested type contribute nothing.
    private static void CollectTypeDependencies(ISymbol member, Action<ITypeSymbol?, Location?> add)
    {
        if (member.IsImplicitlyDeclared) return;
        switch (member)
        {
            case IMethodSymbol { AssociatedSymbol: not null }:
                break;
            case IMethodSymbol method:
                add(method.ReturnType, null);
                foreach (var parameter in method.Parameters) add(parameter.Type, null);
                foreach (var typeParameter in method.TypeParameters)
                {
                    foreach (var constraint in typeParameter.ConstraintTypes) add(constraint, null);
                }
                break;
            case IPropertySymbol property:
                add(property.Type, null);
                foreach (var parameter in property.Parameters) add(parameter.Type, null);
                break;
            case IFieldSymbol field:
                add(field.Type, null);
                break;
            case IEventSymbol @event:
                add(@event.Type, null);
                break;
        }
    }

    // Records the edge with its representative location: the earliest usage in source
    // order (path, line, column), null while the edge is signature-only. void, type
    // parameters and error types are not edges; generic type arguments and array elements
    // are. With includeMetadataTargets, edges to referenced/metadata types are kept too;
    // otherwise only in-solution source types count.
    private static void AddType(
        ITypeSymbol? type, Dictionary<INamedTypeSymbol, Location?> outbound, INamedTypeSymbol owner,
        bool includeMetadataTargets, Location? location)
    {
        switch (type)
        {
            case IErrorTypeSymbol:
            case { SpecialType: SpecialType.System_Void }:
                return;
            case INamedTypeSymbol named:
                if (includeMetadataTargets || IsSourceType(named))
                {
                    var def = (INamedTypeSymbol)named.OriginalDefinition;
                    if (!CrossCompilationSymbolComparer.Instance.Equals(def, owner)
                        && (!outbound.TryGetValue(def, out var existing) || IsEarlier(location, existing)))
                    {
                        outbound[def] = location;
                    }
                }
                foreach (var argument in named.TypeArguments)
                {
                    if (argument is not ITypeParameterSymbol) AddType(argument, outbound, owner, includeMetadataTargets, location);
                }
                break;
            case IArrayTypeSymbol array:
                AddType(array.ElementType, outbound, owner, includeMetadataTargets, location);
                break;
            case IPointerTypeSymbol pointer:
                AddType(pointer.PointedAtType, outbound, owner, includeMetadataTargets, location);
                break;
        }
    }

    // Whether a usage location comes before the edge's current representative one (a
    // location-less, signature-only edge has none).
    private static bool IsEarlier(Location? candidate, Location? current) =>
        ResultLocation.From(candidate ?? Location.None) is { } candidateLocation
        && (ResultLocation.From(current ?? Location.None) is not { } currentLocation
            || ResultLocationComparer.Instance.Compare(candidateLocation, currentLocation) < 0);

    private static bool IsSourceType(INamedTypeSymbol type) =>
        type.Locations.Any(l => l.IsInSource);

    // The canonical unknown-projectName error: a consistent INVALID_PARAMETER that
    // lists the valid project names, shared by every project-scoped tool.
    internal object ProjectNotFoundError(string projectName) => CreateErrorResponse(
        ErrorCodes.InvalidParameter,
        $"Project not found: {projectName}",
        hint: $"Available projects: {string.Join(", ", _solution!.Projects.Select(p => p.Name))}",
        context: new { projectName });

    // The largest topN get_project_health accepts.
    private const int MaxHealthTopN = 100;

    // Composite per-project audit: aggregates diagnostics, unused-code, coupling, and
    // coverage signals via the typed *DataAsync helpers (no dynamic, no reflection).
    // Returns counts + top-N hotspots per dimension so the agent gets the lay of the land
    // in one round-trip. The project name resolves through the logical project model, and
    // every section scans the same scope: all target frameworks for a plain name, one for a
    // framework-qualified name. Breakdowns (diagnostics by id, unused symbols by kind) are
    // computed from the full data, never from a capped page; topN caps only the hotspot and
    // top-id lists.
    public async Task<object> GetProjectHealthAsync(
        string projectName,
        bool includeAnalyzers = true,
        int topN = 5,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("topN", topN, MaxHealthTopN) is { } invalid) return CreateErrorResponse(invalid);

        var (scope, scopeError) = ResolveProjectScope(projectName);
        if (scope == null) return scopeError!;
        var logical = GetLogicalProjects().Of(scope[0]);
        // The name every section resolves to the same scope: the plain name for the whole
        // logical project, the framework-qualified one for a single framework.
        var scopeName = scope.Count == logical.Projects.Count ? logical.Name : scope[0].Name;

        var diag = await ComputeDiagnosticsDataAsync(
            scope.Select(p => (p, (Document?)null)).ToList(),
            severity: null,
            includeHidden: false,
            runAnalyzers: includeAnalyzers,
            cancellationToken);

        var topDiagsByCount = diag.Diagnostics
            .GroupBy(d => d.Id)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(topN)
            .Select(g => new { id = g.Key, count = g.Count() })
            .ToList();

        var unused = await FindUnusedCodeDataAsync(
            projectName: scopeName,
            includePrivate: false,
            includeInternal: false,
            symbolKindFilter: null,
            maxResults: int.MaxValue,
            cancellationToken: cancellationToken);

        var unusedByKind = unused.Symbols
            .GroupBy(s => s.Kind)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { kind = g.Key, count = g.Count() })
            .ToList();

        var god = await FindGodObjectsDataAsync(
            projectName: scopeName,
            minEfferentCoupling: 20,
            minMembers: 20,
            maxResults: topN,
            cancellationToken: cancellationToken);

        UntestedCodeData? untested = null;
        try
        {
            untested = await FindUntestedCodeDataAsync(
                projectName: scopeName,
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

        var asyncIssues = await FindAsyncIssuesDataAsync(scope, cancellationToken);
        var asyncByCategory = asyncIssues
            .GroupBy(i => i.Item.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new { category = g.Key, count = g.Count() })
            .ToList();

        var summary = $"{diag.ErrorCount} errors, {diag.WarningCount} warnings, {god.TotalCount} god-object candidates, {untestedCount} uncovered public methods, {unused.TotalCount} unused symbols, {asyncIssues.Count} async issues";

        return CreateSuccessResponse(
            data: new
            {
                projectName = logical.Name,
                targetFrameworks = logical.IsMultiTargeted
                    ? scope.Select(LogicalProject.TargetFrameworkOf).OfType<string>().ToList()
                    : null,
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
