using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

public partial class RoslynService
{
    /// <summary>
    /// Gets all members of a type by name (methods, properties, fields, events).
    /// Supports fully-qualified names, simple names, and partial matches.
    /// </summary>
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

        var (type, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name (e.g., 'MyNamespace.MyClass') or check spelling. Use search_symbols to find available types.",
                context: new { typeName }
            );
        }

        // Collect members
        var allMembers = new List<ISymbol>();

        if (includeInherited)
        {
            // Walk up the inheritance chain
            var currentType = type;
            var isQueriedType = true;
            while (currentType != null && currentType.SpecialType != SpecialType.System_Object)
            {
                allMembers.AddRange(currentType.GetMembers().Where(m =>
                    !m.IsImplicitlyDeclared
                    // Constructors, static constructors, and destructors are NOT
                    // inherited — include them only from the queried type itself.
                    && (isQueriedType || m is not IMethodSymbol
                    {
                        MethodKind: MethodKind.Constructor
                            or MethodKind.StaticConstructor
                            or MethodKind.Destructor
                    })));
                currentType = currentType.BaseType;
                isQueriedType = false;
            }
            // Interface members (including default-interface-members) are part of
            // the inherited surface but are not on the base-type chain. The
            // MemberDedupFormat pass below collapses a member that the type itself
            // implements, leaving only the genuinely interface-supplied ones.
            foreach (var iface in type.AllInterfaces)
            {
                allMembers.AddRange(iface.GetMembers().Where(m => !m.IsImplicitlyDeclared));
            }
        }
        else
        {
            allMembers.AddRange(type.GetMembers().Where(m => !m.IsImplicitlyDeclared));
        }

        // Filter by member kind if specified. An unknown kind is rejected rather
        // than silently returning every member.
        if (!string.IsNullOrEmpty(memberKind))
        {
            var kind = memberKind.ToLower();
            if (kind is not ("method" or "property" or "field" or "event" or "constructor" or "operator"))
            {
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"Unknown memberKind '{memberKind}'",
                    hint: "Use one of: Method, Property, Field, Event, Constructor, Operator.",
                    context: new { typeName, memberKind });
            }
            allMembers = allMembers.Where(m => kind switch
            {
                "method" => m is IMethodSymbol ms && ms.MethodKind == MethodKind.Ordinary,
                "property" => m is IPropertySymbol,
                "field" => m is IFieldSymbol,
                "event" => m is IEventSymbol,
                "constructor" => m is IMethodSymbol cm && cm.MethodKind == MethodKind.Constructor,
                "operator" => m is IMethodSymbol om && om.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion,
                _ => false
            }).ToList();
        }

        // Members that share a containing-type-free signature are reconciled by who
        // concretely provides them:
        //  - If a class/struct member is present, it overrides the base member and
        //    implements the interface member of the same signature — keep only that
        //    most-derived occurrence (the inheritance walk adds it first).
        //  - If only interface members remain, they come from DISTINCT interfaces
        //    (GetMembers dedups within a type), so keep one per declaring interface
        //    rather than collapsing IFoo.Do() and IBar.Do() into a single entry.
        var distinctMembers = allMembers
            .GroupBy(m => m.ToDisplayString(MemberDedupFormat))
            .SelectMany(g =>
            {
                var concrete = g.FirstOrDefault(m => m.ContainingType?.TypeKind != TypeKind.Interface);
                if (concrete != null) return new[] { concrete };
                return g.GroupBy(m => m.ContainingType?.ToDisplayString() ?? string.Empty)
                        .Select(byInterface => byInterface.First());
            })
            .ToList();

        // One entry per member name+kind: overloads collapse into their first
        // declaration, and the entry reports how many overloads share the name.
        var memberGroups = distinctMembers
            .GroupBy(m => m.Name + m.Kind.ToString())
            .ToList();

        var totalCount = memberGroups.Count;
        var limitedGroups = memberGroups.Take(maxResults).ToList();

        // Format members based on verbosity
        var formattedMembers = limitedGroups
            .Select(g => FormatMember(g.First(), verbosity, g.Count()))
            .ToList();

        // Count by kind across ALL members, not just the returned page, so the
        // per-kind counts sum to totalMembers rather than to the capped list.
        var countByKind = memberGroups
            .GroupBy(g => GetMemberKindString(g.First()))
            .ToDictionary(g => g.Key, g => g.Count());

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                typeKind = GetTypeKindString(type),
                totalMembers = totalCount,
                memberCounts = countByKind,
                members = formattedMembers
            },
            suggestedNextTools: new[]
            {
                $"get_method_signature to get detailed parameter info for a specific method",
                $"get_base_types for {type.Name} to see inheritance chain",
                "find_attribute_usages to find members decorated with a specific attribute"
            },
            totalCount: totalCount,
            returnedCount: limitedGroups.Count,
            verbosity: verbosity
        );
    }

    /// <summary>
    /// Gets members for multiple types in a single call (batch optimization).
    /// </summary>
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

        var results = new List<object>();
        var errors = new List<object>();

        foreach (var typeName in typeNames.Distinct())
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

        return CreateSuccessResponse(
            data: new
            {
                totalRequested = typeNames.Count,
                successCount = results.Count,
                errorCount = errors.Count,
                results,
                errors = errors.Count > 0 ? errors : null
            },
            suggestedNextTools: new[]
            {
                results.Count > 0 ? "get_method_signature for detailed method info" : null,
                errors.Count > 0 ? "Check type names - some were not found" : null
            }.Where(s => s != null).ToArray()!,
            totalCount: typeNames.Count,
            returnedCount: results.Count
        );
    }

    // MinimallyQualifiedFormat with the containing type removed from member
    // rendering: "void Run()" instead of "void DerivedRunner.Run()", so an
    // override and the base member it replaces produce the same key.
    private static readonly SymbolDisplayFormat MemberDedupFormat =
        SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeParameters |
            SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeRef |
            SymbolDisplayMemberOptions.IncludeExplicitInterface);

    private object FormatMember(ISymbol member, string verbosity, int overloadCount = 1)
    {
        var kind = GetMemberKindString(member);

        // Summary: minimal info
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

        // Compact: add signature and key properties
        var result = new Dictionary<string, object>
        {
            ["name"] = member.Name,
            ["kind"] = kind,
            ["accessibility"] = member.DeclaredAccessibility.ToString(),
            ["isStatic"] = member.IsStatic
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

        // Full: add documentation, attributes, location
        if (verbosity == "full")
        {
            result["documentation"] = member.GetDocumentationCommentXml() ?? "";
            result["attributes"] = member.GetAttributes()
                .Select(a => a.AttributeClass?.Name ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .ToArray();
            result["location"] = GetSymbolLocation(member) ?? new { filePath = "", line = 0, column = 0 };
            result["containingType"] = member.ContainingType?.ToDisplayString() ?? "";
        }

        return result;
    }

    private static string GetMemberKindString(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol m when m.MethodKind == MethodKind.Ordinary => "Method",
            IMethodSymbol m when m.MethodKind == MethodKind.Constructor => "Constructor",
            IPropertySymbol => "Property",
            IFieldSymbol => "Field",
            IEventSymbol => "Event",
            _ => member.Kind.ToString()
        };
    }

    /// <summary>
    /// Gets detailed method signature including parameters, return type, and modifiers.
    /// </summary>
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

        var (type, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name or use get_type_members to list available types"
            );
        }

        // Find all methods with this name (including inherited)
        var methods = new List<IMethodSymbol>();
        var currentType = type;
        while (currentType != null)
        {
            methods.AddRange(currentType.GetMembers(methodName).OfType<IMethodSymbol>());
            currentType = currentType.BaseType;
        }

        // An override and the base method it replaces share a containing-type-free
        // signature; collapse them (keeping the most-derived, walked first) so the
        // overload count and index range don't double-count the same method.
        methods = methods
            .GroupBy(m => m.ToDisplayString(MemberDedupFormat))
            .Select(g => g.First())
            .ToList();

        if (methods.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found on type '{type.Name}'",
                hint: $"Use get_type_members for {type.Name} with memberKind='Method' to see available methods",
                context: new { typeName = type.ToDisplayString(), methodName }
            );
        }

        // Select the specific overload or default to first
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
                overloadCount = methods.Count,
                selectedOverload = index,
                documentation = method.GetDocumentationCommentXml(),
                location = GetSymbolLocation(method)
            },
            suggestedNextTools: new[]
            {
                $"find_callers to see where {method.Name} is called",
                $"get_type_members for {type.Name} to see other methods",
                methods.Count > 1 ? $"get_method_signature with overloadIndex=0..{methods.Count - 1} to see other overloads" : null
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

    /// <summary>
    /// Finds all symbols with specific attributes.
    /// </summary>
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

        var results = new List<object>();
        var totalFound = 0;
        // A partial type resolves to the same symbol from each of its parts; dedup by
        // symbol identity (across trees and files) so its attributes are counted once.
        var seenSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        // Determine which projects/documents to search
        IEnumerable<Project> projectsToSearch = _solution!.Projects;
        if (!string.IsNullOrEmpty(scope))
        {
            if (scope.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
            {
                var projectName = scope.Substring("project:".Length);
                projectsToSearch = projectsToSearch.Where(p =>
                    p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            }
            // file: scope handled below
        }

        foreach (var project in projectsToSearch)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                // Handle file: scope
                if (!string.IsNullOrEmpty(scope) && scope.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = scope.Substring("file:".Length);
                    if (!syntaxTree.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                // Find all declarations with attributes
                var declarations = root.DescendantNodes()
                    .Where(n => n is MemberDeclarationSyntax || n is TypeDeclarationSyntax);

                foreach (var decl in declarations)
                {
                    foreach (var symbol in DeclaredSymbolsForAttributes(decl, semanticModel, cancellationToken))
                    {
                        // A field/event-field declares one symbol per variable; partial
                        // parts share a symbol. Process each declared symbol exactly once.
                        if (symbol == null || !seenSymbols.Add(symbol)) continue;

                        // Exact match (with trailing-Attribute handling) via the shared
                        // matcher, NOT substring Contains — so get_attributes agrees with
                        // find_attribute_usages and a query for "Obsolete" doesn't also match
                        // an unrelated "ObsoleteThing" attribute.
                        var matchingAttrs = symbol.GetAttributes()
                            .Where(a => MatchesAttribute(a, attributeName))
                            .ToList();

                        if (matchingAttrs.Count == 0) continue;

                        foreach (var attr in matchingAttrs)
                        {
                            // Count per attribute APPLICATION, exactly as find_attribute_usages
                            // does — an AllowMultiple attribute applied twice on one declaration
                            // counts twice, so returnedCount never exceeds totalCount and the
                            // maxResults cap can't overshoot inside a single declaration.
                            totalFound++;
                            if (results.Count >= maxResults) continue;

                            var attrInfo = new Dictionary<string, object?>
                            {
                                ["name"] = attr.AttributeClass?.Name,
                                ["fullName"] = attr.AttributeClass?.ToDisplayString(),
                                // Positional and named arguments, rendered with the C#-literal
                                // renderer (ToCSharpString) exactly as find_attribute_usages
                                // emits them — not the ambiguous boxed .ToString().
                                ["arguments"] = attr.ConstructorArguments.Select(a => a.ToCSharpString()).ToList(),
                                ["namedArguments"] = attr.NamedArguments
                                    .ToDictionary(na => na.Key, na => (object?)na.Value.ToCSharpString())
                            };

                            results.Add(new
                            {
                                symbolName = symbol.Name,
                                symbolKind = symbol.Kind.ToString(),
                                containingType = symbol.ContainingType?.ToDisplayString(),
                                memberType = symbol is IPropertySymbol ps ? ps.Type.ToDisplayString() :
                                            symbol is IFieldSymbol fs ? fs.Type.ToDisplayString() :
                                            symbol is IMethodSymbol ms ? ms.ReturnType.ToDisplayString() : null,
                                location = GetSymbolLocation(symbol),
                                attribute = attrInfo
                            });
                        }
                    }
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                attributeName,
                totalFound,
                symbols = results
            },
            suggestedNextTools: new[]
            {
                "get_symbol_info to get more details about a specific symbol",
                "get_type_members to see all members of a containing type"
            },
            totalCount: totalFound,
            returnedCount: results.Count
        );
    }

    // Yields the declared symbol(s) carrying a declaration's attributes. A field or
    // event-field declares one symbol PER variable, and GetDeclaredSymbol on the
    // declaration node itself returns null, so its variables are resolved individually —
    // matching find_attribute_usages' per-member walk, which would otherwise see field
    // and event-field attributes that get_attributes dropped.
    private static IEnumerable<ISymbol?> DeclaredSymbolsForAttributes(
        SyntaxNode declaration, SemanticModel semanticModel, CancellationToken cancellationToken)
    {
        switch (declaration)
        {
            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                    yield return semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                break;
            case EventFieldDeclarationSyntax eventField:
                foreach (var variable in eventField.Declaration.Variables)
                    yield return semanticModel.GetDeclaredSymbol(variable, cancellationToken);
                break;
            default:
                yield return semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Finds all types inheriting from a base type, by name.
    /// </summary>
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

        var (baseType, ambiguousBaseType) = await FindTypeByNameAsync(baseTypeName, cancellationToken);
        if (ambiguousBaseType != null) return ambiguousBaseType;
        if (baseType == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Base type '{baseTypeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols to find available types"
            );
        }

        var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
            baseType, _solution!, transitive: includeTransitive, cancellationToken: cancellationToken);

        var derivedList = derivedTypes.ToList();
        var totalCount = derivedList.Count;

        var results = derivedList
            .Take(maxResults)
            .Select(dt => new
            {
                name = dt.Name,
                fullName = dt.ToDisplayString(),
                @namespace = dt.ContainingNamespace?.ToDisplayString(),
                isAbstract = dt.IsAbstract,
                isSealed = dt.IsSealed,
                location = GetSymbolLocation(dt),
                directBase = dt.BaseType?.ToDisplayString()
            })
            .ToList();

        return CreateSuccessResponse(
            data: new
            {
                baseType = baseType.ToDisplayString(),
                includeTransitive,
                totalDerived = totalCount,
                derivedTypes = results
            },
            suggestedNextTools: new[]
            {
                "get_type_members to see members of a derived type",
                "get_base_types to see the full inheritance chain of a type"
            },
            totalCount: totalCount,
            returnedCount: results.Count
        );
    }

    /// <summary>
    /// Gets full inheritance chain by type name.
    /// </summary>
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

        var (type, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols to find available types"
            );
        }

        // Walk up the inheritance chain. Track the first base name in a typed local
        // so the suggestedNextTools formatter doesn't have to reach back through the
        // anonymous-typed list with dynamic dispatch.
        var baseTypes = new List<object>();
        string? firstBaseName = null;
        var currentBase = type.BaseType;
        while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object)
        {
            firstBaseName ??= currentBase.Name;
            baseTypes.Add(new
            {
                name = currentBase.Name,
                fullName = currentBase.ToDisplayString(),
                isAbstract = currentBase.IsAbstract,
                location = GetSymbolLocation(currentBase)
            });
            currentBase = currentBase.BaseType;
        }

        // Collect all interfaces
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
                location = GetSymbolLocation(type)
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
