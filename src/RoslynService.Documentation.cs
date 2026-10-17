using Microsoft.CodeAnalysis;

namespace SharpLensMcp;

// get_documentation: a symbol's full XML documentation with <inheritdoc>
// resolved recursively — every other tool returns the tag verbatim.
// A member-level <inheritdoc> (bare or cref) is expanded element-by-element:
// inherited content fills sections the local doc omits. A cref points at the
// referenced symbol; a bare tag points at the overridden member, the
// explicitly implemented interface member, or the implicitly satisfied
// interface member; for a constructor, the base class constructor with the same
// parameter types; for a type, its base type or first interface. Inherited
// <param>/<typeparam> entries document the parameters at the same positions, so
// they are reported under the inheriting symbol's own parameter names.
public partial class RoslynService
{
    public async Task<object> GetDocumentationAsync(
        string typeName, string? memberName = null, int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var resolution = await ResolveMemberByNameAsync(typeName, memberName, overloadIndex, cancellationToken);
        if (resolution.Symbol is not { } symbol) return resolution.Error!;

        var compilation = await FindCompilationForSymbolAsync(symbol, cancellationToken);
        if (compilation == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not obtain a compilation to resolve documentation",
                context: new { typeName, memberName });
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var (docs, inheritedFrom, unresolved) = await ResolveStructuredDocsAsync(
            symbol, compilation, visited, cancellationToken);

        return CreateSuccessResponse(
            data: new
            {
                symbol = symbol.ToDisplayString(),
                documentation = StructuredDocsToWire(docs),
                inheritedFrom,
                unresolved,
                notice = resolution.Type?.ShadowNotice
            },
            suggestedNextTools: new[]
            {
                "get_method_source to read the implementation behind these docs",
                "get_type_members to list the rest of the type's surface"
            });
    }

    // A symbol's effective documentation with member-level <inheritdoc>
    // resolved and merged. The cycle guard keys on documentation ids, so a
    // self- or mutually-referential inheritdoc terminates with unresolved:"cycle".
    internal async Task<(StructuredDocs docs, string? inheritedFrom, string? unresolved)>
        ResolveStructuredDocsAsync(
            ISymbol symbol, Compilation compilation,
            HashSet<string> visited, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        var (local, hasInheritDoc, inheritCref) = XmlDocParser.Parse(xml);
        if (!hasInheritDoc) return (local, null, null);

        var key = DocumentationCommentId.CreateDeclarationId(symbol) ?? symbol.ToDisplayString();
        if (!visited.Add(key)) return (local, null, "cycle");

        var target = inheritCref != null
            ? DocumentationCommentId.GetFirstSymbolForReferenceId(inheritCref, compilation)
              ?? DocumentationCommentId.GetFirstSymbolForDeclarationId(inheritCref, compilation)
            : FindInheritedDocSource(symbol);

        if (target == null)
        {
            return (local, null, inheritCref != null ? "unresolved-cref" : "no-inherited-member");
        }

        var (inherited, deeperInheritedFrom, deeperUnresolved) = await ResolveStructuredDocsAsync(
            target, compilation, visited, cancellationToken);
        // Report the symbol the docs ultimately came from: if the immediate target
        // itself inherited, follow through to that origin rather than stopping at the
        // first hop.
        var origin = deeperInheritedFrom ?? target.ToDisplayString();
        return (local.MergeInherited(RenameInheritedEntries(inherited, target, symbol)), origin, deeperUnresolved);
    }

    // The inherited entries named for the inheriting symbol: a <param>/<typeparam> of the
    // source's parameter at position i documents the inheriting symbol's parameter at
    // position i. An entry naming none of the source's parameters keeps its name; one
    // whose position the inheriting symbol does not have documents nothing of it.
    private static StructuredDocs RenameInheritedEntries(StructuredDocs inherited, ISymbol source, ISymbol inheriting) =>
        inherited with
        {
            Params = RenameByPosition(inherited.Params, ParameterNames(source), ParameterNames(inheriting)),
            TypeParams = RenameByPosition(inherited.TypeParams, TypeParameterNames(source), TypeParameterNames(inheriting))
        };

    private static IReadOnlyList<DocEntry> RenameByPosition(
        IReadOnlyList<DocEntry> entries, IReadOnlyList<string> sourceNames, IReadOnlyList<string> inheritingNames)
    {
        var renamed = new List<DocEntry>();
        foreach (var entry in entries)
        {
            var position = sourceNames.ToList().IndexOf(entry.Name);
            if (position < 0) renamed.Add(entry);
            else if (position < inheritingNames.Count) renamed.Add(entry with { Name = inheritingNames[position] });
        }
        return renamed;
    }

