using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLensMcp;

public partial class RoslynService
{
    // The verbosity levels get_type_members(_batch) accept.
    private static readonly IReadOnlyList<string> MemberVerbosities = new[] { "summary", "compact", "full" };

    // The members of a type by name, listed by the one listing rule (UserVisibleMembers: no accessors or
    // compiler-generated members; with includeInherited, the accessible base members C#
    // inheritance gives the type), labeled and filtered by the MemberKind vocabulary. One
    // entry per name and kind (overloads collapse, with overloadCount); an explicit interface
    // implementation is flagged, as it is not part of the type's own public surface.
    public async Task<object> GetTypeMembersAsync(
        string typeName,
        bool includeInherited = false,
        string? memberKind = null,
        string verbosity = "compact",
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required",
                hint: "Provide a type name like 'MyClass' or 'MyNamespace.MyService'",
                context: new { parameter = "typeName" }
            );
        }
        if (ParameterValidation.Choice("memberKind", memberKind, MemberClassifier.KindNames, out var kindFilter) is { } invalidKind)
            return CreateErrorResponse(invalidKind);
        if (ParameterValidation.Choice("verbosity", verbosity, MemberVerbosities, out var canonicalVerbosity) is { } invalidVerbosity)
            return CreateErrorResponse(invalidVerbosity);
        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);

        var resolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        if (resolution.Error != null) return resolution.Error;
        if (resolution.Type is not { } type)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name (e.g., 'MyNamespace.MyClass') or check spelling. Use search_symbols to find available types.",
                context: new { typeName }
            );
        }

        var members = UserVisibleMembers.Of(type, includeInherited)
            .Where(m => kindFilter == null || m.Kind.ToString() == kindFilter);

        // One entry per member name and kind: overloads collapse into their first
        // occurrence (declared before inherited), and the entry reports how many overloads
        // share the name.
        var memberGroups = members.GroupBy(m => (m.Symbol.Name, m.Kind)).ToList();
        var limitedGroups = memberGroups.Take(maxResults).ToList();
        var formattedMembers = limitedGroups
            .Select(g => FormatMember(g.First(), canonicalVerbosity!, g.Count()))
            .ToList();

        // Count by kind across ALL members, not just the returned page, so the per-kind
        // counts sum to totalMembers rather than to the capped list.
        var countByKind = memberGroups
            .GroupBy(g => g.Key.Kind)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                typeKind = GetTypeKindString(type),
                totalMembers = memberGroups.Count,
                memberCounts = countByKind,
                members = formattedMembers,
                notice = resolution.ShadowNotice
            },
            ResultCounts.Capped(memberGroups.Count, limitedGroups.Count),
            suggestedNextTools: new[]
            {
                $"get_method_signature to get detailed parameter info for a specific method",
                $"get_base_types for {type.Name} to see inheritance chain",
                "find_attribute_usages to find members decorated with a specific attribute"
            },
            verbosity: canonicalVerbosity
        );
    }

    // The members of several types in one call: one entry per requested name, in request
    // order — its get_type_members data or its error.
    public async Task<object> GetTypeMembersBatchAsync(
        List<string> typeNames,
        bool includeInherited = false,
        string? memberKind = null,
        string verbosity = "compact",
        int maxResultsPerType = 50,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (typeNames == null || typeNames.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeNames array is required and must not be empty",
                hint: "Provide an array of type names like ['ServiceA', 'ServiceB', 'ControllerC']",
                context: new { parameter = "typeNames" }
            );
        }
        if (ParameterValidation.Choice("memberKind", memberKind, MemberClassifier.KindNames, out _) is { } invalidKind)
            return CreateErrorResponse(invalidKind);
        if (ParameterValidation.Choice("verbosity", verbosity, MemberVerbosities, out _) is { } invalidVerbosity)
            return CreateErrorResponse(invalidVerbosity);
        if (ParameterValidation.Limit("maxResultsPerType", maxResultsPerType, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);

        var results = new List<object>();
        var errors = new List<object>();

        foreach (var typeName in typeNames)
        {
            var result = await GetTypeMembersAsync(typeName, includeInherited, memberKind, verbosity, maxResultsPerType, cancellationToken);

            if (IsSuccessResponse(result))
            {
                results.Add(new
                {
                    typeName,
                    success = true,
                    data = GetResponseData(result)
                });
            }
            else
            {
                errors.Add(new
                {
                    typeName,
                    success = false,
                    error = GetResponseError(result)
                });
            }
        }

        // Every requested type is answered — resolved or with its error — so nothing is
        // cut off: totalCount is the request size, returnedCount the resolved ones.
        return CreateSuccessResponse(
            data: new
            {
                totalRequested = typeNames.Count,
                successCount = results.Count,
                errorCount = errors.Count,
                results,
                errors = errors.Count > 0 ? errors : null
            },
            ResultCounts.Explicit(typeNames.Count, results.Count, truncated: false),
            suggestedNextTools: new[]
            {
                results.Count > 0 ? "get_method_signature for detailed method info" : null,
                errors.Count > 0 ? "Check type names - some were not found" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    // A member entry: summary is { name, kind } (+ overloadCount for a method); compact adds
    // accessibility, isStatic, isInherited, isExplicitInterfaceImplementation and the
    // kind-specific shape; full adds documentation, attributes, location ({ filePath, line,
    // column }, null for a member without source) and containingType.
    private object FormatMember(TypeMember entry, string verbosity, int overloadCount = 1)
    {
        var member = entry.Symbol;
        var kind = entry.Kind.ToString();

        if (verbosity == "summary")
        {
            if (member is IMethodSymbol)
            {
                return new
                {
                    name = member.Name,
                    kind,
                    overloadCount
                };
            }
            return new
            {
                name = member.Name,
                kind
            };
        }

        var result = new Dictionary<string, object?>
        {
            ["name"] = member.Name,
            ["kind"] = kind,
            ["accessibility"] = member.DeclaredAccessibility.ToString(),
            ["isStatic"] = member.IsStatic,
            ["isInherited"] = entry.IsInherited,
            ["isExplicitInterfaceImplementation"] = entry.IsExplicitInterfaceImplementation
        };

        if (member is IMethodSymbol method)
        {
            result["signature"] = method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            result["returnType"] = method.ReturnType.ToDisplayString();
            result["isAsync"] = method.IsAsync;
            result["isVirtual"] = method.IsVirtual;
            result["isOverride"] = method.IsOverride;
            result["isAbstract"] = method.IsAbstract;
            result["overloadCount"] = overloadCount;
        }
        else if (member is IPropertySymbol property)
        {
            result["type"] = property.Type.ToDisplayString();
            result["hasGetter"] = property.GetMethod != null;
            result["hasSetter"] = property.SetMethod != null;
            result["isVirtual"] = property.IsVirtual;
        }
        else if (member is IFieldSymbol field)
        {
            result["type"] = field.Type.ToDisplayString();
            result["isReadOnly"] = field.IsReadOnly;
            result["isConst"] = field.IsConst;
        }
        else if (member is IEventSymbol evt)
        {
            result["type"] = evt.Type.ToDisplayString();
        }

        if (verbosity == "full")
        {
            result["documentation"] = member.GetDocumentationCommentXml() ?? "";
            result["attributes"] = member.GetAttributes()
                .Select(a => a.AttributeClass?.Name ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            result["location"] = GetSymbolLocation(member);
            result["containingType"] = member.ContainingType?.ToDisplayString() ?? "";
        }

        return result;
    }

    // A method's detailed signature — parameters, return type, nullability and modifiers.
    // The method is addressed like get_method_source (ResolveMethodByNameAsync): the
    // canonical overload set with inherited overloads after the declared ones.
    public async Task<object> GetMethodSignatureAsync(
        string typeName,
        string methodName,
        int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required",
                hint: "Provide a type name like 'MyClass' or 'MyNamespace.MyService'"
            );
        }

        if (string.IsNullOrWhiteSpace(methodName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "methodName is required",
                hint: "Provide a method name like 'ProcessData' or 'Calculate'"
            );
        }

        var (method, set, index, resolution, error) = await ResolveMethodByNameAsync(typeName, methodName, overloadIndex, cancellationToken);
        if (method == null) return error!;
        var type = resolution!.Type!;

        var parameters = method.Parameters.Select(p => new
        {
            name = p.Name,
            type = p.Type.ToDisplayString(),
            isOptional = p.IsOptional,
            hasDefaultValue = p.HasExplicitDefaultValue,
            defaultValue = FormatDefaultValue(p),
            isParams = p.IsParams,
            refKind = p.RefKind.ToString(),
            isNullable = p.NullableAnnotation == NullableAnnotation.Annotated
        }).ToList();

        var typeParameters = method.TypeParameters.Select(tp => new
        {
            name = tp.Name,
            constraints = tp.ConstraintTypes.Select(c => c.ToDisplayString()).ToArray()
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                name = method.Name,
                containingType = method.ContainingType.ToDisplayString(),
                fullSignature = method.ToDisplayString(),
                returnType = method.ReturnType.ToDisplayString(),
                isAsync = method.IsAsync,
                isStatic = method.IsStatic,
                isVirtual = method.IsVirtual,
                isOverride = method.IsOverride,
                isAbstract = method.IsAbstract,
                isExtensionMethod = method.IsExtensionMethod,
                accessibility = method.DeclaredAccessibility.ToString(),
                parameters,
                typeParameters,
                overloadCount = set!.Count,
                selectedOverload = index,
                isInherited = set.IsInherited(index),
                documentation = method.GetDocumentationCommentXml(),
                location = GetSymbolLocation(method),
                notice = resolution.ShadowNotice
            },
            suggestedNextTools: new[]
            {
                $"find_callers to see where {method.Name} is called",
                $"get_type_members for {type.Name} to see other methods",
                set.Count > 1 ? $"get_method_signature with overloadIndex=0..{set.Count - 1} to see other overloads" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    // A parameter's default value rendered as the C# literal a caller would write:
    // `true`/`false` (not boxed `True`), quoted strings/chars, `null` vs `default`
    // for a struct's zero value — not the ambiguous boxed .ToString().
    private static string? FormatDefaultValue(IParameterSymbol p) =>
        p.HasExplicitDefaultValue ? FormatConstantLiteral(p.Type, p.ExplicitDefaultValue) : null;

    // The single renderer for a constant/default value as the C# literal a caller
    // would write — shared by get_method_signature, generate_constructor, and
    // change_signature so enums, primitives, and null/default format identically.
    // An enum value (a boxed underlying integer) renders as its member name, not
    // the raw int; an unmatched flags combo as a cast.
    internal static string FormatConstantLiteral(ITypeSymbol type, object? value)
    {
        if (value == null)
        {
            return type.IsValueType
                && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                ? "default"
                : "null";
        }
        // Unwrap Nullable<T> so a nullable enum (`Mode? x = Mode.On`) maps to its
        // member name rather than the boxed underlying integer — the underlying value
        // is identical, only the static type differs.
        var effectiveType = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && nullable.TypeArguments.Length == 1
            ? nullable.TypeArguments[0]
            : type;
        if (effectiveType is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            var field = enumType.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
            return field != null ? $"{enumType.Name}.{field.Name}" : $"({enumType.Name}){value}";
        }
        return SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false)
            ?? value.ToString() ?? "null";
    }

    // Every application of the attribute in scope (FindAttributeApplicationsAsync), one entry
    // per application, in the order the attributes are written.
    public async Task<object> GetAttributesAsync(
        string attributeName,
        string? scope = null,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(attributeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "attributeName is required",
                hint: "Provide an attribute name like 'Obsolete', 'Serializable', or 'JsonProperty'"
            );
        }
        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);

        var (projects, includeTree, scopeError) = ResolveAttributeScope(scope);
        if (projects == null) return scopeError!;

        var applications = await FindAttributeApplicationsAsync(projects, includeTree, attributeName, cancellationToken);
        var results = applications.Take(maxResults).Select(m =>
        {
            var application = m.Item;
            var owner = application.Owner;
            var attr = application.Attribute;
            return WithTargetFrameworks(new Dictionary<string, object?>
            {
                ["symbolName"] = owner.Name,
                ["symbolKind"] = AttributeOwnerKind(owner),
                ["target"] = application.Target,
                ["containingType"] = owner.ContainingType?.ToDisplayString(),
                ["memberType"] = owner switch
                {
                    IPropertySymbol property => property.Type.ToDisplayString(),
                    IFieldSymbol field => field.Type.ToDisplayString(),
                    IEventSymbol @event => @event.Type.ToDisplayString(),
                    IParameterSymbol parameter => parameter.Type.ToDisplayString(),
                    IMethodSymbol method => method.ReturnType.ToDisplayString(),
                    _ => null
                },
                ["location"] = GetSymbolLocation(owner),
                ["attributeLocation"] = FormatLocation(application.AttributeLocation),
                ["attribute"] = new Dictionary<string, object?>
                {
                    ["name"] = attr.AttributeClass?.Name,
                    ["fullName"] = attr.AttributeClass?.ToDisplayString(),
                    // Positional and named arguments, rendered with the C#-literal
                    // renderer (ToCSharpString) exactly as find_attribute_usages emits
                    // them — not the ambiguous boxed .ToString().
                    ["arguments"] = attr.ConstructorArguments.Select(a => a.ToCSharpString()).ToList(),
                    ["namedArguments"] = attr.NamedArguments
                        .ToDictionary(na => na.Key, na => (object?)na.Value.ToCSharpString())
                }
            }, m.TargetFrameworks);
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                attributeName,
                totalFound = applications.Count,
                symbols = results
            },
            ResultCounts.Capped(applications.Count, results.Count),
            suggestedNextTools: new[]
            {
                "get_symbol_info to get more details about a specific symbol",
                "get_type_members to see all members of a containing type"
            }
        );
    }

    // The types below a base type, by name. For a class, the classes deriving from it; for an interface, the interfaces deriving
    // from it and the types implementing it (FindDerivedTypesAsync) — each once, in
    // canonical order, capped at maxResults.
    public async Task<object> GetDerivedTypesAsync(
        string baseTypeName,
        bool includeTransitive = true,
        int maxResults = 100,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(baseTypeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "baseTypeName is required",
                hint: "Provide a base type name like 'BaseClass' or 'MyNamespace.BaseService'"
            );
        }
        if (ParameterValidation.Limit("maxResults", maxResults, MaxListLimit) is { } invalidLimit)
            return CreateErrorResponse(invalidLimit);

        var resolution = await ResolveTypeByNameAsync(baseTypeName, cancellationToken);
        if (resolution.Error != null) return resolution.Error;
        if (resolution.Type is not { } baseType)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Base type '{baseTypeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols to find available types"
            );
        }

        var derived = await FindDerivedTypesAsync(baseType, includeTransitive, cancellationToken);
        var results = derived
            .Take(maxResults)
            .Select(m => WithTargetFrameworks(new Dictionary<string, object?>
            {
                ["name"] = m.Item.Name,
                ["fullName"] = m.Item.ToDisplayString(),
                ["namespace"] = m.Item.ContainingNamespace?.ToDisplayString(),
                ["kind"] = GetTypeKindString(m.Item),
                ["isAbstract"] = m.Item.IsAbstract,
                ["isSealed"] = m.Item.IsSealed,
                ["location"] = GetSymbolLocation(m.Item),
                ["directBase"] = m.Item.BaseType?.ToDisplayString()
            }, m.TargetFrameworks))
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                baseType = baseType.ToDisplayString(),
                includeTransitive,
                totalDerived = derived.Count,
                derivedTypes = results,
                notice = resolution.ShadowNotice
            },
            ResultCounts.Capped(derived.Count, results.Count),
            suggestedNextTools: new[]
            {
                "get_type_members to see members of a derived type",
                "get_base_types to see the full inheritance chain of a type"
            }
        );
    }

    // A type's inheritance chain by name: the base classes nearest first, System.Object
    // omitted, and every interface.
    public async Task<object> GetBaseTypesAsync(string typeName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required",
                hint: "Provide a type name like 'MyClass' or 'MyBaseService'"
            );
        }

        var resolution = await ResolveTypeByNameAsync(typeName, cancellationToken);
        if (resolution.Error != null) return resolution.Error;
        if (resolution.Type is not { } type)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols to find available types"
            );
        }

        // Track the first base name in a typed local so the suggestedNextTools formatter
        // doesn't have to reach back through the anonymous-typed list.
        var baseTypes = new List<object>();
        string? firstBaseName = null;
        for (var currentBase = type.BaseType;
             currentBase != null && currentBase.SpecialType != SpecialType.System_Object;
             currentBase = currentBase.BaseType)
        {
            firstBaseName ??= currentBase.Name;
            baseTypes.Add(new
            {
                name = currentBase.Name,
                fullName = currentBase.ToDisplayString(),
                isAbstract = currentBase.IsAbstract,
                location = GetSymbolLocation(currentBase)
            });
        }

        var interfaces = type.AllInterfaces
            .Select(i => new
            {
                name = i.Name,
                fullName = i.ToDisplayString()
            })
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                typeKind = GetTypeKindString(type),
                isAbstract = type.IsAbstract,
                isSealed = type.IsSealed,
                baseTypes,
                interfaces,
                location = GetSymbolLocation(type),
                notice = resolution.ShadowNotice
            },
            suggestedNextTools: new[]
            {
                $"get_type_members to see members of {type.Name}",
                $"get_derived_types to find classes inheriting from {type.Name}",
                firstBaseName != null ? $"get_type_members for {firstBaseName} to see inherited members" : null
            }.Where(s => s != null).ToArray()!
        );
    }
}
