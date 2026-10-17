using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// Read-only analysis/inspection tools.
public partial class RoslynService
{
    // The changeType values analyze_change_impact documents and accepts.
    private static readonly IReadOnlyList<string> ChangeTypes =
        new[] { "rename", "changeType", "addParameter", "removeParameter", "changeAccessibility", "delete" };

    // The overloads of the method at a position: the canonical overload set it belongs to
    // (GetOverloadSet, inherited overloads after the declared ones), each with the
    // overloadIndex every by-name tool uses for it. A constructor's set is the type's
    // constructors, an explicit interface implementation's the set its implemented
    // member's name denotes, a local function's itself alone.
    public async Task<object> GetMethodOverloadsAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        if (symbol is not IMethodSymbol methodSymbol || MethodKindLabel(methodSymbol) == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "Not a method symbol",
                hint: "This tool requires a method, constructor, operator or local function. Use get_method_signature with type and method names instead.",
                context: new { actualKind = symbol.Kind.ToString(), symbolName = symbol.Name }
            );
        }

        var set = GetOverloadSet(methodSymbol, includeInherited: true);
        var overloadList = set.Members.Select((member, index) =>
        {
            var m = (IMethodSymbol)member;
            return new
            {
                overloadIndex = index,
                signature = m.ToDisplayString(),
                kind = MethodKindLabel(m),
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
                isInherited = set.IsInherited(index),
                location = GetSymbolLocation(m)
            };
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                methodName = SymbolParts.Canonical(methodSymbol).Name,
                containingType = methodSymbol.ContainingType?.ToDisplayString(),
                overloads = overloadList
            },
            ResultCounts.Complete(overloadList.Count),
            suggestedNextTools: new[]
            {
                "find_references to see where overloads are called",
                "get_method_signature for detailed signature info"
            }
        );
    }

    // The kind label of a method the method tools address: its MemberKind (Method,
    // Constructor, StaticConstructor, Finalizer, Operator, Conversion) or LocalFunction;
    // null for an accessor or a lambda, which are not addressable methods.
    private static string? MethodKindLabel(IMethodSymbol method) =>
        method.MethodKind == MethodKind.LocalFunction
            ? "LocalFunction"
            : MemberClassifier.Classify(method)?.ToString();

    // The member declaration around a position — a method, constructor, finalizer,
    // operator, conversion, property, indexer, event (field-like or with accessors), field
    // (one variable of a multi-variable declaration), enum member or delegate — or, for a
    // position in a type declaration outside its members, the type. Accessors, local
    // functions and lambdas are inside their member; a C# 14 extension block is inside its
    // static class; a top-level statement is inside the program's entry point.
    public async Task<object> GetContainingMemberAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

        VariableDeclaratorSyntax? declarator = null;
        for (var node = pos.Token.Parent; node != null; node = node.Parent)
        {
            ISymbol? symbol = null;
            switch (node)
            {
                case VariableDeclaratorSyntax variable:
                    declarator = variable;
                    continue;
                case BaseFieldDeclarationSyntax field:
                    var variables = field.Declaration.Variables;
                    var named = declarator ?? (variables.Count == 1 ? variables[0] : null);
                    if (named == null)
                    {
                        return CreateErrorResponse(
                            ErrorCodes.AmbiguousMatch,
                            $"The position is in a declaration of {variables.Count} members ({string.Join(", ", variables.Select(v => v.Identifier.Text))})",
                            hint: "Place the position on the name or initializer of one of them.",
                            context: new { filePath, line, column, members = variables.Select(v => v.Identifier.Text).ToList() });
                    }
                    symbol = pos.Model.GetDeclaredSymbol(named, cancellationToken);
                    break;
                case EnumMemberDeclarationSyntax or BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax
                    or DelegateDeclarationSyntax or BaseTypeDeclarationSyntax:
                    symbol = pos.Model.GetDeclaredSymbol(node, cancellationToken);
                    if (symbol is INamedTypeSymbol { IsExtension: true }) continue;
                    break;
                case GlobalStatementSyntax:
                    symbol = pos.Model.GetEnclosingSymbol(pos.Position, cancellationToken);
                    while (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction })
                        symbol = symbol.ContainingSymbol;
                    break;
                default:
                    continue;
            }

            if (symbol == null) break;
            var lineSpan = pos.Tree.GetLineSpan(node.Span);
            return CreateSuccessResponse(
                data: new
                {
                    memberName = symbol.Name,
                    memberKind = SymbolKindVocabulary.Label(symbol) ?? symbol.Kind.ToString(),
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

        return CreateErrorResponse(
            ErrorCodes.SymbolNotFound,
            "No containing member found",
            hint: "The position is outside every type declaration (a using directive, a namespace declaration, or between types).",
            context: new { filePath, line, column }
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
        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);
        var data = await FindUnusedCodeDataAsyncCore(projectName, includePrivate, includeInternal, symbolKindFilter, maxResults ?? 50, namePredicate: null, cancellationToken);
        if (data.Error != null) return data.Error;

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
            ResultCounts.Capped(data.TotalCount, data.Symbols.Count),
            suggestedNextTools: data.Symbols.Count > 0
                ? new[] { "find_references to verify symbol is truly unused", "rename_symbol or delete unused code" }
                : new[] { "No unused code found - codebase is clean" }
        );
    }

    // Internal typed-data variant used by GetProjectHealthAsync and
    // RemoveUnusedCodeAsync. namePredicate narrows the candidate sweep to
    // matching symbol names BEFORE any reference counting — an allow-list
    // query checks a handful of names instead of the whole project. An unresolvable
    // projectName yields no symbols and the canonical project error in Error.
    internal Task<UnusedCodeData> FindUnusedCodeDataAsync(
        string? projectName,
        bool includePrivate,
        bool includeInternal,
        string? symbolKindFilter,
        int maxResults,
        Func<string, bool>? namePredicate = null,
        CancellationToken cancellationToken = default) => FindUnusedCodeDataAsyncCore(projectName, includePrivate, includeInternal, symbolKindFilter, maxResults, namePredicate, cancellationToken);

    // Unused code: source types and members of the scoped projects (ResolveProjectScope) with
    // no reference outside their own declaration — a member referenced only by itself (a
    // self-recursive method) is unused, and a type is unused when nothing outside its own
    // declarations references it or any of its members. Never reported: code something
    // outside the source calls (the compilation's entry point and its type, test methods by
    // each framework's discovery contract and the classes holding them, the lifecycle code
    // a test runner runs around them, framework-set members such as MSTest's TestContext
    // property, types a framework discovers by interface or attribute), members reached
    // through a contract (virtual, abstract, override, and implementations of an interface
    // member), and compiler-generated, extern and non-source symbols. A multi-targeted
    // project's symbols are checked once. The list is in canonical location order, capped
    // at maxResults; TotalCount counts every unused symbol.
    private async Task<UnusedCodeData> FindUnusedCodeDataAsyncCore(
        string? projectName,
        bool includePrivate,
        bool includeInternal,
        string? symbolKindFilter,
        int maxResults,
        Func<string, bool>? namePredicate = null,
        CancellationToken cancellationToken = default)
    {
        var (projects, scopeError) = ResolveProjectScope(projectName);
        if (projects == null) return new UnusedCodeData(0, Array.Empty<UnusedSymbolEntry>(), scopeError);

        var shouldAnalyzeTypes = string.IsNullOrEmpty(symbolKindFilter) ||
                                 symbolKindFilter.Equals("Class", StringComparison.OrdinalIgnoreCase) ||
                                 symbolKindFilter.Equals("Interface", StringComparison.OrdinalIgnoreCase) ||
                                 symbolKindFilter.Equals("Struct", StringComparison.OrdinalIgnoreCase) ||
                                 symbolKindFilter.Equals("Enum", StringComparison.OrdinalIgnoreCase) ||
                                 symbolKindFilter.Equals("Delegate", StringComparison.OrdinalIgnoreCase) ||
                                 symbolKindFilter.Equals("Type", StringComparison.OrdinalIgnoreCase);
        var shouldAnalyzeMembers = string.IsNullOrEmpty(symbolKindFilter) ||
                                   symbolKindFilter.Equals("Method", StringComparison.OrdinalIgnoreCase) ||
                                   symbolKindFilter.Equals("Property", StringComparison.OrdinalIgnoreCase) ||
                                   symbolKindFilter.Equals("Field", StringComparison.OrdinalIgnoreCase) ||
                                   symbolKindFilter.Equals("Member", StringComparison.OrdinalIgnoreCase);

        bool PassesAccessibility(ISymbol symbol) =>
            (includePrivate || symbol.DeclaredAccessibility != Accessibility.Private)
            && (includeInternal || symbol.DeclaredAccessibility != Accessibility.Internal);

        var candidates = new List<(ISymbol Symbol, string? TargetFramework)>();
        var runtimeUsed = new HashSet<ISymbol>(CrossCompilationSymbolComparer.Instance);
        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);
            runtimeUsed.UnionWith(RuntimeUsedSymbols(compilation, cancellationToken));

            if (shouldAnalyzeTypes)
            {
                foreach (var typeSymbol in compilation.GetSymbolsWithName(namePredicate ?? (_ => true), SymbolFilter.Type, cancellationToken)
                             .OfType<INamedTypeSymbol>())
                {
                    if (typeSymbol.IsImplicitlyDeclared || typeSymbol.IsExtern || !typeSymbol.Locations.Any(loc => loc.IsInSource)) continue;
                    if (!MatchesKindFilter(typeSymbol, symbolKindFilter) || !PassesAccessibility(typeSymbol)) continue;
                    // Types a framework discovers through an interface or attribute (DI,
                    // hosted services, controllers, test fixtures, serialization).
                    if (ImplementsFrameworkInterface(typeSymbol) || HasFrameworkAttribute(typeSymbol)) continue;
                    candidates.Add((typeSymbol, targetFramework));
                }
            }

            if (shouldAnalyzeMembers)
            {
                foreach (var member in compilation.GetSymbolsWithName(namePredicate ?? (_ => true), SymbolFilter.Member, cancellationToken))
                {
                    if (member is not (IMethodSymbol or IPropertySymbol or IFieldSymbol)) continue;
                    if (!MatchesKindFilter(member, symbolKindFilter)) continue;
                    if (member.IsImplicitlyDeclared || member.IsExtern || !member.Locations.Any(loc => loc.IsInSource)) continue;
                    // Members reached through a contract rather than a direct reference:
                    // special methods (constructors, operators, accessors, ...), virtual,
                    // abstract and override members, and interface implementations.
                    if (member is IMethodSymbol method &&
                        (method.MethodKind != MethodKind.Ordinary || method.IsOverride || method.IsVirtual || method.IsAbstract))
                        continue;
                    if (member is IPropertySymbol property && (property.IsOverride || property.IsVirtual || property.IsAbstract))
                        continue;
                    if (ImplementsInterfaceMember(member)) continue;
                    if (!PassesAccessibility(member)) continue;
                    candidates.Add((member, targetFramework));
                }
            }
        }

        var unused = new List<(ISymbol Symbol, Location Location)>();
        foreach (var candidate in TargetFrameworkDeduplication.BySymbol(candidates, c => c.Symbol, c => c.TargetFramework))
        {
            var symbol = candidate.Item.Symbol;
            if (runtimeUsed.Contains(symbol)) continue;
            var isUnused = symbol is INamedTypeSymbol type
                ? await IsTypeUnusedAsync(type, cancellationToken)
                : !await IsReferencedOutsideAsync(symbol, symbol.DeclaringSyntaxReferences, cancellationToken);
            if (!isUnused) continue;
            if (SymbolDisplayLocation(symbol) is { } location) unused.Add((symbol, location));
        }

        var ordered = CanonicalOrder.ByLocation(unused, u => ReportedLocation(u.Location));
        var entries = ordered.Take(maxResults).Select(u =>
        {
            var lineSpan = u.Location.GetLineSpan();
            return new UnusedSymbolEntry(
                Name: u.Symbol.Name,
                FullName: u.Symbol.ToDisplayString(),
                Kind: u.Symbol is INamedTypeSymbol t ? GetTypeKindString(t) : u.Symbol.Kind.ToString(),
                Accessibility: u.Symbol.DeclaredAccessibility.ToString(),
                ContainingType: u.Symbol.ContainingType?.ToDisplayString(),
                FilePath: FormatPath(lineSpan.Path),
                Line: lineSpan.StartLinePosition.Line,
                Column: lineSpan.StartLinePosition.Character,
                Symbol: u.Symbol);
        }).ToList();

        return new UnusedCodeData(TotalCount: unused.Count, Symbols: entries);
    }

    // The symbols of a compilation something outside the source calls: the entry point and
    // its type; every test entry point (TestFrameworkContracts) and class lifecycle method,
    // the classes declaring them, and the lifecycle roots the runner runs around their tests
    // (TestLifecycleResolver: constructors, fixtures, IAsyncLifetime/IDisposable
    // implementations) with their types; and MSTest's TestContext property, which the
    // framework sets.
    private static IEnumerable<ISymbol> RuntimeUsedSymbols(Compilation compilation, CancellationToken cancellationToken)
    {
        var used = new List<ISymbol>();
        if (compilation.GetEntryPoint(cancellationToken) is { } entryPoint)
        {
            used.Add(entryPoint);
            used.Add(entryPoint.ContainingType);
        }

        var sourceTypes = GetAllNamedTypes(compilation)
            .Where(t => SymbolEqualityComparer.Default.Equals(t.ContainingAssembly, compilation.Assembly)
                        && t.Locations.Any(l => l.IsInSource))
            .ToList();
        var lifecycle = new TestLifecycleResolver(sourceTypes);
        foreach (var type in sourceTypes)
        {
            var testMethods = type.GetMembers().OfType<IMethodSymbol>()
                .Where(m => TestFrameworkContracts.ClassifyEntryPoint(m) != null || TestFrameworkContracts.IsClassLifecycleMethod(m))
                .ToList();
            if (testMethods.Count > 0)
            {
                used.Add(type);
                used.AddRange(testMethods);
                foreach (var root in lifecycle.RootsForTestsOf(type))
                {
                    used.Add(root);
                    used.Add(root.ContainingType);
                }
            }
            used.AddRange(type.GetMembers().OfType<IPropertySymbol>()
                .Where(p => TestFrameworkContracts.Is(p.Type, "Microsoft.VisualStudio.TestTools.UnitTesting", "TestContext")));
        }
        return used;
    }

    // Whether a member implements an interface member of its type (explicitly or
    // implicitly): the interface contract reaches it, so it is required even with no
    // direct reference.
    private static bool ImplementsInterfaceMember(ISymbol member)
    {
        if (member.ContainingType is not { } type) return false;
        return type.AllInterfaces.Any(iface => iface.GetMembers().Any(interfaceMember =>
            type.FindImplementationForInterfaceMember(interfaceMember) is { } implementation
            && SymbolEqualityComparer.Default.Equals(implementation, member)));
    }

    // A type is unused when nothing outside its own declarations references it or any of
    // its members (constructors aside — `new T()` references the type itself).
    private async Task<bool> IsTypeUnusedAsync(INamedTypeSymbol type, CancellationToken cancellationToken)
    {
        var declarations = type.DeclaringSyntaxReferences;
        if (await IsReferencedOutsideAsync(type, declarations, cancellationToken)) return false;
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared ||
                member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
                continue;
            if (await IsReferencedOutsideAsync(member, declarations, cancellationToken)) return false;
        }
        return true;
    }

    // Whether any reference to the symbol lies outside the given declarations. A reference
    // inside them (a recursive call, a type naming itself) does not make the symbol used.
    // Declarations are compared by file path and span, so the copy of a file another target
    // framework compiles counts as the same declaration.
    private async Task<bool> IsReferencedOutsideAsync(
        ISymbol symbol, IReadOnlyList<SyntaxReference> declarations, CancellationToken cancellationToken)
    {
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, _solution!, cancellationToken))
        {
            foreach (var location in reference.Locations)
            {
                var span = location.Location.SourceSpan;
                var path = location.Location.SourceTree?.FilePath;
                var inside = declarations.Any(d =>
                    string.Equals(d.SyntaxTree.FilePath, path, PathComparison) && d.Span.Contains(span));
                if (!inside) return true;
            }
        }
        return false;
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
    // "Type"/"Member" match every kind in that group. A record is a class and a record
    // struct a struct.
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

    // The project dependency graph (BuildProjectGraph) as nodes and edges, or as Mermaid
    // flowchart text, with its cycles.
    public Task<object> GetDependencyGraphAsync(string? format, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Choice("format", format, new[] { "json", "mermaid" }, out var canonicalFormat) is { } invalidFormat)
            return Task.FromResult(CreateErrorResponse(invalidFormat));
        cancellationToken.ThrowIfCancellationRequested();

        var graph = BuildProjectGraph();
        var cycles = DetectCycles(graph.ToAdjacency());
        var suggestedNextTools = cycles.Cycles.Count > 0
            ? new[] { "Resolve circular dependencies before building" }
            : new[] { "get_project_structure for detailed project info" };

        if (canonicalFormat == "mermaid")
        {
            return Task.FromResult(CreateSuccessResponse(
                data: new
                {
                    format = "mermaid",
                    graph = RenderMermaid(graph),
                    hasCycles = cycles.Cycles.Count > 0,
                    cycles = cycles.Cycles,
                    cyclesTruncated = cycles.Truncated,
                    stronglyConnectedComponents = cycles.StronglyConnectedComponents
                },
                ResultCounts.Complete(graph.Nodes.Count),
                suggestedNextTools: suggestedNextTools));
        }

        return Task.FromResult(CreateSuccessResponse(
            data: new
            {
                nodes = FormatGraphNodes(graph),
                edges = FormatGraphEdges(graph),
                hasCycles = cycles.Cycles.Count > 0,
                cycles = cycles.Cycles,
                cyclesTruncated = cycles.Truncated,
                stronglyConnectedComponents = cycles.StronglyConnectedComponents
            },
            ResultCounts.Complete(graph.Nodes.Count),
            suggestedNextTools: suggestedNextTools));
    }

    // The members a type must still provide, exactly the compiler's requirements: one
    // entry per interface member without an implementation (CS0535) — abstract members,
    // static abstract ones included, never a member with a default body, a static member
    // with a body, or a nested type — and per abstract base member no class between the
    // type and its declaring base overrides (CS0534). A property or event is one entry,
    // never its accessors.
    public async Task<object> GetMissingMembersAsync(
        string filePath, int line, int column, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (pos, posError) = await ResolvePositionAsync(filePath, line, column, cancellationToken);
        if (pos == null) return posError!;

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

        if (pos.Model.GetDeclaredSymbol(typeDecl, cancellationToken) is not INamedTypeSymbol typeSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                "Could not resolve type symbol",
                context: new { filePath, line, column }
            );
        }

        var missingMembers = new List<object>();
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            foreach (var member in iface.GetMembers())
            {
                if (!member.IsAbstract || member is INamedTypeSymbol || MemberClassifier.Classify(member) is not { } kind) continue;
                if (typeSymbol.FindImplementationForInterfaceMember(member) != null) continue;
                missingMembers.Add(new
                {
                    fromInterface = iface.ToDisplayString(),
                    memberName = member.Name,
                    kind = kind.ToString(),
                    isStatic = member.IsStatic,
                    signature = GetMemberSignature(member),
                    returnType = GetMemberReturnType(member)
                });
            }
        }

        // Whether an inherited abstract member is satisfied is resolved by walking the base
        // chain (IsAbstractSlotSatisfied) — an override in an intermediate abstract base
        // counts. Each abstract slot is reported once, attributed to the base that
        // originally declared it.
        var reportedSlots = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        for (var baseType = typeSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (!baseType.IsAbstract) continue;

            foreach (var member in baseType.GetMembers())
            {
                if (!member.IsAbstract || member is INamedTypeSymbol || MemberClassifier.Classify(member) == null) continue;

                var slotRoot = GetOverrideSlotRoot(member);
                if (!reportedSlots.Add(slotRoot.OriginalDefinition)) continue;
                if (IsAbstractSlotSatisfied(typeSymbol, slotRoot)) continue;

                missingMembers.Add(new
                {
                    fromAbstractClass = slotRoot.ContainingType.ToDisplayString(),
                    memberName = slotRoot.Name,
                    kind = MemberClassifier.Classify(slotRoot)!.Value.ToString(),
                    isStatic = slotRoot.IsStatic,
                    signature = GetMemberSignature(slotRoot),
                    returnType = GetMemberReturnType(slotRoot)
                });
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                typeName = typeSymbol.ToDisplayString(),
                isAbstract = typeSymbol.IsAbstract,
                interfaces = typeSymbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList(),
                missingMembers
            },
            ResultCounts.Complete(missingMembers.Count),
            suggestedNextTools: missingMembers.Count > 0
                ? new[] { "Use the signatures to implement the missing members" }
                : new[] { "All interface and abstract members are implemented" }
        );
    }

    /// <summary>
    /// Gets all ways to instantiate a type.
    /// </summary>
    public async Task<object> GetInstantiationOptionsAsync(
        string typeName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var resolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        if (resolution.Error != null) return resolution.Error;
        if (resolution.Type is not { } type)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use fully qualified name like Namespace.ClassName",
                context: new { typeName }
            );
        }

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

        var definition = type.OriginalDefinition;
        var factoryMethods = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.IsStatic &&
                        m.DeclaredAccessibility == Accessibility.Public &&
                        SymbolEqualityComparer.Default.Equals(m.ReturnType.OriginalDefinition, definition))
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

        // Static Create*/Build*/New* methods of other source types that return this type
        // (any construction of it), each once however many target frameworks compile it,
        // in the canonical order. The target matches across compilations by the solution's
        // identity rule: a generator-augmented or retargeted compilation binds the same
        // declaration to a different instance.
        var candidates = new List<(IMethodSymbol Factory, string? TargetFramework)>();
        foreach (var project in GetLogicalProjects().Projects.SelectMany(p => p.Projects))
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);

            foreach (var t in compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type, cancellationToken).OfType<INamedTypeSymbol>())
            {
                if (CrossCompilationSymbolComparer.Instance.Equals(t.OriginalDefinition, definition)) continue;
                foreach (var factory in t.GetMembers().OfType<IMethodSymbol>())
                {
                    if (factory.IsStatic &&
                        factory.DeclaredAccessibility == Accessibility.Public &&
                        (factory.Name.StartsWith("Create") || factory.Name.StartsWith("Build") || factory.Name.StartsWith("New")) &&
                        CrossCompilationSymbolComparer.Instance.Equals(factory.ReturnType.OriginalDefinition, definition))
                    {
                        candidates.Add((factory, targetFramework));
                    }
                }
            }
        }
        var externalFactories = CanonicalOrder.BySymbol(
                TargetFrameworkDeduplication.BySymbol(candidates, c => c.Factory, c => c.TargetFramework),
                m => m.Item.Factory)
            .Select(m => new
            {
                containingType = m.Item.Factory.ContainingType.ToDisplayString(),
                name = m.Item.Factory.Name,
                signature = m.Item.Factory.ToDisplayString()
            })
            .ToList();

        var implementsIDisposable = type.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_IDisposable);
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

        // What prevents or shapes instantiation, most decisive first: an interface (which
        // the symbol model also marks abstract), a static class, an abstract class, then
        // IDisposable.
        string? hint = null;
        if (type.TypeKind == TypeKind.Interface)
            hint = "Type is an interface - cannot instantiate directly";
        else if (type.IsStatic)
            hint = "Type is static - it cannot be instantiated";
        else if (type.IsAbstract)
            hint = "Type is abstract - cannot instantiate directly, use derived type";
        else if (implementsIDisposable)
            hint = "Type implements IDisposable - consider using 'using' statement";

        var total = constructors.Count + factoryMethods.Count + externalFactories.Count;
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
                hint,
                notice = resolution.ShadowNotice
            },
            ResultCounts.Complete(total),
            suggestedNextTools: new[]
            {
                "get_method_signature for constructor parameter details",
                "get_type_members to see what methods are available after creation"
            }
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

        if (ParameterValidation.Choice("changeType", changeType, ChangeTypes, out var canonicalChangeType) is { } invalidChange)
            return CreateErrorResponse(invalidChange);

        var (symbol, _, error) = await ResolveSymbolAtAsync(filePath, line, column, cancellationToken);
        if (symbol == null) return error!;

        // The same reference set find_references reports: each site once.
        var sites = await FindReferenceSitesAsync(symbol, cancellationToken);

        var impactedLocations = new List<object>();
        var breakingChanges = 0;
        var warnings = 0;

        foreach (var site in sites.Select(s => s.Item))
        {
            string impact;
            string severity;

            switch (canonicalChangeType)
            {
                case "rename":
                    impact = "Reference will need to be updated";
                    severity = "info";
                    break;

                case "changeType":
                    impact = "Usage may be incompatible with new type";
                    severity = "warning";
                    warnings++;
                    break;

                case "addParameter":
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

                case "removeParameter":
                    impact = "Call site has extra parameter";
                    severity = "error";
                    breakingChanges++;
                    break;

                case "changeAccessibility":
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

                default:
                    impact = "Reference will be broken";
                    severity = "error";
                    breakingChanges++;
                    break;
            }

            impactedLocations.Add(new
            {
                filePath = site.FilePath,
                line = site.Line,
                column = site.Column,
                impact,
                severity
            });
        }

        return CreateSuccessResponse(
            data: new
            {
                symbol = symbol.ToDisplayString(),
                symbolKind = symbol.Kind.ToString(),
                changeType = canonicalChangeType,
                newValue,
                totalReferences = sites.Count,
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
            ResultCounts.Complete(sites.Count),
            suggestedNextTools: breakingChanges > 0
                ? new[] { "Fix breaking changes before applying", "Use rename_symbol for safe renames" }
                : new[] { "Safe to proceed with change" }
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

    // Walks a member's override chain to its original declaration — the "slot" an
    // abstract requirement occupies. An `abstract override` re-abstracts an inherited
    // slot; both levels resolve to the same root, so a slot is reported once.
    private static ISymbol GetOverrideSlotRoot(ISymbol member)
    {
        var current = member;
        while (OverriddenMemberOf(current) is { } overridden)
            current = overridden;
        return current;
    }

    private static ISymbol? OverriddenMemberOf(ISymbol member) => member switch
    {
        IMethodSymbol m => m.OverriddenMethod,
        IPropertySymbol p => p.OverriddenProperty,
        IEventSymbol e => e.OverriddenEvent,
        _ => null
    };

    // Whether the abstract requirement at slotRoot is satisfied by a concrete override
    // between `type` and the declaring base. GetMembers on `type` alone misses overrides
    // in intermediate bases, so walk the chain; the most-derived member occupying the
    // slot decides — satisfied iff it is not itself abstract (an `abstract override`
    // re-abstracts and leaves it unsatisfied).
    private static bool IsAbstractSlotSatisfied(INamedTypeSymbol type, ISymbol slotRoot)
    {
        var declaringType = slotRoot.ContainingType.OriginalDefinition;
        for (var current = type;
             current != null && !SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, declaringType);
             current = current.BaseType)
        {
            foreach (var candidate in current.GetMembers(slotRoot.Name))
            {
                if (!candidate.IsOverride) continue;
                if (SymbolEqualityComparer.Default.Equals(
                        GetOverrideSlotRoot(candidate).OriginalDefinition, slotRoot.OriginalDefinition))
                    return !candidate.IsAbstract;
            }
        }
        return false;
    }

    // The method a by-name method tool (get_method_source, get_method_signature) addresses:
    // typeName resolved (the TypeResolution carries the shadowing notice), methodName looked
    // up in the canonical overload set with inherited overloads after the declared ones
    // (GetMembersByName — constructors by ".ctor" or the type's simple name, an explicit
    // interface implementation by its implemented member's name), and overloadIndex
    // (default 0) selecting within it. A name that denotes a non-method is NOT_A_METHOD.
    private async Task<(IMethodSymbol? Method, OverloadSet? Set, int Index, TypeResolution? Type, object? Error)> ResolveMethodByNameAsync(
        string typeName, string methodName, int? overloadIndex, CancellationToken cancellationToken)
    {
        var resolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        if (resolution.Error != null) return (null, null, 0, resolution, resolution.Error);
        if (resolution.Type is not { } type)
        {
            return (null, null, 0, resolution, CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName }));
        }

        var set = GetMembersByName(type, methodName, includeInherited: true);
        if (set.Count == 0)
        {
            return (null, null, 0, resolution, CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found in type '{type.ToDisplayString()}'",
                hint: resolution.ShadowNotice ?? "Use get_type_members to see available methods",
                context: new { typeName, methodName, shadowedTypes = resolution.ShadowedMatches }));
        }

        var index = overloadIndex ?? 0;
        if (index < 0 || index >= set.Count)
        {
            return (null, null, 0, resolution, CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"Invalid overloadIndex {index}. Available: 0 to {set.Count - 1}",
                hint: resolution.ShadowNotice,
                context: new { overloadIndex, availableOverloads = set.Count }));
        }

        if (set.Members[index] is not IMethodSymbol method || MethodKindLabel(method) == null)
        {
            var member = set.Members[index];
            return (null, null, 0, resolution, CreateErrorResponse(
                ErrorCodes.NotAMethod,
                $"'{member.ToDisplayString()}' is a {SymbolKindVocabulary.Label(member) ?? member.Kind.ToString()}, not a method",
                hint: "Use get_type_members or get_symbol_info for non-method members.",
                context: new { typeName, methodName, overloadIndex = index, actualKind = SymbolKindVocabulary.Label(member) ?? member.Kind.ToString() }));
        }

        return (method, set, index, resolution, null);
    }

    // The source code of a method by name: the source of the part that carries the body (a partial method's implementation);
    // location is its name, span the whole declaration. A primary constructor's source is
    // its type's declaration header through the parameter list.
    public async Task<object> GetMethodSourceAsync(
        string typeName, string methodName, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (method, set, index, resolution, error) = await ResolveMethodByNameAsync(typeName, methodName, overloadIndex, cancellationToken);
        if (method == null) return error!;

        var location = SymbolDisplayLocation(method);
        if (location?.SourceTree is not { } syntaxTree)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Method is defined in metadata (external library), source not available",
                hint: "This method is from a compiled assembly, not source code",
                context: new { typeName, methodName, isFromMetadata = true });
        }

        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var declaration = root.FindNode(location.SourceSpan).AncestorsAndSelf()
            .FirstOrDefault(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or TypeDeclarationSyntax);
        if (declaration == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not find the method's declaration syntax",
                context: new { typeName, methodName });
        }

        // A method located at its type's declaration is declared by the type: a primary
        // constructor by the type's parameter list, or an implicitly declared member (a
        // default or copy constructor) with no source of its own. A C# 14 extension member's
        // implementation, though implicitly declared, is located at the member it implements.
        if (declaration is TypeDeclarationSyntax typeDeclaration
            && (method.IsImplicitlyDeclared || typeDeclaration.ParameterList == null))
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"'{method.ToDisplayString()}' is declared implicitly and has no source",
                hint: "Only methods declared in source have source code.",
                context: new { typeName, methodName, isFromMetadata = false });
        }

        // A method's declaration spans the whole member; a primary constructor (declared by
        // its type's parameter list) spans the type's header through that list.
        var span = declaration is TypeDeclarationSyntax { ParameterList: { } parameterList } primaryOwner
            ? TextSpan.FromBounds(primaryOwner.SpanStart, parameterList.Span.End)
            : declaration.Span;
        var text = await syntaxTree.GetTextAsync(cancellationToken);
        var fullSource = declaration is TypeDeclarationSyntax
            ? text.ToString(span)
            : declaration.ToFullString();
        var bodySource = declaration switch
        {
            BaseMethodDeclarationSyntax m => (m.Body ?? (SyntaxNode?)m.ExpressionBody)?.ToFullString(),
            LocalFunctionStatementSyntax f => (f.Body ?? (SyntaxNode?)f.ExpressionBody)?.ToFullString(),
            _ => null
        };
        var lineSpan = syntaxTree.GetLineSpan(span);

        return CreateSuccessResponse(
            data: new
            {
                typeName = resolution!.Type!.ToDisplayString(),
                methodName = method.Name,
                signature = method.ToDisplayString(),
                overloadIndex = index,
                totalOverloads = set!.Count,
                isInherited = set.IsInherited(index),
                location = FormatLocation(location),
                span = new
                {
                    startLine = lineSpan.StartLinePosition.Line,
                    startColumn = lineSpan.StartLinePosition.Character,
                    endLine = lineSpan.EndLinePosition.Line,
                    endColumn = lineSpan.EndLinePosition.Character
                },
                fullSource,
                bodySource,
                // The declaration's own line span — consistent with span and free of the
                // leading-trivia lines that fullSource carries.
                lineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1,
                notice = resolution.ShadowNotice
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

        if (ParameterValidation.Limit("maxMethods", maxMethods, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);

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

        // Every requested method is answered — resolved or with its error — so nothing is
        // cut off: totalCount is the request size, returnedCount the resolved ones.
        return CreateSuccessResponse(
            data: new
            {
                totalRequested = methods.Count,
                successCount = results.Count,
                errorCount = errors.Count,
                results,
                errors = errors.Count > 0 ? errors : null
            },
            ResultCounts.Explicit(methods.Count, results.Count, truncated: false),
            suggestedNextTools: new[]
            {
                results.Count > 0 ? "analyze_method for deeper analysis of specific methods" : null,
                errors.Count > 0 ? "Check type/method names - some were not found" : null
            }.Where(s => s != null).ToArray()!
        );
    }
}
