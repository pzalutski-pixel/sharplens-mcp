using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Xml.Linq;

namespace SharpLensMcp;

public partial class RoslynService
{

    public async Task<object> FindAttributeUsagesAsync(
        string attributeName, string? projectName = null, int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var results = new List<object>();
        var totalFound = 0;
        var projects = projectName != null
            ? _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : _solution!.Projects;

        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            foreach (var type in GetAllNamedTypes(compilation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // GetAllNamedTypes walks the merged global namespace, which includes
                // every referenced assembly — restrict to this project's own source
                // types so the BCL isn't scanned and sibling projects aren't double-
                // counted by their referencing projects' iterations.
                if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)) continue;

                foreach (var attr in type.GetAttributes())
                {
                    if (!MatchesAttribute(attr, attributeName)) continue;
                    totalFound++;
                    if (results.Count >= maxResults) continue;
                    results.Add(new
                    {
                        symbolName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        symbolKind = "Type",
                        attributeName = attr.AttributeClass?.Name,
                        arguments = attr.ConstructorArguments.Select(a => a.ToCSharpString()).ToList(),
                        namedArguments = attr.NamedArguments.ToDictionary(a => a.Key, a => a.Value.ToCSharpString()),
                        location = GetSymbolLocation(type)
                    });
                }

                foreach (var member in type.GetMembers())
                {
                    // Nested types are reported by the outer type walk, not as members
                    // of their container.
                    if (member is INamedTypeSymbol) continue;
                    foreach (var attr in member.GetAttributes())
                    {
                        if (!MatchesAttribute(attr, attributeName)) continue;
                        totalFound++;
                        if (results.Count >= maxResults) continue;
                        results.Add(new
                        {
                            symbolName = $"{type.Name}.{member.Name}",
                            symbolKind = member.Kind.ToString(),
                            attributeName = attr.AttributeClass?.Name,
                            arguments = attr.ConstructorArguments.Select(a => a.ToCSharpString()).ToList(),
                            namedArguments = attr.NamedArguments.ToDictionary(a => a.Key, a => a.Value.ToCSharpString()),
                            location = GetSymbolLocation(member)
                        });
                    }
                }
            }
        }

        return CreateSuccessResponse(
            data: new { attributeFilter = attributeName, usages = results },
            suggestedNextTools: new[] { "get_type_overview", "find_references" },
            totalCount: totalFound,
            returnedCount: results.Count
        );
    }

    public async Task<object> GetDiRegistrationsAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var registrations = new List<object>();
        var diMethodPatterns = new[]
        {
            "AddScoped", "AddTransient", "AddSingleton", "AddHostedService",
            "TryAddScoped", "TryAddTransient", "TryAddSingleton",
            "AddKeyedScoped", "AddKeyedTransient", "AddKeyedSingleton"
        };

        var projects = projectName != null
            ? _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : _solution!.Projects;

        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (syntaxTree == null) continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var invocation in invocations)
                {
                    var methodName = GetInvocationMethodName(invocation);
                    // Exact match: a `StartsWith` test would also catch a user's own
                    // AddSingletonCache / AddScopedFactory helpers.
                    if (methodName == null || !diMethodPatterns.Contains(methodName)) continue;

                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) continue;

                    var containingType = methodSymbol.ContainingType?.ToDisplayString();
                    if (containingType == null || !containingType.Contains("ServiceCollection")) continue;

                    var lifetime = methodName.Contains("Scoped") ? "Scoped"
                        : methodName.Contains("Transient") ? "Transient"
                        : methodName.Contains("Singleton") ? "Singleton"
                        : methodName.Contains("Hosted") ? "Singleton"
                        : "Unknown";

                    var typeArgs = methodSymbol.TypeArguments;
                    var serviceType = typeArgs.Length > 0
                        ? typeArgs[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) : null;
                    var implementationType = typeArgs.Length > 1
                        ? typeArgs[1].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                        : typeArgs.Length == 1 ? serviceType : null;

                    if (methodName == "AddHostedService" && typeArgs.Length >= 1)
                    {
                        // AddHostedService<T> registers T as the IHostedService service,
                        // not as its own service type.
                        implementationType = typeArgs[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                        serviceType = "IHostedService";
                    }

                    // Non-generic registrations carry the types as typeof(...) arguments
                    // or a factory lambda whose produced type is the implementation.
                    if (serviceType == null || implementationType == null)
                    {
                        var systemType = compilation.GetTypeByMetadataName("System.Type");
                        var typeofTypes = new List<string>();
                        string? factoryType = null;
                        var args = invocation.ArgumentList.Arguments;
                        for (var i = 0; i < args.Count; i++)
                        {
                            var expr = args[i].Expression;
                            // Only typeof arguments bound to a System.Type parameter are
                            // service/implementation types; a keyed overload's object key
                            // is skipped. argument[i] aligns with Parameters[i] for both
                            // the reduced-extension and static-call symbol shapes.
                            var param = i < methodSymbol.Parameters.Length ? methodSymbol.Parameters[i] : null;
                            var paramIsType = param == null
                                || (systemType != null && SymbolEqualityComparer.Default.Equals(param.Type, systemType));

                            if (expr is TypeOfExpressionSyntax typeOf && paramIsType
                                && semanticModel.GetTypeInfo(typeOf.Type, cancellationToken).Type is { } resolvedType)
                            {
                                typeofTypes.Add(resolvedType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                            }
                            else if (expr is LambdaExpressionSyntax lambda
                                && FactoryProducedType(lambda, semanticModel, cancellationToken) is { } producedType)
                            {
                                factoryType = producedType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                            }
                        }
                        serviceType ??= typeofTypes.ElementAtOrDefault(0);
                        implementationType ??= typeofTypes.ElementAtOrDefault(1) ?? factoryType ?? serviceType;
                    }

                    var lineSpan = invocation.GetLocation().GetLineSpan();
                    registrations.Add(new
                    {
                        lifetime,
                        serviceType,
                        implementationType,
                        method = methodName,
                        location = new
                        {
                            filePath = FormatPath(lineSpan.Path),
                            line = lineSpan.StartLinePosition.Line,
                            column = lineSpan.StartLinePosition.Character
                        }
                    });
                }
            }
        }

        return CreateSuccessResponse(
            data: new { registrations },
            suggestedNextTools: new[] { "find_references", "get_type_overview" },
            totalCount: registrations.Count,
            returnedCount: registrations.Count
        );
    }

    // The type a DI factory lambda produces: the body expression for an
    // expression-bodied lambda, or the last return in a block-bodied one (skipping
    // returns that belong to nested lambdas/local functions).
    private static ITypeSymbol? FactoryProducedType(
        LambdaExpressionSyntax lambda, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        if (lambda.Body is ExpressionSyntax expr)
            return semanticModel.GetTypeInfo(expr, cancellationToken).Type;
        if (lambda.Body is BlockSyntax block)
        {
            var returned = block
                .DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax
                                   && n is not LocalFunctionStatementSyntax)
                .OfType<ReturnStatementSyntax>()
                .Select(r => r.Expression)
                .LastOrDefault(e => e != null);
            if (returned != null)
                return semanticModel.GetTypeInfo(returned, cancellationToken).Type;
        }
        return null;
    }

    public async Task<object> FindReflectionUsageAsync(
        string? projectName = null, int maxResults = 100, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var hits = new List<(bool lowSignal, object entry)>();
        var reflectionApis = new[]
        {
            "GetType", "GetMethod", "GetProperty", "GetField", "GetEvent",
            "GetMember", "GetMembers", "GetMethods", "GetProperties", "GetFields",
            "CreateInstance", "Invoke", "GetValue", "SetValue", "DynamicInvoke",
            "MakeGenericType", "MakeGenericMethod"
        };

        var projects = projectName != null
            ? _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : _solution!.Projects;

        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                if (syntaxTree == null) continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var invocation in invocations)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) continue;

                    var containingNamespace = methodSymbol.ContainingType?.ContainingNamespace?.ToDisplayString();
                    if (containingNamespace == null) continue;

                    // System.Type members (Type.GetType, GetMethod, …), Activator,
                    // Delegate.DynamicInvoke, everything in System.Reflection, and the
                    // instance Object.GetType() runtime-type query. The last is the
                    // highest-volume and lowest-signal of these; it stays distinguishable
                    // by the "Object.GetType" reflectionApi so callers can filter it out.
                    var isReflection = containingNamespace.StartsWith("System.Reflection")
                        || (containingNamespace == "System" && methodSymbol.ContainingType?.Name == "Type" && reflectionApis.Contains(methodSymbol.Name))
                        || (containingNamespace == "System" && methodSymbol.ContainingType?.Name == "Object" && methodSymbol.Name == "GetType")
                        || (containingNamespace == "System" && methodSymbol.ContainingType?.Name == "Activator")
                        || (containingNamespace == "System" && methodSymbol.ContainingType?.Name == "Delegate" && methodSymbol.Name == "DynamicInvoke");

                    if (!isReflection) continue;

                    var lowSignal = methodSymbol.ContainingType?.Name == "Object" && methodSymbol.Name == "GetType";
                    var lineSpan = invocation.GetLocation().GetLineSpan();
                    hits.Add((lowSignal, new
                    {
                        reflectionApi = $"{methodSymbol.ContainingType?.Name}.{methodSymbol.Name}",
                        context = TruncateForSnippet(invocation.ToString(), 200, "..."),
                        location = new
                        {
                            filePath = FormatPath(lineSpan.Path),
                            line = lineSpan.StartLinePosition.Line,
                            column = lineSpan.StartLinePosition.Character
                        }
                    }));
                }
            }
        }

        // High-signal (string-driven) reflection is ordered ahead of the common
        // Object.GetType() runtime-type query so the latter never crowds the
        // meaningful hits out of a capped page (stable within each group).
        var usages = hits
            .OrderBy(h => h.lowSignal ? 1 : 0)
            .Take(maxResults)
            .Select(h => h.entry)
            .ToList();

        return CreateSuccessResponse(
            data: new { usages },
            suggestedNextTools: new[] { "find_references", "get_symbol_info" },
            totalCount: hits.Count,
            returnedCount: usages.Count
        );
    }

    public Task<object> FindCircularDependenciesAsync(
        string? level = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var analysisLevel = level?.ToLower() ?? "project";

        // Validate level explicitly — silently treating unknown values as namespace
        // (the prior behavior) hides typos from callers and produces wrong-but-plausible
        // output. The supported set is just {project, namespace}.
        if (analysisLevel != "project" && analysisLevel != "namespace")
        {
            return Task.FromResult(CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"level must be 'project' or 'namespace' (got '{level}')",
                context: new { level }
            ));
        }

        if (analysisLevel == "project")
        {
            var projectGraph = new Dictionary<string, List<string>>();
            foreach (var project in _solution!.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dependencies = project.ProjectReferences
                    .Select(pr => _solution!.GetProject(pr.ProjectId)?.Name)
                    .Where(name => name != null)
                    .Cast<string>()
                    .ToList();
                projectGraph[project.Name] = dependencies;
            }

            var cycles = DetectCycles(projectGraph);
            return Task.FromResult(CreateSuccessResponse(
                data: new { level = analysisLevel, hasCycles = cycles.Count > 0, cycles, graph = projectGraph },
                suggestedNextTools: new[] { "dependency_graph", "get_project_structure" }
            ));
        }

        // Namespace level
        return FindNamespaceCircularDependenciesAsync(cancellationToken);
    }

    private async Task<object> FindNamespaceCircularDependenciesAsync(CancellationToken cancellationToken = default)
    {
        var namespaceGraph = new Dictionary<string, HashSet<string>>();

        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var root = await syntaxTree.GetRootAsync(cancellationToken);
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var fileUsings = (root as CompilationUnitSyntax)?.Usings ?? default;

                // Every namespace declared in the file — not just the first — and the
                // usings actually in scope for each (its own + enclosing + file-level).
                foreach (var namespaceDecl in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                {
                    var currentNamespace =
                        (semanticModel.GetDeclaredSymbol(namespaceDecl, cancellationToken) as INamespaceSymbol)
                            ?.ToDisplayString() ?? namespaceDecl.Name.ToString();

                    if (!namespaceGraph.ContainsKey(currentNamespace))
                        namespaceGraph[currentNamespace] = new HashSet<string>();

                    var inScopeUsings = namespaceDecl.AncestorsAndSelf()
                        .OfType<BaseNamespaceDeclarationSyntax>()
                        .SelectMany(n => n.Usings)
                        .Concat(fileUsings);
                    foreach (var u in inScopeUsings)
                    {
                        if (u.Name == null) continue;
                        // Resolve the using to its namespace symbol so aliases and
                        // global:: prefixes map to the canonical namespace name.
                        var referencedNamespace =
                            (semanticModel.GetSymbolInfo(u.Name, cancellationToken).Symbol as INamespaceSymbol)
                                ?.ToDisplayString() ?? u.Name.ToString();
                        if (referencedNamespace != currentNamespace)
                        {
                            namespaceGraph[currentNamespace].Add(referencedNamespace);
                        }
                    }
                }
            }
        }

        var graphForCycleDetection = namespaceGraph.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToList()
        );
        var cycles = DetectCycles(graphForCycleDetection);

        return CreateSuccessResponse(
            data: new
            {
                level = "namespace",
                hasCycles = cycles.Count > 0,
                cycles,
                namespaceCount = namespaceGraph.Count,
                // Emit the graph for consistency with the project-level response. Without
                // this, the namespace-level response shape diverged from project-level for
                // no documented reason.
                graph = graphForCycleDetection
            },
            suggestedNextTools: new[] { "dependency_graph", "get_project_structure" }
        );
    }

    public Task<object> GetNuGetDependenciesAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var projectDependencies = new List<object>();
        var projects = projectName != null
            ? _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : _solution!.Projects;

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packages = new List<object>();
            var projectFilePath = project.FilePath;

            string? parseError = null;
            if (projectFilePath != null && File.Exists(projectFilePath))
            {
                // Effective references: merges Directory.Build.props and resolves CPM
                // versions from Directory.Packages.props, not just the leaf csproj. A
                // malformed props/csproj is reported via parseError, not swallowed.
                var parseErrors = new List<string>();
                foreach (var pkgRef in CollectEffectivePackageReferences(projectFilePath, parseErrors))
                {
                    packages.Add(new
                    {
                        packageName = pkgRef.Name,
                        version = pkgRef.Version ?? "unknown",
                        privateAssets = pkgRef.PrivateAssets,
                        excludeAssets = pkgRef.ExcludeAssets
                    });
                }
                if (parseErrors.Count > 0) parseError = string.Join("; ", parseErrors);
            }

            projectDependencies.Add(new
            {
                projectName = project.Name,
                projectPath = FormatPath(project.FilePath ?? ""),
                packages,
                parseError
            });
        }

        return Task.FromResult(CreateSuccessResponse(
            data: new { projects = projectDependencies },
            suggestedNextTools: new[] { "get_project_structure", "dependency_graph" },
            totalCount: projectDependencies.Count,
            returnedCount: projectDependencies.Count
        ));
    }

    public async Task<object> GetSourceGeneratorsAsync(
        string? projectName = null, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var generatorResults = new List<object>();
        var projects = projectName != null
            ? _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : _solution!.Projects;

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

            var generatedFiles = new List<object>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (projectDocumentPaths.Contains(tree.FilePath)) continue;

                var text = await tree.GetTextAsync(cancellationToken);
                generatedFiles.Add(new
                {
                    fileName = Path.GetFileName(tree.FilePath),
                    hintPath = tree.FilePath,
                    lineCount = text.Lines.Count
                });
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
                generatedFiles
            });
        }

        return CreateSuccessResponse(
            data: new { projects = generatorResults },
            suggestedNextTools: new[] { "get_generated_code", "get_diagnostics" },
            totalCount: generatorResults.Count,
            returnedCount: generatorResults.Count
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

    public async Task<object> GetGeneratedCodeAsync(
        string projectName, string generatedFileName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var project = _solution!.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            return ProjectNotFoundError(projectName);
        }

        var compilation = await GetProjectCompilationAsync(project, cancellationToken);
        if (compilation == null)
        {
            return CreateErrorResponse(ErrorCodes.AnalysisFailed, "Could not get compilation");
        }

        // Only search generated trees (not the project's hand-written documents).
        var projectDocumentPaths = new HashSet<string>(
            project.Documents.Where(d => d.FilePath != null).Select(d => d.FilePath!),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (projectDocumentPaths.Contains(tree.FilePath)) continue;

            if (tree.FilePath.Contains(generatedFileName, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(tree.FilePath).Contains(generatedFileName, StringComparison.OrdinalIgnoreCase))
            {
                var text = await tree.GetTextAsync(cancellationToken);
                return CreateSuccessResponse(
                    data: new
                    {
                        fileName = Path.GetFileName(tree.FilePath),
                        hintPath = tree.FilePath,
                        sourceCode = text.ToString(),
                        lineCount = text.Lines.Count
                    },
                    suggestedNextTools: new[] { "get_diagnostics", "get_file_overview" }
                );
            }
        }

        return CreateErrorResponse(ErrorCodes.FileNotFound, $"Generated file '{generatedFileName}' not found in project '{projectName}'");
    }

    private static bool MatchesAttribute(AttributeData attr, string attributeName)
    {
        var className = attr.AttributeClass?.Name;
        if (className == null) return false;

        // Strip only a TRAILING "Attribute" suffix — not every occurrence — so
        // "FooAttributeBarAttribute" reduces to "FooAttributeBar", never "FooBar".
        var withoutSuffix = className.EndsWith("Attribute", StringComparison.Ordinal)
            ? className[..^"Attribute".Length]
            : className;
        return className.Equals(attributeName, StringComparison.OrdinalIgnoreCase)
            || className.Equals($"{attributeName}Attribute", StringComparison.OrdinalIgnoreCase)
            || withoutSuffix.Equals(attributeName, StringComparison.OrdinalIgnoreCase);
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

    private static string? GetInvocationMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }


}
