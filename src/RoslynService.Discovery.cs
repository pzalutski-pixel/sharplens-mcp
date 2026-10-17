using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Xml.Linq;

namespace SharpLensMcp;

public partial class RoslynService
{
    // Every application of the attribute in the scoped projects — the same walk as
    // get_attributes (FindAttributeApplicationsAsync), so both tools cover the same targets
    // — one entry per application, in the order the attributes are written.
    public async Task<object> FindAttributeUsagesAsync(
        string attributeName, string? projectName = null, int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);
        var (projects, scopeError) = ResolveProjectScope(projectName);
        if (projects == null) return scopeError!;

        var applications = await FindAttributeApplicationsAsync(projects, includeTree: null, attributeName, cancellationToken);
        var results = applications.Take(maxResults).Select(m =>
        {
            var application = m.Item;
            var attr = application.Attribute;
            return WithTargetFrameworks(new Dictionary<string, object?>
            {
                ["symbolName"] = AttributeOwnerDisplayName(application.Owner),
                ["symbolKind"] = AttributeOwnerKind(application.Owner),
                ["target"] = application.Target,
                ["attributeName"] = attr.AttributeClass?.Name,
                ["arguments"] = attr.ConstructorArguments.Select(a => a.ToCSharpString()).ToList(),
                ["namedArguments"] = attr.NamedArguments.ToDictionary(a => a.Key, a => a.Value.ToCSharpString()),
                ["location"] = GetSymbolLocation(application.Owner),
                ["attributeLocation"] = FormatLocation(application.AttributeLocation)
            }, m.TargetFrameworks);
        }).ToList();

