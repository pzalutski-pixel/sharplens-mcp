using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SharpLensMcp;

// Read-only analysis/inspection tools. Previously co-located with the actual
// refactoring tools in RoslynService.Refactoring.cs; the split mirrors the
// v1.5.0 partial-class organization.
public partial class RoslynService
{
    public async Task<object> GetMethodOverloadsAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not IMethodSymbol methodSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a method symbol",
                hint: "This tool requires a method symbol. Use get_method_signature with type and method names instead.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        // Get all members of the containing type with the same name
        var containingType = methodSymbol.ContainingType;
        var overloads = containingType.GetMembers(methodSymbol.Name)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .ToList();

        var overloadList = overloads.Select(m =>
        {
            var location = m.Locations.FirstOrDefault(loc => loc.IsInSource);
            var lineSpan = location?.GetLineSpan();

            return new
            {
                signature = m.ToDisplayString(),
                parameters = m.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type.ToDisplayString(),
                    isOptional = p.IsOptional,
                    defaultValue = FormatDefaultValue(p)
                }).ToList(),
                returnType = m.ReturnType.ToDisplayString(),
                isAsync = m.IsAsync,
                isStatic = m.IsStatic,
                location = lineSpan != null ? new
                {
                    filePath = FormatPath(lineSpan.Value.Path),
                    line = lineSpan.Value.StartLinePosition.Line,
                    column = lineSpan.Value.StartLinePosition.Character
                } : null
            };
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                methodName = methodSymbol.Name,
                containingType = containingType.ToDisplayString(),
                overloads = overloadList
            },
            suggestedNextTools: new[]
            {
                "find_references to see where overloads are called",
                "get_method_signature for detailed signature info"
            },
            totalCount: overloadList.Count,
            returnedCount: overloadList.Count
        );
    }

    public async Task<object> GetContainingMemberAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        // Walk up the syntax tree to find the containing member
        var memberNode = pos.Token.Parent?.AncestorsAndSelf().FirstOrDefault(n =>
            n is MethodDeclarationSyntax or
            PropertyDeclarationSyntax or
            ConstructorDeclarationSyntax or
            ClassDeclarationSyntax or
            StructDeclarationSyntax or
            InterfaceDeclarationSyntax);

        if (memberNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "No containing member found",
                hint: "Position may be outside any method, property, or type declaration",
                context: new { filePath, line, column }
            );
        }

        var symbol = pos.Model.GetDeclaredSymbol(memberNode, cancellationToken);
        if (symbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Could not resolve symbol for containing member",
                context: new { filePath, line, column }
            );
        }

        var span = memberNode.Span;
        var lineSpan = pos.Tree.GetLineSpan(span);

        return CreateSuccessResponse(
            data: new
            {
                memberName = symbol.Name,
                memberKind = symbol.Kind.ToString(),
                containingType = symbol.ContainingType?.ToDisplayString(),
                signature = symbol.ToDisplayString(),
                span = new
                {
                    startLine = lineSpan.StartLinePosition.Line,
                    startColumn = lineSpan.StartLinePosition.Character,
                    endLine = lineSpan.EndLinePosition.Line,
                    endColumn = lineSpan.EndLinePosition.Character
                }
            },
            suggestedNextTools: new[]
            {
                $"find_references to see usages of {symbol.Name}",
                "get_symbol_info for more details"
            }
        );
    }

    public async Task<object> FindUnusedCodeAsync(
        string? projectName,
        bool includePrivate,
        bool includeInternal,
        string? symbolKindFilter = null,
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();
        if (ValidateUnusedCodeKindFilter(symbolKindFilter) is { } filterError) return filterError;
        var data = await FindUnusedCodeDataAsyncCore(projectName, includePrivate, includeInternal, symbolKindFilter, maxResults ?? 50, namePredicate: null, cancellationToken);

        var countByKind = data.Symbols
            .GroupBy(s => s.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        return CreateSuccessResponse(
            data: new
            {
                projectName = projectName ?? "All projects",
                countByKind,
                unusedSymbols = data.Symbols.Select(s => new
                {
                    name = s.Name,
                    fullyQualifiedName = s.FullName,
                    kind = s.Kind,
                    accessibility = s.Accessibility,
                    containingType = s.ContainingType,
                    filePath = s.FilePath,
                    line = s.Line,
                    column = s.Column
                }).ToList()
            },
            suggestedNextTools: data.Symbols.Count > 0
                ? new[] { "find_references to verify symbol is truly unused", "rename_symbol or delete unused code" }
                : new[] { "No unused code found - codebase is clean" },
            totalCount: data.TotalCount,
            returnedCount: data.Symbols.Count
        );
    }

    // Internal typed-data variant used by GetProjectHealthAsync and
    // RemoveUnusedCodeAsync. namePredicate narrows the candidate sweep to
    // matching symbol names BEFORE any reference counting — an allow-list
    // query checks a handful of names instead of the whole project.
    internal Task<UnusedCodeData> FindUnusedCodeDataAsync(
        string? projectName,
        bool includePrivate,
        bool includeInternal,
        string? symbolKindFilter,
        int maxResults,
        Func<string, bool>? namePredicate = null,
        CancellationToken cancellationToken = default) => FindUnusedCodeDataAsyncCore(projectName, includePrivate, includeInternal, symbolKindFilter, maxResults, namePredicate, cancellationToken);

    private async Task<UnusedCodeData> FindUnusedCodeDataAsyncCore(
        string? projectName,
        bool includePrivate,
        bool includeInternal,
        string? symbolKindFilter,
        int maxResults,
        Func<string, bool>? namePredicate = null,
        CancellationToken cancellationToken = default)
    {
        // The returned list is capped at maxResults to bound the payload, but
        // totalUnused counts EVERY unused symbol so totalCount is the true total (and
        // meta.Truncated fires when the cap drops some). Identifying unused code
        // requires reference-checking every candidate, so this is a full scan by
        // nature — the only way to know what is unused — bounded by the per-call timeout.
        var unusedSymbols = new List<UnusedSymbolEntry>();
        var maxResultsToReturn = maxResults;
        var totalUnused = 0;

        var projectsToAnalyze = string.IsNullOrEmpty(projectName)
            ? _solution!.Projects
            : _solution!.Projects.Where(p => p.Name == projectName);

        foreach (var project in projectsToAnalyze)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            var shouldAnalyzeTypes = string.IsNullOrEmpty(symbolKindFilter) ||
                                     symbolKindFilter.Equals("Class", StringComparison.OrdinalIgnoreCase) ||
                                     symbolKindFilter.Equals("Interface", StringComparison.OrdinalIgnoreCase) ||
                                     symbolKindFilter.Equals("Struct", StringComparison.OrdinalIgnoreCase) ||
                                     symbolKindFilter.Equals("Enum", StringComparison.OrdinalIgnoreCase) ||
                                     symbolKindFilter.Equals("Delegate", StringComparison.OrdinalIgnoreCase) ||
                                     symbolKindFilter.Equals("Type", StringComparison.OrdinalIgnoreCase);

            if (shouldAnalyzeTypes)
            {
                // Get all named type symbols (classes, interfaces, structs, enums, delegates)
                var allTypes = compilation.GetSymbolsWithName(namePredicate ?? (_ => true), SymbolFilter.Type)
                    .OfType<INamedTypeSymbol>();

                foreach (var typeSymbol in allTypes)
                {
                    // Skip compiler-generated, extern, and types not in source
                    if (typeSymbol.IsImplicitlyDeclared ||
                        typeSymbol.IsExtern ||
                        !typeSymbol.Locations.Any(loc => loc.IsInSource))
                        continue;

                    // Narrow to the requested kind: "Class" must not also report
                    // interfaces, structs, and enums.
                    if (!MatchesKindFilter(typeSymbol, symbolKindFilter)) continue;

                    // Filter by accessibility
                    if (!includePrivate && typeSymbol.DeclaredAccessibility == Accessibility.Private)
                        continue;
                    if (!includeInternal && typeSymbol.DeclaredAccessibility == Accessibility.Internal)
                        continue;

                    // Skip classes that implement framework interfaces (likely used via DI or framework)
                    if (ImplementsFrameworkInterface(typeSymbol))
                        continue;

                    // Skip classes with framework attributes (controllers, hosted services, etc.)
                    if (HasFrameworkAttribute(typeSymbol))
                        continue;

                    // Find references to this type. SymbolFinder reference locations
                    // exclude the declaration itself, so zero locations means unused.
                    var references = await SymbolFinder.FindReferencesAsync(typeSymbol, _solution!, cancellationToken);
                    var referenceCount = references.SelectMany(r => r.Locations).Count();

                    // For types, also check if any members are referenced
                    // This handles static classes where the class itself isn't referenced
                    // but its static methods/properties are called
                    var hasReferencedMembers = false;
                    if (referenceCount == 0) // Type itself has no references
                    {
                        // Check if any public/internal members are referenced
                        foreach (var member in typeSymbol.GetMembers())
                        {
                            // Skip constructors, compiler-generated, and special members
                            if (member.IsImplicitlyDeclared ||
                                member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
                                continue;

                            var memberRefs = await SymbolFinder.FindReferencesAsync(member, _solution!, cancellationToken);
                            var memberRefCount = memberRefs.SelectMany(r => r.Locations).Count();

                            if (memberRefCount > 0) // Member is referenced
                            {
                                hasReferencedMembers = true;
                                break; // No need to check other members
                            }
                        }
                    }

                    if (referenceCount == 0 && !hasReferencedMembers)
                    {
                        var location = typeSymbol.Locations.FirstOrDefault(loc => loc.IsInSource);
                        if (location != null)
                        {
                            totalUnused++;
                            if (unusedSymbols.Count < maxResultsToReturn)
                            {
                                var lineSpan = location.GetLineSpan();
                                unusedSymbols.Add(new UnusedSymbolEntry(
                                    Name: typeSymbol.Name,
                                    FullName: typeSymbol.ToDisplayString(),
                                    Kind: GetTypeKindString(typeSymbol),
                                    Accessibility: typeSymbol.DeclaredAccessibility.ToString(),
                                    ContainingType: null,
                                    FilePath: FormatPath(lineSpan.Path),
                                    Line: lineSpan.StartLinePosition.Line,
                                    Column: lineSpan.StartLinePosition.Character,
                                    Symbol: typeSymbol));
                            }
                        }
                    }
                }
            }

            // Check if we should analyze members
            var shouldAnalyzeMembers = string.IsNullOrEmpty(symbolKindFilter) ||
                                       symbolKindFilter.Equals("Method", StringComparison.OrdinalIgnoreCase) ||
                                       symbolKindFilter.Equals("Property", StringComparison.OrdinalIgnoreCase) ||
                                       symbolKindFilter.Equals("Field", StringComparison.OrdinalIgnoreCase) ||
                                       symbolKindFilter.Equals("Member", StringComparison.OrdinalIgnoreCase);

            if (shouldAnalyzeMembers)
            {
                // Also check methods, properties, and fields
                var allMembers = compilation.GetSymbolsWithName(namePredicate ?? (_ => true), SymbolFilter.Member);

                foreach (var member in allMembers)
                {
                    if (member is not (IMethodSymbol or IPropertySymbol or IFieldSymbol))
                        continue;

                    // Narrow to the requested kind: "Method" must not also report
                    // properties and fields.
                    if (!MatchesKindFilter(member, symbolKindFilter)) continue;

                    // Skip compiler-generated, extern, and symbols not in source
                    if (member.IsImplicitlyDeclared ||
                        member.IsExtern ||
                        !member.Locations.Any(loc => loc.IsInSource))
                        continue;

                    // Skip special methods (constructors, operators, etc.) and any
                    // polymorphic-dispatch member — abstract/virtual/override are reached
                    // through the type contract, not necessarily a direct call. Properties
                    // dispatch the same way, so an abstract/virtual/override property is
                    // excluded too.
                    if (member is IMethodSymbol method &&
                        (method.MethodKind != MethodKind.Ordinary || method.IsOverride
                         || method.IsVirtual || method.IsAbstract))
                        continue;
                    if (member is IPropertySymbol property &&
                        (property.IsOverride || property.IsVirtual || property.IsAbstract))
                        continue;

                    // Filter by accessibility
                    if (!includePrivate && member.DeclaredAccessibility == Accessibility.Private)
                        continue;
                    if (!includeInternal && member.DeclaredAccessibility == Accessibility.Internal)
                        continue;

                    // Find references. SymbolFinder reference locations exclude the
                    // declaration itself, so zero locations means unused.
                    var references = await SymbolFinder.FindReferencesAsync(member, _solution!, cancellationToken);
                    var referenceCount = references.SelectMany(r => r.Locations).Count();

                    if (referenceCount == 0)
                    {
                        var location = member.Locations.FirstOrDefault(loc => loc.IsInSource);
                        if (location != null)
                        {
                            totalUnused++;
                            if (unusedSymbols.Count < maxResultsToReturn)
                            {
                                var lineSpan = location.GetLineSpan();
                                unusedSymbols.Add(new UnusedSymbolEntry(
                                    Name: member.Name,
                                    FullName: member.ToDisplayString(),
                                    Kind: member.Kind.ToString(),
                                    Accessibility: member.DeclaredAccessibility.ToString(),
                                    ContainingType: member.ContainingType?.ToDisplayString(),
                                    FilePath: FormatPath(lineSpan.Path),
                                    Line: lineSpan.StartLinePosition.Line,
                                    Column: lineSpan.StartLinePosition.Character,
                                    Symbol: member));
                            }
                        }
                    }
                }
            }
        }

        return new UnusedCodeData(TotalCount: totalUnused, Symbols: unusedSymbols);
    }

    // The symbolKindFilter values find_unused_code and remove_unused_code accept.
    // The group gates and MatchesKindFilter only recognize these tokens, so an
    // unrecognized value matches neither group and would silently report "zero
    // unused" — ValidateUnusedCodeKindFilter rejects it up front instead.
    private static readonly string[] UnusedCodeKindFilters =
        { "Class", "Interface", "Struct", "Enum", "Delegate", "Type", "Method", "Property", "Field", "Member" };

    private object? ValidateUnusedCodeKindFilter(string? symbolKindFilter)
    {
        if (string.IsNullOrEmpty(symbolKindFilter)) return null;
        if (UnusedCodeKindFilters.Any(k => k.Equals(symbolKindFilter, StringComparison.OrdinalIgnoreCase)))
            return null;
        return CreateErrorResponse(
            ErrorCodes.InvalidParameter,
            $"Unknown symbolKindFilter '{symbolKindFilter}'",
            hint: $"Use one of: {string.Join(", ", UnusedCodeKindFilters)}.",
            context: new { symbolKindFilter });
    }

    // Honors a specific symbolKindFilter at the symbol level. The group gates
    // (shouldAnalyzeTypes/shouldAnalyzeMembers) decide whether a loop runs at all;
    // this narrows within it. Null/empty matches everything; the group tokens
    // "Type"/"Member" match every kind in that group.
    private static bool MatchesKindFilter(ISymbol symbol, string? filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return symbol switch
        {
            INamedTypeSymbol type => filter.Equals("Type", StringComparison.OrdinalIgnoreCase)
                || type.TypeKind switch
                {
                    TypeKind.Class => filter.Equals("Class", StringComparison.OrdinalIgnoreCase),
                    TypeKind.Interface => filter.Equals("Interface", StringComparison.OrdinalIgnoreCase),
                    TypeKind.Struct => filter.Equals("Struct", StringComparison.OrdinalIgnoreCase),
                    TypeKind.Enum => filter.Equals("Enum", StringComparison.OrdinalIgnoreCase),
                    TypeKind.Delegate => filter.Equals("Delegate", StringComparison.OrdinalIgnoreCase),
                    _ => false
                },
            IMethodSymbol => filter.Equals("Member", StringComparison.OrdinalIgnoreCase)
                || filter.Equals("Method", StringComparison.OrdinalIgnoreCase),
            IPropertySymbol => filter.Equals("Member", StringComparison.OrdinalIgnoreCase)
                || filter.Equals("Property", StringComparison.OrdinalIgnoreCase),
            IFieldSymbol => filter.Equals("Member", StringComparison.OrdinalIgnoreCase)
                || filter.Equals("Field", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public Task<object> GetDependencyGraphAsync(string? format, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var projectGraph = new Dictionary<string, List<string>>();
        var allProjects = _solution!.Projects.ToList();

        // Build dependency graph
        foreach (var project in allProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependencies = project.ProjectReferences
                .Select(pr => _solution!.GetProject(pr.ProjectId)?.Name)
                .Where(name => name != null)
                .Cast<string>()
                .ToList();

            projectGraph[project.Name] = dependencies;
        }

        // Detect cycles
        var cycles = DetectCycles(projectGraph);

        // Generate output based on format
        if (format?.ToLower() == "mermaid")
        {
            var mermaid = GenerateMermaidGraph(projectGraph);
            return Task.FromResult(CreateSuccessResponse(
                data: new
                {
                    format = "mermaid",
                    graph = mermaid,
                    hasCycles = cycles.Count > 0,
                    cycles
                },
                suggestedNextTools: cycles.Count > 0
                    ? new[] { "Resolve circular dependencies before building" }
                    : new[] { "get_project_structure for detailed project info" },
                totalCount: allProjects.Count,
                returnedCount: allProjects.Count
            ));
        }

        // Default: return structured data
        return Task.FromResult(CreateSuccessResponse(
            data: new
            {
                dependencies = projectGraph,
                hasCycles = cycles.Count > 0,
                cycles
            },
            suggestedNextTools: cycles.Count > 0
                ? new[] { "Resolve circular dependencies before building" }
                : new[] { "get_project_structure for detailed project info" },
            totalCount: allProjects.Count,
            returnedCount: allProjects.Count
        ));
    }

    public async Task<object> GetMissingMembersAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        // Find the type declaration
        var typeDecl = pos.Token.Parent?.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAType,
                "No type declaration found at position",
                hint: "Place cursor on a class or struct declaration",
                context: new { filePath, line, column }
            );
        }

        var typeSymbol = pos.Model.GetDeclaredSymbol(typeDecl, cancellationToken) as INamedTypeSymbol;
        if (typeSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Could not resolve type symbol",
                context: new { filePath, line, column }
            );
        }

        var missingMembers = new List<object>();

        // Check interfaces
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility == Accessibility.Private)
                    continue;

                var implementation = typeSymbol.FindImplementationForInterfaceMember(member);
                if (implementation == null)
                {
                    missingMembers.Add(new
                    {
                        fromInterface = iface.ToDisplayString(),
                        memberName = member.Name,
                        kind = member.Kind.ToString(),
                        signature = GetMemberSignature(member),
                        returnType = GetMemberReturnType(member)
                    });
                }
            }
        }

        // Check abstract base class members
        var baseType = typeSymbol.BaseType;
        while (baseType != null)
        {
            if (baseType.IsAbstract)
            {
                foreach (var member in baseType.GetMembers())
                {
                    if (!member.IsAbstract) continue;

                    var isImplemented = typeSymbol.GetMembers(member.Name)
                        .Any(m => m.IsOverride && !m.IsAbstract);

                    if (!isImplemented)
                    {
                        missingMembers.Add(new
                        {
                            fromAbstractClass = baseType.ToDisplayString(),
                            memberName = member.Name,
                            kind = member.Kind.ToString(),
                            signature = GetMemberSignature(member),
                            returnType = GetMemberReturnType(member)
                        });
                    }
                }
            }
            baseType = baseType.BaseType;
        }

        return CreateSuccessResponse(
            data: new
            {
                typeName = typeSymbol.ToDisplayString(),
                isAbstract = typeSymbol.IsAbstract,
                interfaces = typeSymbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList(),
                missingMembers
            },
            suggestedNextTools: missingMembers.Count > 0
                ? new[] { "Use the signatures to implement the missing members" }
                : new[] { "All interface and abstract members are implemented" },
            totalCount: missingMembers.Count,
            returnedCount: missingMembers.Count
        );
    }

    /// <summary>
    /// Gets all ways to instantiate a type.
    /// </summary>
    public async Task<object> GetInstantiationOptionsAsync(
        string typeName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (type, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use fully qualified name like Namespace.ClassName",
                context: new { typeName }
            );
        }

        // Get constructors
        var constructors = type.Constructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public ||
                        c.DeclaredAccessibility == Accessibility.Protected)
            .Select(c => new
            {
                signature = c.ToDisplayString(),
                accessibility = c.DeclaredAccessibility.ToString(),
                parameters = c.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type.ToDisplayString(),
                    isOptional = p.IsOptional,
                    defaultValue = FormatDefaultValue(p)
                }).ToList(),
                isObsolete = c.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute")
            })
            .ToList();

        // Find static factory methods
        var factoryMethods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.IsStatic &&
                       m.DeclaredAccessibility == Accessibility.Public &&
                       SymbolEqualityComparer.Default.Equals(m.ReturnType, type))
            .Select(m => new
            {
                name = m.Name,
                signature = m.ToDisplayString(),
                parameters = m.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Type.ToDisplayString(),
                    isOptional = p.IsOptional,
                    defaultValue = FormatDefaultValue(p)
                }).ToList()
            })
            .ToList();

        // Find factory methods in other types that return this type. Re-resolve
        // `type` in each project's augmented compilation: FindTypeByNameAsync
        // returns the type from the BASE compilation, but allTypes (and their
        // return types) come from the AUGMENTED compilation. When a project has
        // source generators (implicit .NET SDK generators count), base != augmented
        // and SymbolEqualityComparer.Default returns false for the "same" source
        // type across compilations — silently breaking external-factory detection.
        // Match the target type by its documentation-comment id — stable across the
        // base→augmented compilation boundary AND across constructed/definition forms.
        // The old GetTypeByMetadataName fed a display string (with angle brackets) to an
        // API that needs `+`-nested, backtick-arity metadata names, so generic/nested
        // receiver types resolved to null and were skipped entirely; and comparing a
        // factory's (possibly constructed) return type to the unbound definition by
        // symbol equality missed generic factories. OriginalDefinition + doc-id fixes both.
        var targetTypeId = type.OriginalDefinition.GetDocumentationCommentId();
        var externalFactories = new List<object>();
        foreach (var project in _solution!.Projects)
        {
            if (targetTypeId == null) break;
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            var allTypes = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>();
            foreach (var t in allTypes)
            {
                if (t.OriginalDefinition.GetDocumentationCommentId() == targetTypeId) continue; // the type itself

                var factories = t.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Where(m => m.IsStatic &&
                               m.DeclaredAccessibility == Accessibility.Public &&
                               (m.Name.StartsWith("Create") || m.Name.StartsWith("Build") || m.Name.StartsWith("New")) &&
                               m.ReturnType.OriginalDefinition.GetDocumentationCommentId() == targetTypeId)
                    .Take(5); // Limit to avoid too many

                foreach (var f in factories)
                {
                    externalFactories.Add(new
                    {
                        containingType = t.ToDisplayString(),
                        name = f.Name,
                        signature = f.ToDisplayString()
                    });
                }
            }

            if (externalFactories.Count >= 10) break; // Limit total
        }

        // Check for common patterns
        var implementsIDisposable = type.AllInterfaces.Any(i => i.Name == "IDisposable");
        // A real source type named {Type}Builder, not a filename substring.
        var builderTypeName = $"{type.Name}Builder";
        var hasBuilder = false;
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            if (compilation.GetSymbolsWithName(n => n == builderTypeName, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>().Any(t => t.Locations.Any(l => l.IsInSource)))
            {
                hasBuilder = true;
                break;
            }
        }

        string? hint = null;
        if (implementsIDisposable)
            hint = "Type implements IDisposable - consider using 'using' statement";
        else if (type.IsAbstract)
            hint = "Type is abstract - cannot instantiate directly, use derived type";
        else if (type.TypeKind == TypeKind.Interface)
            hint = "Type is an interface - cannot instantiate directly";

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                typeKind = GetTypeKindString(type),
                isAbstract = type.IsAbstract,
                isStatic = type.IsStatic,
                constructors,
                factoryMethods,
                externalFactories,
                implementsIDisposable,
                hasBuilder,
                hint
            },
            suggestedNextTools: new[]
            {
                "get_method_signature for constructor parameter details",
                "get_type_members to see what methods are available after creation"
            },
            totalCount: constructors.Count + factoryMethods.Count + externalFactories.Count,
            returnedCount: constructors.Count + factoryMethods.Count + externalFactories.Count
        );
    }

    /// <summary>
    /// Analyzes impact of changing a symbol.
    /// </summary>
    public async Task<object> AnalyzeChangeImpactAsync(
        string filePath,
        int line,
        int column,
        string changeType,
        string? newValue = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        // Find all references
        var references = await SymbolFinder.FindReferencesAsync(symbol, _solution!, cancellationToken);
        var allLocations = references.SelectMany(r => r.Locations).ToList();

        var impactedLocations = new List<object>();
        var breakingChanges = 0;
        var warnings = 0;

        foreach (var location in allLocations)
        {
            if (!location.Location.IsInSource) continue;

            var lineSpan = location.Location.GetLineSpan();

            string impact;
            string severity;

            switch (changeType.ToLower())
            {
                case "rename":
                    impact = "Reference will need to be updated";
                    severity = "info";
                    break;

                case "changetype":
                case "change_type":
                    impact = $"Usage may be incompatible with new type";
                    severity = "warning";
                    warnings++;
                    break;

                case "addparameter":
                case "add_parameter":
                    // A new parameter WITH a default value (newValue carries `=`) is
                    // source-compatible: existing call sites compile unchanged. Only
                    // a defaultless addition breaks them. This is the one real per-site
                    // feasibility signal here; the rest are change-type heuristics.
                    if (newValue != null && newValue.Contains('='))
                    {
                        impact = "New parameter has a default value — existing call sites remain valid";
                        severity = "info";
                    }
                    else
                    {
                        impact = "Call site missing new parameter";
                        severity = "error";
                        breakingChanges++;
                    }
                    break;

                case "removeparameter":
                case "remove_parameter":
                    impact = "Call site has extra parameter";
                    severity = "error";
                    breakingChanges++;
                    break;

                case "changeaccessibility":
                case "change_accessibility":
                    if (symbol.DeclaredAccessibility == Accessibility.Public)
                    {
                        impact = "External usages may lose access";
                        severity = "error";
                        breakingChanges++;
                    }
                    else
                    {
                        impact = "Accessibility change may affect visibility";
                        severity = "warning";
                        warnings++;
                    }
                    break;

                case "delete":
                    impact = "Reference will be broken";
                    severity = "error";
                    breakingChanges++;
                    break;

                default:
                    impact = "Unknown impact";
                    severity = "info";
                    break;
            }

            impactedLocations.Add(new
            {
                filePath = FormatPath(lineSpan.Path),
                line = lineSpan.StartLinePosition.Line,
                column = lineSpan.StartLinePosition.Character,
                impact,
                severity
            });
        }

        return CreateSuccessResponse(
            data: new
            {
                symbol = symbol.ToDisplayString(),
                symbolKind = symbol.Kind.ToString(),
                changeType,
                newValue,
                totalReferences = allLocations.Count,
                breakingChanges,
                warnings,
                safe = breakingChanges == 0,
                // Honesty about scope: except for the addParameter-with-default case,
                // each reference's severity is a heuristic from the change type and the
                // symbol's accessibility — not a per-call-site compile-feasibility check.
                // Treat this as a categorized reference inventory, not a proof of safety.
                impactKind = "heuristic-by-change-type",
                note = "Severities are derived from changeType + accessibility, not a per-call-site feasibility check (except addParameter, which honors a default value in newValue). Validate with get_diagnostics after applying.",
                impactedLocations
            },
            suggestedNextTools: breakingChanges > 0
                ? new[] { "Fix breaking changes before applying", "Use rename_symbol for safe renames" }
                : new[] { "Safe to proceed with change" },
            totalCount: allLocations.Count,
            returnedCount: impactedLocations.Count
        );
    }

    // Helpers for member signatures used by GetMissingMembersAsync and friends.
    private string GetMemberSignature(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => $"{method.ReturnType.ToDisplayString()} {method.Name}({string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))})",
            IPropertySymbol prop => $"{prop.Type.ToDisplayString()} {prop.Name} {{ {(prop.GetMethod != null ? "get; " : "")}{(prop.SetMethod != null ? "set; " : "")}}}",
            IEventSymbol evt => $"event {evt.Type.ToDisplayString()} {evt.Name}",
            _ => member.ToDisplayString()
        };
    }

    private string? GetMemberReturnType(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol method => method.ReturnType.ToDisplayString(),
            IPropertySymbol prop => prop.Type.ToDisplayString(),
            IEventSymbol evt => evt.Type.ToDisplayString(),
            _ => null
        };
    }

    /// <summary>
    /// Gets the actual source code of a method by name.
    /// </summary>
    public async Task<object> GetMethodSourceAsync(
        string typeName, string methodName, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (type, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName }
            );
        }

        // Find all methods with matching name
        var methods = type.GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary)
            .ToList();

        if (methods.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found in type '{typeName}'",
                hint: "Use get_type_members to see available methods",
                context: new { typeName, methodName }
            );
        }

        // Select specific overload or default to first
        var index = overloadIndex ?? 0;
        if (index < 0 || index >= methods.Count)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid overloadIndex {index}. Available: 0 to {methods.Count - 1}",
                context: new { overloadIndex, availableOverloads = methods.Count }
            );
        }

        var method = methods[index];
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);

        if (location == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Method is defined in metadata (external library), source not available",
                hint: "This method is from a compiled assembly, not source code",
                context: new { typeName, methodName, isFromMetadata = true }
            );
        }

        // Get the syntax node
        var syntaxTree = location.SourceTree;
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree for method",
                context: new { typeName, methodName }
            );
        }

        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var node = root.FindNode(location.SourceSpan);

        // Find the method declaration
        var methodDecl = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        if (methodDecl == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not find method declaration syntax",
                context: new { typeName, methodName }
            );
        }

        // Span the whole method declaration (not the symbol's identifier-only
        // location) so the reported start/end and lineCount describe the same
        // range, and lineCount excludes leading-trivia lines.
        var lineSpan = methodDecl.GetLocation().GetLineSpan();
        var sourceText = methodDecl.ToFullString();

        // Also get just the body if available
        string? bodySource = null;
        if (methodDecl.Body != null)
        {
            bodySource = methodDecl.Body.ToFullString();
        }
        else if (methodDecl.ExpressionBody != null)
        {
            bodySource = methodDecl.ExpressionBody.ToFullString();
        }

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                methodName = method.Name,
                signature = method.ToDisplayString(),
                overloadIndex = index,
                totalOverloads = methods.Count,
                location = new
                {
                    filePath = FormatPath(lineSpan.Path),
                    startLine = lineSpan.StartLinePosition.Line,
                    endLine = lineSpan.EndLinePosition.Line
                },
                fullSource = sourceText,
                bodySource,
                // The declaration's own line span — consistent with start/end and
                // free of the leading-trivia lines that ToFullString() carries.
                lineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1
            },
            suggestedNextTools: new[]
            {
                "get_outgoing_calls to see what this method calls",
                "find_callers to see who calls this method"
            }
        );
    }

    /// <summary>
    /// Gets source code for multiple methods in a single call (batch optimization).
    /// </summary>
    public async Task<object> GetMethodSourceBatchAsync(
        List<Dictionary<string, object>> methods,
        int maxMethods = 20,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (methods == null || methods.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "methods array is required and must not be empty",
                hint: "Provide an array like [{typeName: 'MyClass', methodName: 'MyMethod'}, ...]",
                context: new { parameter = "methods" }
            );
        }

        if (methods.Count > maxMethods)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Too many methods requested ({methods.Count}). Maximum is {maxMethods}",
                hint: $"Split request into batches of {maxMethods} or fewer"
            );
        }

        var results = new List<object>();
        var errors = new List<object>();

        foreach (var methodReq in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var typeName = methodReq.TryGetValue("typeName", out var tn) ? tn?.ToString() : null;
            var methodName = methodReq.TryGetValue("methodName", out var mn) ? mn?.ToString() : null;

            int? overloadIndex = null;
            if (methodReq.TryGetValue("overloadIndex", out var oi) && oi != null)
            {
                if (oi is int oiInt) overloadIndex = oiInt;
                else if (int.TryParse(oi.ToString(), out var parsed)) overloadIndex = parsed;
                else
                {
                    // Per-item error — a bad index on one entry must not abort the batch.
                    errors.Add(new
                    {
                        typeName,
                        methodName,
                        success = false,
                        error = $"overloadIndex must be an integer (got '{oi}')"
                    });
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
            {
                errors.Add(new
                {
                    typeName,
                    methodName,
                    success = false,
                    error = "typeName and methodName are required"
                });
                continue;
            }

            var result = await GetMethodSourceAsync(typeName, methodName, overloadIndex, cancellationToken);

            if (IsSuccessResponse(result))
            {
                results.Add(new
                {
                    typeName,
                    methodName,
                    success = true,
                    data = GetResponseData(result)
                });
            }
            else
            {
                errors.Add(new
                {
                    typeName,
                    methodName,
                    success = false,
                    error = GetResponseError(result)
                });
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                totalRequested = methods.Count,
                successCount = results.Count,
                errorCount = errors.Count,
                results,
                errors = errors.Count > 0 ? errors : null
            },
            suggestedNextTools: new[]
            {
                results.Count > 0 ? "analyze_method for deeper analysis of specific methods" : null,
                errors.Count > 0 ? "Check type/method names - some were not found" : null
            }.Where(s => s != null).ToArray()!,
            totalCount: methods.Count,
            returnedCount: results.Count
        );
    }
}
