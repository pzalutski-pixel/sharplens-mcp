using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Xml.Linq;

namespace SharpLensMcp;

public partial class RoslynService
{

    public async Task<object> FindAttributeUsagesAsync(string attributeName, string? projectName = null, int maxResults = 100)
    {
        EnsureSolutionLoaded();

        var results = new List<object>();
        var projects = projectName != null
            ? _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : _solution!.Projects;

        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var type in GetAllNamedTypes(compilation))
            {
                // Check the type itself
                foreach (var attr in type.GetAttributes())
                {
                    if (MatchesAttribute(attr, attributeName))
                    {
                        results.Add(new
                        {
                            symbolName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            symbolKind = "Type",
                            attributeName = attr.AttributeClass?.Name,
                            arguments = attr.ConstructorArguments.Select(a => a.ToCSharpString()).ToList(),
                            namedArguments = attr.NamedArguments.ToDictionary(a => a.Key, a => a.Value.ToCSharpString()),
                            location = GetSymbolLocation(type)
                        });
                        if (results.Count >= maxResults) break;
                    }
                }

                if (results.Count >= maxResults) break;

                // Check members
                foreach (var member in type.GetMembers())
                {
                    foreach (var attr in member.GetAttributes())
                    {
                        if (MatchesAttribute(attr, attributeName))
                        {
                            results.Add(new
                            {
                                symbolName = $"{type.Name}.{member.Name}",
                                symbolKind = member.Kind.ToString(),
                                attributeName = attr.AttributeClass?.Name,
                                arguments = attr.ConstructorArguments.Select(a => a.ToCSharpString()).ToList(),
                                namedArguments = attr.NamedArguments.ToDictionary(a => a.Key, a => a.Value.ToCSharpString()),
                                location = GetSymbolLocation(member)
                            });
                            if (results.Count >= maxResults) break;
                        }
                    }
                    if (results.Count >= maxResults) break;
                }
                if (results.Count >= maxResults) break;
            }
            if (results.Count >= maxResults) break;
        }

        return CreateSuccessResponse(
            data: new { attributeFilter = attributeName, usages = results },
            suggestedNextTools: new[] { "get_type_overview", "find_references" },
            totalCount: results.Count,
            returnedCount: results.Count
        );
    }

    public async Task<object> GetDiRegistrationsAsync(string? projectName = null)
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
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var document in project.Documents)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var invocation in invocations)
                {
                    var methodName = GetInvocationMethodName(invocation);
                    if (methodName == null || !diMethodPatterns.Any(p => methodName.StartsWith(p))) continue;

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
                    var serviceType = typeArgs.Length > 0 ? typeArgs[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) : null;
                    var implementationType = typeArgs.Length > 1 ? typeArgs[1].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) : serviceType;

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

    public async Task<object> FindReflectionUsageAsync(string? projectName = null, int maxResults = 100)
    {
        EnsureSolutionLoaded();

        var usages = new List<object>();
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
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var document in project.Documents)
            {
                var syntaxTree = await document.GetSyntaxTreeAsync();
                if (syntaxTree == null) continue;

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync();

                var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();
                foreach (var invocation in invocations)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) continue;

                    var containingNamespace = methodSymbol.ContainingType?.ContainingNamespace?.ToDisplayString();
                    if (containingNamespace == null) continue;

                    var isReflection = containingNamespace.StartsWith("System.Reflection")
                        || (containingNamespace == "System" && methodSymbol.ContainingType?.Name == "Type" && reflectionApis.Contains(methodSymbol.Name))
                        || (containingNamespace == "System" && methodSymbol.ContainingType?.Name == "Activator")
                        || (containingNamespace == "System" && methodSymbol.ContainingType?.Name == "Delegate" && methodSymbol.Name == "DynamicInvoke");

                    if (!isReflection) continue;

                    var lineSpan = invocation.GetLocation().GetLineSpan();
                    usages.Add(new
                    {
                        reflectionApi = $"{methodSymbol.ContainingType?.Name}.{methodSymbol.Name}",
                        context = invocation.ToString().Length > 200 ? invocation.ToString()[..200] + "..." : invocation.ToString(),
                        location = new
                        {
                            filePath = FormatPath(lineSpan.Path),
                            line = lineSpan.StartLinePosition.Line,
                            column = lineSpan.StartLinePosition.Character
                        }
                    });
                    if (usages.Count >= maxResults) break;
                }
                if (usages.Count >= maxResults) break;
            }
            if (usages.Count >= maxResults) break;
        }

        return CreateSuccessResponse(
            data: new { usages },
            suggestedNextTools: new[] { "find_references", "get_symbol_info" },
            totalCount: usages.Count,
            returnedCount: usages.Count
        );
    }

    public Task<object> FindCircularDependenciesAsync(string? level = null)
    {
        EnsureSolutionLoaded();

        var analysisLevel = level?.ToLower() ?? "project";

        if (analysisLevel == "project")
        {
            var projectGraph = new Dictionary<string, List<string>>();
            foreach (var project in _solution!.Projects)
            {
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
        return FindNamespaceCircularDependenciesAsync();
    }

    private async Task<object> FindNamespaceCircularDependenciesAsync()
    {
        var namespaceGraph = new Dictionary<string, HashSet<string>>();

        foreach (var project in _solution!.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var root = await syntaxTree.GetRootAsync();
                var semanticModel = compilation.GetSemanticModel(syntaxTree);

                // Get namespace of this file
                var namespaceDecl = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                if (namespaceDecl == null) continue;
                var currentNamespace = namespaceDecl.Name.ToString();

                if (!namespaceGraph.ContainsKey(currentNamespace))
                    namespaceGraph[currentNamespace] = new HashSet<string>();

                // Get referenced namespaces from using directives
                var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>();
                foreach (var u in usings)
                {
                    var referencedNamespace = u.Name?.ToString();
                    if (referencedNamespace != null && referencedNamespace != currentNamespace)
                    {
                        namespaceGraph[currentNamespace].Add(referencedNamespace);
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
                namespaceCount = namespaceGraph.Count
            },
            suggestedNextTools: new[] { "dependency_graph", "get_project_structure" }
        );
    }

    public Task<object> GetNuGetDependenciesAsync(string? projectName = null)
    {
        EnsureSolutionLoaded();

        var projectDependencies = new List<object>();
        var projects = projectName != null
            ? _solution!.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : _solution!.Projects;

        foreach (var project in projects)
        {
            var packages = new List<object>();
            var projectFilePath = project.FilePath;

            if (projectFilePath != null && File.Exists(projectFilePath))
            {
                try
                {
                    var csprojContent = File.ReadAllText(projectFilePath);
                    var doc = System.Xml.Linq.XDocument.Parse(csprojContent);
                    var packageRefs = doc.Descendants("PackageReference");

                    foreach (var pkgRef in packageRefs)
                    {
                        var name = pkgRef.Attribute("Include")?.Value;
                        var version = pkgRef.Attribute("Version")?.Value ?? pkgRef.Element("Version")?.Value;
                        var privateAssets = pkgRef.Attribute("PrivateAssets")?.Value ?? pkgRef.Element("PrivateAssets")?.Value;
                        var excludeAssets = pkgRef.Attribute("ExcludeAssets")?.Value ?? pkgRef.Element("ExcludeAssets")?.Value;

                        if (name != null)
                        {
                            packages.Add(new
                            {
                                packageName = name,
                                version = version ?? "unknown",
                                privateAssets = privateAssets,
                                excludeAssets = excludeAssets
                            });
                        }
                    }
                }
                catch
                {
                    // Failed to parse csproj, skip
                }
            }

            projectDependencies.Add(new
            {
                projectName = project.Name,
                projectPath = FormatPath(project.FilePath ?? ""),
                packages
            });
        }

        return Task.FromResult(CreateSuccessResponse(
            data: new { projects = projectDependencies },
            suggestedNextTools: new[] { "get_project_structure", "dependency_graph" },
            totalCount: projectDependencies.Count,
            returnedCount: projectDependencies.Count
        ));
    }

    public async Task<object> GetSourceGeneratorsAsync(string? projectName = null)
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

            var compilation = await project.GetCompilationAsync();
            if (compilation == null) continue;

            var generatedFiles = new List<object>();
            foreach (var tree in compilation.SyntaxTrees)
            {
                var filePath = tree.FilePath;
                if (!string.IsNullOrEmpty(filePath) && filePath.Contains("Generated"))
                {
                    generatedFiles.Add(new
                    {
                        fileName = Path.GetFileName(filePath),
                        filePath = FormatPath(filePath),
                        lineCount = (await tree.GetRootAsync()).GetText().Lines.Count
                    });
                }
            }

            generatorResults.Add(new
            {
                projectName = project.Name,
                generators = generators.Select(g => new
                {
                    typeName = g.GetType().FullName,
                    assemblyName = g.GetType().Assembly.GetName().Name
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

    public async Task<object> GetGeneratedCodeAsync(string projectName, string generatedFileName)
    {
        EnsureSolutionLoaded();

        var project = _solution!.Projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (project == null)
        {
            return CreateErrorResponse(ErrorCodes.FileNotFound, $"Project '{projectName}' not found");
        }

        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
        {
            return CreateErrorResponse(ErrorCodes.AnalysisFailed, "Could not get compilation");
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree.FilePath.Contains(generatedFileName, StringComparison.OrdinalIgnoreCase))
            {
                var text = await tree.GetTextAsync();
                return CreateSuccessResponse(
                    data: new
                    {
                        fileName = Path.GetFileName(tree.FilePath),
                        filePath = FormatPath(tree.FilePath),
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

        return className.Equals(attributeName, StringComparison.OrdinalIgnoreCase)
            || className.Equals($"{attributeName}Attribute", StringComparison.OrdinalIgnoreCase)
            || className.Replace("Attribute", "").Equals(attributeName, StringComparison.OrdinalIgnoreCase);
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
