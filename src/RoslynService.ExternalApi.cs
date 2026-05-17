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
        int maxMembers = 200)
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
            var compilation = await project.GetCompilationAsync();
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
        }

        var visibleMembers = allMembers
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m.DeclaredAccessibility == Accessibility.Public
                     || m.DeclaredAccessibility == Accessibility.Protected
                     || m.DeclaredAccessibility == Accessibility.ProtectedOrInternal)
            .ToList();

        var totalMembers = visibleMembers.Count;

        var memberList = visibleMembers
            .Take(maxMembers)
            .Select(m => new
            {
                name = m.Name,
                kind = m.Kind.ToString(),
                signature = m.ToDisplayString(),
                xmlDoc = includeXmlDocs ? ExtractSummary(m.GetDocumentationCommentXml()) : null,
                isStatic = m.IsStatic,
                isObsolete = m.GetAttributes().Any(a => a.AttributeClass?.Name == "ObsoleteAttribute"),
                accessibility = m.DeclaredAccessibility.ToString()
            })
            .ToList();

        var assembly = type.ContainingAssembly?.Name ?? "<unknown>";
        var typeXmlDoc = includeXmlDocs ? ExtractSummary(type.GetDocumentationCommentXml()) : null;

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
            var name = parts[idx];

            // Try child namespaces first
            if (container is INamespaceSymbol ns)
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

            // Try type members (handles nested types)
            var types = container.GetTypeMembers(name);
            foreach (var t in types)
            {
                if (idx == parts.Length - 1) return t;
                var found = FindRecursive(t, parts, idx + 1);
                if (found != null) return found;
            }

            return null;
        }
    }

    // XML doc returned by Roslyn looks like <member><summary>text</summary>...</member>.
    // Extract the summary text only; fall back to the raw XML if parsing fails so callers
    // never lose information.
    private static string? ExtractSummary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var summary = doc.Root?.Element("summary")?.Value?.Trim();
            return string.IsNullOrEmpty(summary) ? null : summary;
        }
        catch
        {
            return xml.Trim();
        }
    }
}
