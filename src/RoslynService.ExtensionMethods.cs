using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLensMcp;

// get_extension_methods: which extension members apply to a type — classic
// `this`-parameter methods and C# 14 `extension(T receiver)` block members — grouped
// by their containing static class. Source-declared extensions only.
//   - A classic method applies when the compiler can call it on the type
//     (ReduceExtensionMethod: receiver conversion, type inference and constraints). For a
//     generic type definition (List`1), one whose receiver is a construction of it, of a
//     base class or of an interface it implements (this List<int>) applies too: it applies
//     to some construction of the type.
//   - A block applies when its receiver accepts the type: the compiler's own check on the
//     classic method an instance member lowers to, else the receiver's conversion, the
//     constraints of a type-parameter receiver (extension<T>(T value) where T : IShape),
//     or, for a generic receiver or a generic type definition, the same construction
//     rule. Each block member is reported once as the block member, never again as the
//     compiler-generated classic method it lowers to.
// Each member is reported once however many target frameworks compile it, in class
// then source order, with its declared receiver type and { filePath, line, column }.
public partial class RoslynService
{
    public async Task<object> GetExtensionMethodsAsync(
        string typeName, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (ParameterValidation.Limit("maxResults", maxResults, 500) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);

        var resolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        if (resolution.Error != null) return resolution.Error;
        if (resolution.Type is not { } targetType)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type not found: {typeName}",
                hint: "Use search_symbols to find the correct type name",
                context: new { typeName });
        }
        // The METADATA name (backtick arity, '+' nesting), not the display string —
        // GetTypeByMetadataName fails on "List<int>"/"Outer.Inner".
        var targetMetadataName = FullMetadataName(targetType);

        var found = new List<(ISymbol Member, INamedTypeSymbol ContainingClass, string Style, ITypeSymbol Receiver, string? TargetFramework)>();
        foreach (var project in GetLogicalProjects().Projects.SelectMany(p => p.Projects))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);

            // The target re-resolved IN THIS compilation: ReduceExtensionMethod and
            // ClassifyConversion need same-compilation symbol identity.
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
                        // A block member's lowered classic method is implicitly declared.
                        case IMethodSymbol { IsExtensionMethod: true, IsImplicitlyDeclared: false } classic
                            when ClassicExtensionApplies(classic, targetInCompilation):
                            found.Add((classic, staticClass, "classic", classic.Parameters[0].Type, targetFramework));
                            break;

                        case INamedTypeSymbol { IsExtension: true, ExtensionParameter: { } receiver } extensionBlock
                            when ExtensionBlockApplies(compilation, extensionBlock, targetInCompilation):
                            foreach (var blockMember in extensionBlock.GetMembers())
                            {
                                if (blockMember.IsImplicitlyDeclared) continue;
                                if (blockMember is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol)) continue;
                                found.Add((blockMember, staticClass, "extensionBlock", receiver.Type, targetFramework));
                            }
                            break;
                    }
                }
            }
        }

        // One entry per member, by the solution's identity rule; a member without a
        // SolutionKey is keyed by its static class and signature.
        var merged = TargetFrameworkDeduplication.By(
                found,
                f => CrossCompilationSymbol.SolutionKey(f.Member)
                     ?? $"{CrossCompilationSymbol.SolutionKey(f.ContainingClass)}|{f.Member.ToDisplayString()}",
                StringComparer.Ordinal,
                f => f.TargetFramework)
            .OrderBy(m => m.Item.ContainingClass.ToDisplayString(), StringComparer.Ordinal)
            .ThenBy(m => ReportedLocation(SymbolDisplayLocation(m.Item.Member)), ResultLocationComparer.Instance)
            .ToList();
        var reported = merged.Take(maxResults).ToList();

        return CreateSuccessResponse(
            data: new
            {
                typeName = targetType.ToDisplayString(),
                groups = reported
                    .GroupBy(m => m.Item.ContainingClass.ToDisplayString(), StringComparer.Ordinal)
                    .Select(g => new
                    {
                        containingClass = g.Key,
                        members = g.Select(m => WithTargetFrameworks(new Dictionary<string, object?>
                        {
                            ["name"] = m.Item.Member.Name,
                            ["style"] = m.Item.Style,
                            ["kind"] = MemberClassifier.Classify(m.Item.Member)?.ToString() ?? m.Item.Member.Kind.ToString(),
                            ["signature"] = m.Item.Member.ToDisplayString(),
                            ["receiverType"] = m.Item.Receiver.ToDisplayString(),
                            ["location"] = GetSymbolLocation(m.Item.Member)
                        }, m.TargetFrameworks)).ToList()
                    })
                    .ToList(),
                notice = resolution.ShadowNotice
            },
            ResultCounts.Capped(merged.Count, reported.Count),
            suggestedNextTools: merged.Count > 0
                ? new[] { "get_method_source on an extension to read its implementation" }
                : new[] { "No source-declared extensions apply to this type" });
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

    private static bool ClassicExtensionApplies(IMethodSymbol extension, INamedTypeSymbol target) =>
        extension.ReduceExtensionMethod(target) != null
        || (IsGenericDefinition(target) && AppliesToSomeConstruction(extension.Parameters[0].Type, target));

    private static bool ExtensionBlockApplies(Compilation compilation, INamedTypeSymbol extensionBlock, INamedTypeSymbol target)
    {
        var receiver = extensionBlock.ExtensionParameter!.Type;
        // The compiler's own applicability check on the classic method an instance member
        // lowers to: receiver conversion, type inference and constraints. The check runs
        // on the lowered method as its static class declares it (implicitly), found by the
        // signature AssociatedExtensionImplementation names: the symbol that property
        // returns is not the class's member, and ReduceExtensionMethod does not reduce it
        // for a generic receiver (extension<T>(T value)).
        var associated = extensionBlock.GetMembers().OfType<IMethodSymbol>()
            .Select(m => m.AssociatedExtensionImplementation?.ToDisplayString())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var implementations = extensionBlock.ContainingType.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.IsImplicitlyDeclared && m.IsExtensionMethod && associated.Contains(m.ToDisplayString()))
            .ToList();
        if (implementations.Count > 0)
        {
            return implementations.Any(i => i.ReduceExtensionMethod(target) != null)
                   || (IsGenericDefinition(target) && AppliesToSomeConstruction(receiver, target));
        }

        return receiver switch
        {
            ITypeParameterSymbol typeParameter => SatisfiesConstraints(compilation, typeParameter, target),
            _ => ConvertsImplicitly(compilation, target, receiver)
                 || ((IsGenericDefinition(target) || MentionsTypeParameters(receiver)) && AppliesToSomeConstruction(receiver, target))
        };
    }

    // A generic type definition, such as the List<T> "List`1" names.
    private static bool IsGenericDefinition(INamedTypeSymbol type) =>
        type.Arity > 0 && SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition);

    // Whether a receiver type is a construction of the target's definition, of one of its
    // base classes, or of an interface it implements — so it accepts some construction of
    // the target.
    private static bool AppliesToSomeConstruction(ITypeSymbol receiver, INamedTypeSymbol target)
    {
        if (receiver is not INamedTypeSymbol named) return false;
        var definition = named.OriginalDefinition;
        for (INamedTypeSymbol? current = target; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, definition)) return true;
        }
        return target.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, definition));
    }

    private static bool MentionsTypeParameters(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        INamedTypeSymbol named => named.TypeArguments.Any(MentionsTypeParameters),
        IArrayTypeSymbol array => MentionsTypeParameters(array.ElementType),
        _ => false
    };

    // Identity, an implicit reference conversion, or boxing (a value-type target to object
    // or an interface): the conversions an extension receiver accepts.
    private static bool ConvertsImplicitly(Compilation compilation, ITypeSymbol source, ITypeSymbol destination)
    {
        var conversion = compilation.ClassifyConversion(source, destination);
        return conversion.IsIdentity || (conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing));
    }

    // Whether the target satisfies a type-parameter receiver's constraints: class, struct,
    // unmanaged and new(), and each constraint type (one generic over the type parameter
    // itself by its definition).
    private static bool SatisfiesConstraints(Compilation compilation, ITypeParameterSymbol typeParameter, INamedTypeSymbol target)
    {
        if (typeParameter.HasReferenceTypeConstraint && !target.IsReferenceType) return false;
        if (typeParameter.HasValueTypeConstraint && !target.IsValueType) return false;
        if (typeParameter.HasUnmanagedTypeConstraint && !target.IsUnmanagedType) return false;
        if (typeParameter.HasConstructorConstraint && !target.IsValueType
            && (target.IsAbstract || !target.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)))
            return false;
        return typeParameter.ConstraintTypes.All(constraint =>
            MentionsTypeParameters(constraint)
                ? AppliesToSomeConstruction(constraint, target)
                : ConvertsImplicitly(compilation, target, constraint));
    }
}
