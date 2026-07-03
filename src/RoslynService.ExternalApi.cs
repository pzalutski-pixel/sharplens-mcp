using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLensMcp;

// External assembly inspection. Resolves metadata-only types (BCL, NuGet packages,
// closed-source assemblies) so agents can see their members and XML docs without
// hallucinating signatures. Mirrors the shape of get_type_members but works on
// types whose source isn't in the solution.
public partial class RoslynService
{
    public async Task<object> GetExternalTypeInfoAsync(
        string typeName,
        string? assemblyName = null,
        bool includeInherited = true,
        bool includeXmlDocs = true,
        int maxMembers = 200,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required",
                hint: "Provide a fully-qualified type name, e.g. 'System.Net.Http.HttpClient'",
                context: new { parameter = "typeName" });
        }

        // Try every project's compilation; metadata symbols are interchangeable across
        // projects as long as the same assembly is referenced. Returning from the first
        // project that resolves the type is sufficient.
        INamedTypeSymbol? type = null;
        Compilation? owningCompilation = null;
        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            var resolved = compilation.GetTypeByMetadataName(typeName);
            if (resolved != null && IsFromExpectedAssembly(resolved, assemblyName))
            {
                type = resolved;
                owningCompilation = compilation;
                break;
            }

            // GetTypeByMetadataName won't find nested types via dotted name (uses '+' for nesting).
            // Try walking namespaces if the dotted form failed and the caller passed something
            // like "Outer.Inner" intending "Outer+Inner".
            if (resolved == null)
            {
                resolved = FindTypeByDottedName(compilation.GlobalNamespace, typeName);
                if (resolved != null && IsFromExpectedAssembly(resolved, assemblyName))
                {
                    type = resolved;
                    owningCompilation = compilation;
                    break;
                }
            }
        }

        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: assemblyName != null
                    ? $"No assembly named '{assemblyName}' contains a type '{typeName}'. Check the FQN and the assembly name."
                    : "Use the fully-qualified name (e.g. 'System.Collections.Generic.List`1'). For generic types use backtick-arity (`1, `2).",
                context: new { typeName, assemblyName });
        }

        var allMembers = type.GetMembers().ToList();
        if (includeInherited)
        {
            var current = type.BaseType;
            while (current != null && current.SpecialType != SpecialType.System_Object)
            {
                allMembers.AddRange(current.GetMembers());
                current = current.BaseType;
            }
            // Interface members (including default-interface-members) are part of
            // the inherited surface but not on the base-type chain.
            foreach (var iface in type.AllInterfaces)
            {
                allMembers.AddRange(iface.GetMembers());
            }
        }

        var visibleMembers = allMembers
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m.DeclaredAccessibility == Accessibility.Public
                     || m.DeclaredAccessibility == Accessibility.Protected
                     || m.DeclaredAccessibility == Accessibility.ProtectedOrInternal)
            // An override and the base member it replaces (and an implemented
            // interface member) share a containing-type-free signature; keep the
            // most-derived occurrence, which the type-first walk added first.
            .GroupBy(m => m.ToDisplayString(MemberDedupFormat))
            .Select(g => g.First())
            .ToList();

        var totalMembers = visibleMembers.Count;

        var memberList = visibleMembers
            .Take(maxMembers)
            .Select(m => new
            {
                name = m.Name,
                kind = m.Kind.ToString(),
                signature = m.ToDisplayString(),
                xmlDoc = includeXmlDocs
                    ? StructuredDocsToWireOrNull(XmlDocParser.Parse(m.GetDocumentationCommentXml()).docs)
                    : null,
                isStatic = m.IsStatic,
                isObsolete = m.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute"),
                accessibility = m.DeclaredAccessibility.ToString()
            })
            .ToList();

        var assembly = type.ContainingAssembly?.Name ?? "<unknown>";
        var typeXmlDoc = includeXmlDocs
            ? StructuredDocsToWireOrNull(XmlDocParser.Parse(type.GetDocumentationCommentXml()).docs)
            : null;

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                assembly,
                typeKind = GetTypeKindString(type),
                isAbstract = type.IsAbstract,
                isSealed = type.IsSealed,
                isStatic = type.IsStatic,
                baseType = type.BaseType?.ToDisplayString(),
                interfaces = type.AllInterfaces.Select(i => i.ToDisplayString()).ToList(),
                xmlDoc = typeXmlDoc,
                members = memberList
            },
            suggestedNextTools: new[]
            {
                "get_external_type_info on a base type or interface to inspect inherited surface",
                "find_references to see where this external type is used in the solution"
            },
            totalCount: totalMembers,
            returnedCount: memberList.Count);
    }

    private static bool IsFromExpectedAssembly(INamedTypeSymbol type, string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return true;
        var actual = type.ContainingAssembly?.Name;
        return actual != null && actual.Equals(assemblyName, StringComparison.OrdinalIgnoreCase);
    }

    // Walks namespaces to resolve a dotted name like "Outer.Inner.Type" that
    // GetTypeByMetadataName might not find directly when nested-type metadata-name
    // uses '+' instead of '.'.
    private static INamedTypeSymbol? FindTypeByDottedName(INamespaceSymbol root, string dotted)
    {
        var parts = dotted.Split('.');
        return FindRecursive(root, parts, 0);

        static INamedTypeSymbol? FindRecursive(INamespaceOrTypeSymbol container, string[] parts, int idx)
        {
            if (idx >= parts.Length) return null;
            var part = parts[idx];

            // A part may carry backtick arity (Inner`1); GetTypeMembers matches by the
            // bare name, so split the arity off and filter the candidates by it.
            var tick = part.IndexOf('`');
            var name = tick >= 0 ? part[..tick] : part;
            int? arity = tick >= 0 && int.TryParse(part[(tick + 1)..], out var a) ? a : null;

            // Child namespaces first — only a non-arity part can be a namespace.
            if (arity == null && container is INamespaceSymbol ns)
            {
                foreach (var child in ns.GetNamespaceMembers())
                {
                    if (child.Name == name)
                    {
                        if (idx == parts.Length - 1) continue; // a namespace can't be the final type
                        var found = FindRecursive(child, parts, idx + 1);
                        if (found != null) return found;
                    }
                }
            }

            // Type members (handles nested types).
            foreach (var t in container.GetTypeMembers(name))
            {
                if (arity != null && t.Arity != arity) continue;
                if (idx == parts.Length - 1) return t;
                var found = FindRecursive(t, parts, idx + 1);
                if (found != null) return found;
            }

            return null;
        }
    }

}