    private static IReadOnlyList<string> ParameterNames(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters.Select(p => p.Name).ToList(),
        IPropertySymbol property => property.Parameters.Select(p => p.Name).ToList(),
        INamedTypeSymbol { DelegateInvokeMethod: { } invoke } => invoke.Parameters.Select(p => p.Name).ToList(),
        _ => Array.Empty<string>()
    };

    private static IReadOnlyList<string> TypeParameterNames(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.TypeParameters.Select(t => t.Name).ToList(),
        INamedTypeSymbol type => type.TypeParameters.Select(t => t.Name).ToList(),
        _ => Array.Empty<string>()
    };

    // The symbol a bare <inheritdoc/> inherits from: the overridden member, the
    // explicitly implemented interface member, or the implicitly satisfied one; for a
    // constructor, the base class constructor with the same parameter types; for a type,
    // its non-Object base or first interface.
    private static ISymbol? FindInheritedDocSource(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor } constructor => MatchingBaseConstructor(constructor),
        IMethodSymbol method => method.OverriddenMethod
            ?? method.ExplicitInterfaceImplementations.FirstOrDefault()
            ?? FindImplicitInterfaceMember(method),
        IPropertySymbol property => property.OverriddenProperty
            ?? property.ExplicitInterfaceImplementations.FirstOrDefault()
            ?? FindImplicitInterfaceMember(property),
        IEventSymbol @event => @event.OverriddenEvent
            ?? @event.ExplicitInterfaceImplementations.FirstOrDefault()
            ?? FindImplicitInterfaceMember(@event),
        INamedTypeSymbol type => type.BaseType is { SpecialType: not SpecialType.System_Object } baseType
            ? baseType
            : type.Interfaces.FirstOrDefault(),
        _ => null
    };

    // The base class's constructor with the same parameter types and ref kinds.
    private static IMethodSymbol? MatchingBaseConstructor(IMethodSymbol constructor) =>
        constructor.ContainingType.BaseType?.InstanceConstructors.FirstOrDefault(candidate =>
            candidate.Parameters.Length == constructor.Parameters.Length
            && candidate.Parameters.Zip(constructor.Parameters).All(pair =>
                pair.First.RefKind == pair.Second.RefKind
                && SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type)));

    // The interface member this symbol implicitly implements, if any: walk the
    // containing type's interfaces and match each member's implementation.
    private static ISymbol? FindImplicitInterfaceMember(ISymbol member)
    {
        var containingType = member.ContainingType;
        if (containingType == null) return null;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var interfaceMember in iface.GetMembers())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation != null
                    && SymbolEqualityComparer.Default.Equals(implementation, member))
                {
                    return interfaceMember;
                }
            }
        }
        return null;
    }

    // The compilation in which to resolve crefs: the one owning the symbol's source
    // (exact for source symbols); for a metadata symbol, the first project in
    // logical-project order whose compilation references its assembly — a project that
    // does not reference it cannot resolve the assembly's crefs.
    private async Task<Compilation?> FindCompilationForSymbolAsync(
        ISymbol symbol, CancellationToken cancellationToken)
    {
        var tree = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
        if (tree != null)
        {
            var document = _solution!.GetDocument(tree);
            if (document != null)
            {
                return await document.Project.GetCompilationAsync(cancellationToken);
            }
        }

        var assembly = symbol.ContainingAssembly;
        if (assembly == null) return null;
        foreach (var project in GetLogicalProjects().Projects.SelectMany(p => p.Projects))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;
            if (SymbolEqualityComparer.Default.Equals(compilation.Assembly, assembly)) return compilation;
            if (compilation.References
                .Select(compilation.GetAssemblyOrModuleSymbol)
                .OfType<IAssemblySymbol>()
                .Any(referenced => referenced.Identity.Equals(assembly.Identity)))
            {
                return compilation;
            }
        }
        return null;
    }

    internal static object? StructuredDocsToWireOrNull(StructuredDocs docs) =>
        docs.IsEmpty ? null : StructuredDocsToWire(docs);

    private static object StructuredDocsToWire(StructuredDocs docs) => new
    {
        summary = docs.Summary,
        returns = docs.Returns,
        remarks = docs.Remarks,
        overloads = docs.Overloads,
        @params = docs.Params.Select(p => new { name = p.Name, text = p.Text }).ToList(),
        typeParams = docs.TypeParams.Select(p => new { name = p.Name, text = p.Text }).ToList(),
        exceptions = docs.Exceptions.Select(e => new { type = e.Name, text = e.Text }).ToList()
    };
}