        return CreateSuccessResponse(
            data: new { attributeFilter = attributeName, usages = results },
            ResultCounts.Capped(applications.Count, results.Count),
            suggestedNextTools: new[] { "get_type_overview", "find_references" }
        );
    }

    // The name find_attribute_usages reports an owner by: a type minimally qualified, a
    // member as Type.Member, a parameter or type parameter under its declaring member, the
    // assembly or module by its name.
    private static string AttributeOwnerDisplayName(ISymbol owner) => owner switch
    {
        INamedTypeSymbol type => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        IParameterSymbol or ITypeParameterSymbol => $"{AttributeOwnerDisplayName(owner.ContainingSymbol)}.{owner.Name}",
        { ContainingType: { } containingType } => $"{containingType.Name}.{owner.Name}",
        _ => owner.Name
    };

    // The dependency-injection registrations (DiRegistrationReader) in every source tree of
    // the scoped projects, generator output included.
    public async Task<object> GetDiRegistrationsAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (projects, scopeError) = ResolveProjectScope(projectName);
        if (projects == null) return scopeError!;

        var found = new List<(DiRegistration Registration, int Index, string? TargetFramework)>();
        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await syntaxTree.GetRootAsync(cancellationToken);
                SemanticModel? semanticModel = null;
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    semanticModel ??= compilation.GetSemanticModel(syntaxTree);
                    var index = 0;
                    foreach (var registration in DiRegistrationReader.Read(invocation, semanticModel, cancellationToken))
                    {
                        found.Add((registration, index++, targetFramework));
                    }
                }
            }
        }

        // One registration per call (and per descriptor of a call that adds several), each
        // once however many target frameworks compile it, in the canonical location order.
        var merged = CanonicalOrder.ByLocation(
            TargetFrameworkDeduplication.By(
                found,
                f => $"{SourceSpanKey.From(f.Registration.Location)}|{f.Index}",
                StringComparer.Ordinal,
                f => f.TargetFramework),
            m => ReportedLocation(m.Item.Registration.Location));
        var registrations = merged.Select(m => WithTargetFrameworks(new Dictionary<string, object?>
        {
            ["lifetime"] = m.Item.Registration.Lifetime,
            ["serviceType"] = m.Item.Registration.ServiceType,
            ["implementationType"] = m.Item.Registration.ImplementationType,
            ["method"] = m.Item.Registration.Method,
            ["location"] = FormatLocation(m.Item.Registration.Location)
        }, m.TargetFrameworks)).ToList();

        return CreateSuccessResponse(
            data: new { registrations },
            ResultCounts.Complete(registrations.Count),
            suggestedNextTools: new[] { "find_references", "get_type_overview" }
        );
    }

    // The reflection calls (IsReflectionCall) in the documents of the scoped projects.
    public async Task<object> FindReflectionUsageAsync(
        string? projectName = null, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);
        var (projects, scopeError) = ResolveProjectScope(projectName);
        if (projects == null) return scopeError!;

        var hits = new List<(IMethodSymbol Method, InvocationExpressionSyntax Invocation, string? TargetFramework)>();
        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (syntaxTree == null) continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol method
                        && IsReflectionCall(method))
                    {
                        hits.Add((method, invocation, targetFramework));
                    }
                }
            }
        }

        // Each call once however many target frameworks compile it. High-signal
        // (string-driven) reflection is ordered ahead of the common Object.GetType()
        // runtime-type query so the latter never crowds the meaningful hits out of a capped
        // page; each group is in the canonical location order.
        var ordered = CanonicalOrder.ByLocation(
                TargetFrameworkDeduplication.ByLocation(hits, h => SourceSpanKey.From(h.Invocation.GetLocation())!.Value, h => h.TargetFramework),
                m => ReportedLocation(m.Item.Invocation.GetLocation()))
            .OrderBy(m => IsObjectGetType(m.Item.Method) ? 1 : 0)
            .ToList();
        var usages = ordered.Take(maxResults).Select(m => WithTargetFrameworks(new Dictionary<string, object?>
        {
            ["reflectionApi"] = $"{m.Item.Method.ContainingType?.Name}.{m.Item.Method.Name}",
            ["context"] = TruncateForSnippet(m.Item.Invocation.ToString(), 200, "..."),
            ["location"] = FormatLocation(m.Item.Invocation.GetLocation())
        }, m.TargetFrameworks)).ToList();

        return CreateSuccessResponse(
            data: new { usages },
            ResultCounts.Capped(ordered.Count, usages.Count),
            suggestedNextTools: new[] { "find_references", "get_symbol_info" }
        );
    }

    // A call that looks up or invokes code by reflection, decided by the API itself rather
    // than a list of names: anything in System.Reflection (Assembly.GetTypes,
    // MethodInfo.Invoke, PropertyInfo.GetValue, ...); a System.Type member that returns
    // reflection metadata — a MemberInfo (Type, MethodInfo, ConstructorInfo, EventInfo, ...)
    // or an array of them — or invokes a member (InvokeMember); Activator and
    // AppDomain.CreateInstance*; Delegate.DynamicInvoke; and the instance Object.GetType()
    // runtime-type query, the highest-volume and lowest-signal of these, reported as
    // "Object.GetType" so callers can filter it out. A System.Type query that only answers
    // a question (IsAssignableFrom, IsSubclassOf) is not reflection.
    private static bool IsReflectionCall(IMethodSymbol method)
    {
        var type = method.ContainingType;
        if (type == null) return false;
        if (type.ContainingNamespace?.ToDisplayString() is { } ns && (ns == "System.Reflection" || ns.StartsWith("System.Reflection.", StringComparison.Ordinal)))
            return true;
        if (TestFrameworkContracts.Is(type, "System", "Type"))
            return method.Name == "InvokeMember" || ReturnsReflectionMetadata(method.ReturnType);
        if (TestFrameworkContracts.Is(type, "System", "Activator")) return true;
        if (TestFrameworkContracts.Is(type, "System", "AppDomain")) return method.Name.StartsWith("CreateInstance", StringComparison.Ordinal);
        if (TestFrameworkContracts.Is(type, "System", "Delegate")) return method.Name == "DynamicInvoke";
        return IsObjectGetType(method);
    }

    private static bool ReturnsReflectionMetadata(ITypeSymbol returnType)
    {
        var element = returnType is IArrayTypeSymbol array ? array.ElementType : returnType;
        return TestFrameworkContracts.DerivesFrom(element, "System.Reflection", "MemberInfo");
    }

    private static bool IsObjectGetType(IMethodSymbol method) =>
        method.Name == "GetType" && method.ContainingType is { } type && TestFrameworkContracts.Is(type, "System", "Object");

    // The cycles of the project graph (BuildProjectGraph) or the namespace graph
    // (BuildNamespaceGraphAsync): every elementary cycle (up to the cap, cyclesTruncated
    // when more exist) and every strongly connected component, with the graph itself as
    // nodes and edges — one shape for both levels.
    public async Task<object> FindCircularDependenciesAsync(
        string? level = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Choice("level", level, new[] { "project", "namespace" }, out var canonicalLevel) is { } invalidLevel)
            return CreateErrorResponse(invalidLevel);
        var analysisLevel = canonicalLevel ?? "project";

        var graph = analysisLevel == "project"
            ? BuildProjectGraph()
            : await BuildNamespaceGraphAsync(cancellationToken);
        var cycles = DetectCycles(graph.ToAdjacency());

        var data = new Dictionary<string, object?>
        {
            ["level"] = analysisLevel,
            ["hasCycles"] = cycles.Cycles.Count > 0,
            ["cycles"] = cycles.Cycles,
            ["cyclesTruncated"] = cycles.Truncated,
            ["stronglyConnectedComponents"] = cycles.StronglyConnectedComponents,
            ["nodes"] = FormatGraphNodes(graph),
            ["edges"] = FormatGraphEdges(graph)
        };
        if (analysisLevel == "namespace") data["namespaceCount"] = graph.Nodes.Count;

        return CreateSuccessResponse(
            data,
            CycleCounts(cycles),
            suggestedNextTools: new[] { "dependency_graph", "get_project_structure" }
        );
    }

    // The package references of each project file in scope, as MSBuild evaluates them
    // (CollectEffectivePackageReferences: imports, Conditions, $(Property) versions,
    // Update=, Central Package Management). One entry per project file: a multi-targeted
    // project is evaluated for each of its target frameworks in scope and reported once,
    // identical references merged, and its entry and each package carry the frameworks they
    // apply to (targetFrameworks, present only on a multi-targeted project). Packages are in
    // evaluation order; a project file that cannot be evaluated reports why in parseError.
    public Task<object> GetNuGetDependenciesAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (projects, scopeError) = ResolveProjectScope(projectName);
        if (projects == null) return Task.FromResult(scopeError!);

        var model = GetLogicalProjects();
        var projectDependencies = new List<object>();
        using var evaluator = new MSBuildProjectEvaluator();
        foreach (var members in projects.GroupBy(model.Of))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logical = members.Key;
            var targetFrameworks = members.Select(LogicalProject.TargetFrameworkOf).ToList();

            var packages = new List<(EvaluatedPackageReference Reference, List<string> TargetFrameworks)>();
            var parseErrors = new List<string>();
            if (logical.FilePath != null && File.Exists(logical.FilePath))
            {
                foreach (var targetFramework in targetFrameworks)
                {
                    foreach (var reference in CollectEffectivePackageReferences(logical.FilePath, parseErrors, targetFramework, evaluator))
                    {
                        var index = packages.FindIndex(p => p.Reference == reference);
                        if (index < 0) packages.Add((reference, new List<string>()));
                        if (targetFramework != null) packages[index < 0 ? packages.Count - 1 : index].TargetFrameworks.Add(targetFramework);
                    }
                }
            }

            var entry = new Dictionary<string, object?>
            {
                ["projectName"] = logical.Name,
                ["projectPath"] = FormatPath(logical.FilePath ?? "")
            };
            if (logical.IsMultiTargeted) entry["targetFrameworks"] = targetFrameworks.OfType<string>().OrderBy(t => t, StringComparer.Ordinal).ToList();
            entry["packages"] = packages.Select(p =>
            {
                var package = new Dictionary<string, object?>
                {
                    ["packageName"] = p.Reference.Name,
                    ["version"] = p.Reference.Version ?? "unknown",
                    ["privateAssets"] = p.Reference.PrivateAssets,
                    ["excludeAssets"] = p.Reference.ExcludeAssets
                };
                if (logical.IsMultiTargeted) package["targetFrameworks"] = p.TargetFrameworks.OrderBy(t => t, StringComparer.Ordinal).ToList();
                return package;
            }).ToList();
            entry["parseError"] = parseErrors.Count > 0 ? string.Join("; ", parseErrors.Distinct(StringComparer.Ordinal)) : null;
            projectDependencies.Add(entry);
        }

        return Task.FromResult(CreateSuccessResponse(
            data: new { projects = projectDependencies },
            ResultCounts.Complete(projectDependencies.Count),
            suggestedNextTools: new[] { "get_project_structure", "dependency_graph" }
        ));
    }

    // The generators of each scoped project (per target framework: each compiles with its
    // own generator output) with their timing, and the files they generate in path order.
    public async Task<object> GetSourceGeneratorsAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (projects, scopeError) = ResolveProjectScope(projectName);
        if (projects == null) return scopeError!;

        var generatorResults = new List<object>();
        foreach (var project in projects)
        {
            var generators = project.AnalyzerReferences
                .SelectMany(ar => ar.GetGenerators(LanguageNames.CSharp))
                .ToList();

            if (generators.Count == 0) continue;

            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            // Timing: run the SAME driver configuration once against the
            // project's base compilation and read per-generator elapsed time.
            // The resulting compilation is discarded — only GetTimingInfo
            // matters — so the double-generation hazard the augmented-
            // compilation path guards against does not apply. A misbehaving
            // generator leaves its elapsedMs null.
            var elapsedByGenerator = new Dictionary<ISourceGenerator, double>();
            try
            {
                var baseCompilation = await project.GetCompilationAsync(cancellationToken);
                if (baseCompilation != null)
                {
                    GeneratorDriver timingDriver = CreateGeneratorDriver(project, generators.ToImmutableArray());
                    timingDriver = timingDriver.RunGenerators(baseCompilation, cancellationToken);
                    foreach (var generatorTiming in timingDriver.GetTimingInfo().GeneratorTimes)
                    {
                        elapsedByGenerator[generatorTiming.Generator] =
                            Math.Round(generatorTiming.ElapsedTime.TotalMilliseconds, 2);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // A per-call timeout during generator timing must surface as cancellation,
                // matching ComputeAugmentedCompilationAsync — not be swallowed into a warning.
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Warning] Generator timing failed for '{project.Name}': {ex.Message}");
            }

            // Post-generator compilation has extra syntax trees for generated code.
            // Identify them as trees whose FilePath is not one of the project's own documents.
            var projectDocumentPaths = new HashSet<string>(
                project.Documents.Where(d => d.FilePath != null).Select(d => d.FilePath!),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            var generatedFiles = new List<(string HintPath, object Entry)>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (projectDocumentPaths.Contains(tree.FilePath)) continue;

                var text = await tree.GetTextAsync(cancellationToken);
                var hintPath = FormatPath(tree.FilePath);
                generatedFiles.Add((hintPath, new
                {
                    fileName = Path.GetFileName(tree.FilePath),
                    hintPath,
                    lineCount = text.Lines.Count
                }));
            }

            generatorResults.Add(new
            {
                projectName = project.Name,
                generators = generators.Select(g =>
                {
                    var underlying = UnwrapGeneratorType(g);
                    return new
                    {
                        typeName = underlying.FullName,
                        assemblyName = underlying.Assembly.GetName().Name,
                        elapsedMs = elapsedByGenerator.TryGetValue(g, out var ms) ? ms : (double?)null
                    };
                }).ToList(),
                generatedFiles = generatedFiles
                    .OrderBy(f => f.HintPath, StringComparer.Ordinal)
                    .Select(f => f.Entry)
                    .ToList()
            });
        }

        return CreateSuccessResponse(
            data: new { projects = generatorResults },
            ResultCounts.Complete(generatorResults.Count),
            suggestedNextTools: new[] { "get_generated_code", "get_diagnostics" }
        );
    }

    // Roslyn wraps every IIncrementalGenerator in an internal IncrementalGeneratorWrapper
    // when surfaced through ISourceGenerator. Reporting the wrapper's type name to callers
    // makes every project's generators look identical. Reach the inner generator via the
    // wrapper's "Generator" property when present; fall back to the runtime type otherwise.
    private static Type UnwrapGeneratorType(ISourceGenerator generator)
    {
        var t = generator.GetType();
        var prop = t.GetProperty("Generator",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        if (prop == null) return t;
        var inner = prop.GetValue(generator);
        return inner?.GetType() ?? t;
    }

    // The source of one generated file, addressed by its file name or the trailing whole
    // segments of its generated path ("GenImpl.g.cs", "MyGenerator/GenImpl.g.cs", or the
    // hintPath get_source_generators reports); a part of a segment never matches. The
    // project scope resolves like every project-scoped tool: "Multi" covers each target
    // framework, whose identical output is one file (with targetFrameworks), while
    // "Multi(net8.0)" selects one. A name that matches generated files at several paths, or
    // one path whose content differs between target frameworks, is AMBIGUOUS_MATCH listing
    // the candidates; no match is FILE_NOT_FOUND listing the generated file names.
    public async Task<object> GetGeneratedCodeAsync(
        string projectName, string generatedFileName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(projectName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "projectName is required",
                hint: "Pass the project that runs the generator (see get_source_generators).",
                context: new { parameter = "projectName" });
        }
        var (projects, scopeError) = ResolveProjectScope(projectName);
        if (projects == null) return scopeError!;

        var generated = new List<(string HintPath, SourceText Text, string? TargetFramework)>();
        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            // Only generated trees (not the project's hand-written documents).
            var projectDocumentPaths = new HashSet<string>(
                project.Documents.Where(d => d.FilePath != null).Select(d => d.FilePath!),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (projectDocumentPaths.Contains(tree.FilePath)) continue;
                generated.Add((FormatPath(tree.FilePath), await tree.GetTextAsync(cancellationToken), LogicalProject.TargetFrameworkOf(project)));
            }
        }

        var requested = generatedFileName.Replace('\\', '/');
        var suffix = "/" + requested.TrimStart('/');
        var matches = generated
            .Where(g => g.HintPath.Equals(requested, PathComparison) || g.HintPath.EndsWith(suffix, PathComparison))
            .ToList();
        if (matches.Count == 0)
        {
            var availableFiles = generated
                .Select(g => Path.GetFileName(g.HintPath))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return CreateErrorResponse(
                ErrorCodes.FileNotFound,
                $"Generated file '{generatedFileName}' not found in project '{projectName}'",
                hint: availableFiles.Count > 0
                    ? $"Generated files: {string.Join(", ", availableFiles)}"
                    : "The project has no generated files (see get_source_generators).",
                context: new { projectName, generatedFileName, availableFiles });
        }

        var byPath = matches
            .GroupBy(m => m.HintPath, SitePathComparer)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        if (byPath.Count > 1)
        {
            return CreateErrorResponse(
                ErrorCodes.AmbiguousMatch,
                $"'{generatedFileName}' matches {byPath.Count} generated files",
                hint: "Pass more of the generated path (a generator folder and the file name) to name one of them.",
                context: new { projectName, generatedFileName, candidates = byPath.Select(g => g.Key).ToList() });
        }

        var file = byPath[0].ToList();
        if (file.Select(f => f.Text.ToString()).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return CreateErrorResponse(
                ErrorCodes.AmbiguousMatch,
                $"'{generatedFileName}' differs between the target frameworks of '{projectName}'",
                hint: "Pass the project name with its target framework, e.g. 'Name(net8.0)'.",
                context: new { projectName, generatedFileName, targetFrameworks = file.Select(f => f.TargetFramework).OfType<string>().OrderBy(t => t, StringComparer.Ordinal).ToList() });
        }

        var chosen = file[0];
        var data = WithTargetFrameworks(new Dictionary<string, object?>
        {
            ["fileName"] = Path.GetFileName(chosen.HintPath),
            ["hintPath"] = chosen.HintPath,
            ["sourceCode"] = chosen.Text.ToString(),
            ["lineCount"] = chosen.Text.Lines.Count
        }, file.Select(f => f.TargetFramework).OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList());

        return CreateSuccessResponse(
            data: data,
            suggestedNextTools: new[] { "get_diagnostics", "get_file_overview" }
        );
    }

    // Whether an attribute's class is the one a name denotes, case-insensitively: its
    // simple name with or without the trailing "Attribute" suffix ("Obsolete",
    // "ObsoleteAttribute"), or its fully-qualified name with or without it
    // ("System.Obsolete", "System.ObsoleteAttribute").
    private static bool MatchesAttribute(AttributeData attr, string attributeName)
    {
        if (attr.AttributeClass is not { } attributeClass) return false;
        return NameMatches(attributeClass.Name) || NameMatches(attributeClass.ToDisplayString());

        bool NameMatches(string className)
        {
            // Strip only a TRAILING "Attribute" suffix — not every occurrence — so
            // "FooAttributeBarAttribute" reduces to "FooAttributeBar", never "FooBar".
            var withoutSuffix = className.EndsWith("Attribute", StringComparison.Ordinal)
                ? className[..^"Attribute".Length]
                : className;
            return className.Equals(attributeName, StringComparison.OrdinalIgnoreCase)
                || withoutSuffix.Equals(attributeName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(Compilation compilation)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.GlobalNamespace);

        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            foreach (var type in ns.GetTypeMembers())
            {
                yield return type;
                foreach (var nested in GetNestedTypes(type))
                    yield return nested;
            }
            foreach (var childNs in ns.GetNamespaceMembers())
                stack.Push(childNs);
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deepNested in GetNestedTypes(nested))
                yield return deepNested;
        }
    }

}
