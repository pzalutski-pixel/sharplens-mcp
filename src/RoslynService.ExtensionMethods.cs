using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLensMcp;

// get_extension_methods: which extension members apply to a type — classic
// `this`-parameter methods (IsExtensionMethod + ReduceExtensionMethod) and
// C# 14 `extension(T receiver)` blocks (nested IsExtension containers whose
// receiver the target converts to). Source-declared extensions only, grouped
// by their containing static class.
public partial class RoslynService
{
    public async Task<object> GetExtensionMethodsAsync(
        string typeName, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (maxResults is < 1 or > 500)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxResults must be between 1 and 500 (got {maxResults})",
                context: new { maxResults });
        }

        var (targetType, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
        if (targetType == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName });
        }
        // The METADATA name (backtick arity, '+' nesting), not the display string —
        // GetTypeByMetadataName fails on "List<int>"/"Outer.Inner", so a generic or
        // nested target used to skip every project that only references it.
        var targetMetadataName = FullMetadataName(targetType);

        var membersByClass = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totalFound = 0;

        foreach (var project in _solution!.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;

            // The target re-resolved IN THIS compilation: ReduceExtensionMethod
            // and ClassifyConversion need same-compilation symbol identity.
            var targetInCompilation = compilation.GetTypeByMetadataName(targetMetadataName)
                ?? (SymbolEqualityComparer.Default.Equals(targetType.ContainingAssembly, compilation.Assembly)
                    ? targetType
                    : null);
            if (targetInCompilation == null) continue;

            foreach (var staticClass in GetAllNamedTypes(compilation))
            {
                if (!staticClass.IsStatic || staticClass.TypeKind != TypeKind.Class) continue;
                if (!staticClass.Locations.Any(l => l.IsInSource)) continue;
                if (!SymbolEqualityComparer.Default.Equals(staticClass.ContainingAssembly, compilation.Assembly)) continue;

                foreach (var member in staticClass.GetMembers())
                {
                    switch (member)
                    {
                        case IMethodSymbol { IsExtensionMethod: true } classic
                            when classic.ReduceExtensionMethod(targetInCompilation) != null:
                            AddEntry(staticClass, classic, "classic");
                            break;

                        case INamedTypeSymbol { IsExtension: true } extensionBlock
                            when ReceiverAccepts(compilation, extensionBlock, targetInCompilation):
                            foreach (var blockMember in extensionBlock.GetMembers())
                            {
                                if (blockMember.IsImplicitlyDeclared) continue;
                                if (blockMember is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol)) continue;
                                AddEntry(staticClass, blockMember, "extensionBlock");
                            }
                            break;
                    }
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                typeName = targetType.ToDisplayString(),
                groups = membersByClass
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new { containingClass = g.Key, members = g.Value })
                    .ToList()
            },
            suggestedNextTools: membersByClass.Count > 0
                ? new[] { "get_method_source on an extension to read its implementation" }
                : new[] { "No source-declared extensions apply to this type" },
            totalCount: totalFound,
            returnedCount: membersByClass.Values.Sum(v => v.Count));

        void AddEntry(INamedTypeSymbol containingClass, ISymbol member, string style)
        {
            var classKey = containingClass.ToDisplayString();
            var signature = member.ToDisplayString();
            if (!seen.Add($"{classKey}|{signature}")) return;

            totalFound++;
            var currentCount = membersByClass.Values.Sum(v => v.Count);
            if (currentCount >= maxResults) return;

            var location = member.Locations.FirstOrDefault(l => l.IsInSource);
            var lineSpan = location?.GetLineSpan();
            if (!membersByClass.TryGetValue(classKey, out var entries))
            {
                entries = new List<object>();
                membersByClass[classKey] = entries;
            }
            entries.Add(new
            {
                name = member.Name,
                style,
                kind = member.Kind.ToString(),
                signature,
                location = lineSpan is { } span
                    ? new { filePath = FormatPath(span.Path), line = span.StartLinePosition.Line }
                    : null as object
            });
        }
    }

    // The full metadata name of a type's definition: namespace + the nested-type
    // chain joined by '+', each segment carrying its backtick arity (List`1,
    // Outer`1+Inner). This is what GetTypeByMetadataName expects, unlike the
    // angle-bracket display string.
    private static string FullMetadataName(INamedTypeSymbol type)
    {
        var segments = new List<string>();
        for (INamedTypeSymbol? current = type.OriginalDefinition; current != null; current = current.ContainingType)
        {
            segments.Insert(0, current.MetadataName);
        }
        var nested = string.Join("+", segments);
        return type.OriginalDefinition.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? $"{ns.ToDisplayString()}.{nested}"
            : nested;
    }

    // A C# 14 extension block applies when the target type IS the receiver
    // type or converts to it implicitly by identity/reference.
    private static bool ReceiverAccepts(
        Compilation compilation, INamedTypeSymbol extensionBlock, INamedTypeSymbol target)
    {
        var receiverType = extensionBlock.ExtensionParameter?.Type;
        if (receiverType == null) return false;
        if (SymbolEqualityComparer.Default.Equals(
                receiverType.OriginalDefinition, target.OriginalDefinition)) return true;

        // A generic receiver the target satisfies through an interface or base type
        // (an extension on IEnumerable<T> applies to a List<int> target). The
        // receiver's T is unbound from the target, so match by its definition.
        if (receiverType is INamedTypeSymbol { IsGenericType: true } genericReceiver)
        {
            var definition = genericReceiver.OriginalDefinition;
            for (var baseType = target.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, definition)) return true;
            }
            if (target.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, definition)))
                return true;
        }

        var conversion = compilation.ClassifyConversion(target, receiverType);
        // Boxing (a value-type target to object/an interface) is implicit but not a
        // reference conversion, yet the extension still applies.
        return conversion.IsIdentity ||
               (conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing));
    }
}
