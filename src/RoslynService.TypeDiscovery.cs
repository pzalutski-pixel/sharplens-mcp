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
        int maxResults = 100)
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

        var type = await FindTypeByNameAsync(typeName);
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
            while (currentType != null && currentType.SpecialType != SpecialType.System_Object)
            {
                allMembers.AddRange(currentType.GetMembers().Where(m => !m.IsImplicitlyDeclared));
                currentType = currentType.BaseType;
            }
        }
        else
        {
            allMembers.AddRange(type.GetMembers().Where(m => !m.IsImplicitlyDeclared));
        }

        // Filter by member kind if specified
        if (!string.IsNullOrEmpty(memberKind))
        {
            allMembers = allMembers.Where(m =>
            {
                return memberKind.ToLower() switch
                {
                    "method" => m is IMethodSymbol ms && ms.MethodKind == MethodKind.Ordinary,
                    "property" => m is IPropertySymbol,
                    "field" => m is IFieldSymbol,
                    "event" => m is IEventSymbol,
                    _ => true
                };
            }).ToList();
        }

        // Remove duplicates (from inheritance) and limit
        var uniqueMembers = allMembers
            .GroupBy(m => m.Name + m.Kind.ToString())
            .Select(g => g.First())
            .Take(maxResults)
            .ToList();

        var totalCount = allMembers.GroupBy(m => m.Name + m.Kind.ToString()).Count();

        // Format members based on verbosity
        var formattedMembers = uniqueMembers.Select(m => FormatMember(m, verbosity)).ToList();

        // Count by kind
        var countByKind = uniqueMembers
            .GroupBy(m => GetMemberKindString(m))
            .ToDictionary(g => g.Key, g => g.Count());

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                typeKind = type.TypeKind.ToString(),
                totalMembers = totalCount,
                memberCounts = countByKind,
                members = formattedMembers
            },
            suggestedNextTools: new[]
            {
                $"get_method_signature to get detailed parameter info for a specific method",
                $"get_base_types for {type.Name} to see inheritance chain",
                $"get_attributes to find [Export] or [Signal] decorated members"
            },
            totalCount: totalCount,
            returnedCount: uniqueMembers.Count,
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
        int maxResultsPerType = 50)
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
            var result = await GetTypeMembersAsync(typeName, includeInherited, memberKind, verbosity, maxResultsPerType);

            // Check if result was successful
            var resultDict = result as dynamic;
            if (resultDict?.success == true)
            {
                results.Add(new
                {
                    typeName,
                    success = true,
                    data = resultDict.data
                });
            }
            else
            {
                errors.Add(new
                {
                    typeName,
                    success = false,
                    error = resultDict?.error
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

    private object FormatMember(ISymbol member, string verbosity)
    {
        var kind = GetMemberKindString(member);

        // Summary: minimal info
        if (verbosity == "summary")
        {
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
        int? overloadIndex = null)
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

        var type = await FindTypeByNameAsync(typeName);
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

        if (methods.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found on type '{type.Name}'",
                hint: $"Use get_type_members for {type.Name} with memberKind='Method' to see available methods",
                context: new { typeName = type.ToDisplayString(), methodName }
            );
        }

        // Select the specific overload or first one
        var method = overloadIndex.HasValue && overloadIndex.Value < methods.Count
            ? methods[overloadIndex.Value]
            : methods[0];

        var parameters = method.Parameters.Select(p => new
        {
            name = p.Name,
            type = p.Type.ToDisplayString(),
            isOptional = p.IsOptional,
            hasDefaultValue = p.HasExplicitDefaultValue,
            defaultValue = p.HasExplicitDefaultValue ? p.ExplicitDefaultValue?.ToString() : null,
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
                selectedOverload = overloadIndex ?? 0,
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

    /// <summary>
    /// Finds all symbols with specific attributes, with Godot-specific parsing.
    /// </summary>
    public async Task<object> GetAttributesAsync(
        string attributeName,
        string? scope = null,
        bool parseGodotHints = true,
        int maxResults = 100)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(attributeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "attributeName is required",
                hint: "Provide an attribute name like 'Export', 'Signal', 'Tool', or 'GlobalClass'"
            );
        }

        var results = new List<object>();
        var totalFound = 0;

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
            var compilation = await project.GetCompilationAsync();
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
                var root = await syntaxTree.GetRootAsync();

                // Find all declarations with attributes
                var declarations = root.DescendantNodes()
                    .Where(n => n is MemberDeclarationSyntax || n is TypeDeclarationSyntax);

                foreach (var decl in declarations)
                {
                    var symbol = semanticModel.GetDeclaredSymbol(decl);
                    if (symbol == null) continue;

                    var matchingAttrs = symbol.GetAttributes()
                        .Where(a =>
                            a.AttributeClass?.Name.Contains(attributeName, StringComparison.OrdinalIgnoreCase) == true ||
                            a.AttributeClass?.Name.Contains($"{attributeName}Attribute", StringComparison.OrdinalIgnoreCase) == true)
                        .ToList();

                    if (matchingAttrs.Count == 0) continue;

                    totalFound++;
                    if (results.Count >= maxResults) continue;

                    foreach (var attr in matchingAttrs)
                    {
                        var attrInfo = new Dictionary<string, object?>
                        {
                            ["name"] = attr.AttributeClass?.Name,
                            ["fullName"] = attr.AttributeClass?.ToDisplayString()
                        };

                        // Godot-specific parsing for [Export] attributes
                        if (parseGodotHints && attributeName.Equals("Export", StringComparison.OrdinalIgnoreCase))
                        {
                            var args = attr.ConstructorArguments;
                            if (args.Length > 0)
                            {
                                attrInfo["godotHint"] = args[0].Value?.ToString();
                            }
                            if (args.Length > 1)
                            {
                                attrInfo["godotHintString"] = args[1].Value?.ToString();
                            }
                        }

                        // Include named arguments
                        if (attr.NamedArguments.Length > 0)
                        {
                            attrInfo["namedArguments"] = attr.NamedArguments
                                .ToDictionary(na => na.Key, na => na.Value.Value?.ToString());
                        }

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

    /// <summary>
    /// Finds all types inheriting from a base type, by name.
    /// </summary>
    public async Task<object> GetDerivedTypesAsync(
        string baseTypeName,
        bool includeTransitive = true,
        int maxResults = 100)
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

        var baseType = await FindTypeByNameAsync(baseTypeName);
        if (baseType == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Base type '{baseTypeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols to find available types"
            );
        }

        var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(
            baseType, _solution!, transitive: includeTransitive);

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
    public async Task<object> GetBaseTypesAsync(string typeName)
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

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols to find available types"
            );
        }

        // Walk up the inheritance chain
        var baseTypes = new List<object>();
        var currentBase = type.BaseType;
        while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object)
        {
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
                typeKind = type.TypeKind.ToString(),
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
                baseTypes.Count > 0 ? $"get_type_members for {((dynamic)baseTypes[0]).name} to see inherited members" : null
            }.Where(s => s != null).ToArray()!
        );
    }

}
