using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

public partial class RoslynService
{
    /// <summary>
    /// Analyzes variable assignments and usage in a code region.
    /// </summary>
    public async Task<object> AnalyzeDataFlowAsync(
        string filePath,
        int startLine,
        int endLine)
    {
        EnsureSolutionLoaded();

        var document = await GetDocumentAsync(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File '{filePath}' not found in solution",
                hint: "Check the file path or reload the solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics"
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree"
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var text = await syntaxTree.GetTextAsync();

        // Find the node spanning the specified lines
        var startPosition = text.Lines[Math.Max(0, startLine)].Start;
        var endPosition = text.Lines[Math.Min(text.Lines.Count - 1, endLine)].End;
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        var node = root.FindNode(span);

        // Try to find a suitable node for data flow analysis
        StatementSyntax? firstStatement = null;
        StatementSyntax? lastStatement = null;

        var statements = node.DescendantNodesAndSelf().OfType<StatementSyntax>().ToList();
        if (statements.Count > 0)
        {
            firstStatement = statements.First();
            lastStatement = statements.Last();
        }

        if (firstStatement == null || lastStatement == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "No statements found in the specified region",
                hint: "Ensure the line range contains executable statements",
                context: new { startLine, endLine }
            );
        }

        DataFlowAnalysis? dataFlow = null;
        try
        {
            dataFlow = firstStatement == lastStatement
                ? semanticModel.AnalyzeDataFlow(firstStatement)
                : semanticModel.AnalyzeDataFlow(firstStatement, lastStatement);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"Data flow analysis failed: {ex.Message}",
                hint: "Try selecting a different code region"
            );
        }

        if (dataFlow == null || !dataFlow.Succeeded)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Data flow analysis did not succeed",
                hint: "The selected region may not be suitable for data flow analysis"
            );
        }

        return CreateSuccessResponse(
            data: new
            {
                region = new { startLine, endLine },
                succeeded = dataFlow.Succeeded,
                variablesDeclared = dataFlow.VariablesDeclared.Select(s => s.Name).ToArray(),
                alwaysAssigned = dataFlow.AlwaysAssigned.Select(s => s.Name).ToArray(),
                dataFlowsIn = dataFlow.DataFlowsIn.Select(s => s.Name).ToArray(),
                dataFlowsOut = dataFlow.DataFlowsOut.Select(s => s.Name).ToArray(),
                readInside = dataFlow.ReadInside.Select(s => s.Name).ToArray(),
                writtenInside = dataFlow.WrittenInside.Select(s => s.Name).ToArray(),
                readOutside = dataFlow.ReadOutside.Select(s => s.Name).ToArray(),
                writtenOutside = dataFlow.WrittenOutside.Select(s => s.Name).ToArray(),
                captured = dataFlow.Captured.Select(s => s.Name).ToArray(),
                capturedInside = dataFlow.CapturedInside.Select(s => s.Name).ToArray(),
                capturedOutside = dataFlow.CapturedOutside.Select(s => s.Name).ToArray(),
                unsafeAddressTaken = dataFlow.UnsafeAddressTaken.Select(s => s.Name).ToArray()
            },
            suggestedNextTools: new[]
            {
                "analyze_control_flow to analyze branching in the same region",
                "get_diagnostics to check for related warnings"
            }
        );
    }

    /// <summary>
    /// Analyzes control flow (branching and reachability) in a code region.
    /// </summary>
    public async Task<object> AnalyzeControlFlowAsync(
        string filePath,
        int startLine,
        int endLine)
    {
        EnsureSolutionLoaded();

        var document = await GetDocumentAsync(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File '{filePath}' not found in solution",
                hint: "Check the file path or reload the solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model"
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync();
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree"
            );
        }

        var root = await syntaxTree.GetRootAsync();
        var text = await syntaxTree.GetTextAsync();

        var startPosition = text.Lines[Math.Max(0, startLine)].Start;
        var endPosition = text.Lines[Math.Min(text.Lines.Count - 1, endLine)].End;
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        var node = root.FindNode(span);

        var statements = node.DescendantNodesAndSelf().OfType<StatementSyntax>().ToList();
        if (statements.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "No statements found in the specified region",
                context: new { startLine, endLine }
            );
        }

        var firstStatement = statements.First();
        var lastStatement = statements.Last();

        ControlFlowAnalysis? controlFlow = null;
        try
        {
            controlFlow = semanticModel.AnalyzeControlFlow(firstStatement, lastStatement);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"Control flow analysis failed: {ex.Message}"
            );
        }

        if (controlFlow == null || !controlFlow.Succeeded)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Control flow analysis did not succeed"
            );
        }

        var entryPoints = controlFlow.EntryPoints.Select(ep =>
        {
            var lineSpan = ep.GetLocation().GetLineSpan();
            return new
            {
                line = lineSpan.StartLinePosition.Line,
                kind = ep.Kind().ToString()
            };
        }).ToList();

        var exitPoints = controlFlow.ExitPoints.Select(ep =>
        {
            var lineSpan = ep.GetLocation().GetLineSpan();
            return new
            {
                line = lineSpan.StartLinePosition.Line,
                kind = ep.Kind().ToString()
            };
        }).ToList();

        var returnStatements = controlFlow.ReturnStatements.Select(rs =>
        {
            var lineSpan = rs.GetLocation().GetLineSpan();
            return new
            {
                line = lineSpan.StartLinePosition.Line,
                text = rs.ToString().Trim()
            };
        }).ToList();

        return CreateSuccessResponse(
            data: new
            {
                region = new { startLine, endLine },
                succeeded = controlFlow.Succeeded,
                entryPoints,
                exitPoints,
                returnStatements,
                endPointIsReachable = controlFlow.EndPointIsReachable,
                startPointIsReachable = controlFlow.StartPointIsReachable
            },
            suggestedNextTools: new[]
            {
                "analyze_data_flow to analyze variable usage in the same region",
                "get_diagnostics to check for unreachable code warnings"
            }
        );
    }

    /// <summary>
    /// Gets a comprehensive overview of a type in a single call.
    /// Combines type info, base types summary, member counts, and Godot attributes.
    /// </summary>
    public async Task<object> GetTypeOverviewAsync(string typeName)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required"
            );
        }

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found",
                hint: "Try using fully-qualified name or use search_symbols"
            );
        }

        // Base type chain (first 3)
        var baseTypes = new List<string>();
        var currentBase = type.BaseType;
        var count = 0;
        while (currentBase != null && currentBase.SpecialType != SpecialType.System_Object && count < 3)
        {
            baseTypes.Add(currentBase.ToDisplayString());
            currentBase = currentBase.BaseType;
            count++;
        }

        // Member counts
        var members = type.GetMembers().Where(m => !m.IsImplicitlyDeclared).ToList();
        var memberSummary = new
        {
            methods = members.OfType<IMethodSymbol>().Count(m => m.MethodKind == MethodKind.Ordinary),
            properties = members.OfType<IPropertySymbol>().Count(),
            fields = members.OfType<IFieldSymbol>().Count(),
            events = members.OfType<IEventSymbol>().Count(),
            constructors = members.OfType<IMethodSymbol>().Count(m => m.MethodKind == MethodKind.Constructor)
        };

        // Godot attributes check
        var godotAttributes = new
        {
            hasExport = members.Any(m => HasAttribute(m, "Export")),
            hasSignal = members.Any(m => HasAttribute(m, "Signal")),
            hasTool = HasAttribute(type, "Tool"),
            hasGlobalClass = HasAttribute(type, "GlobalClass")
        };

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                simpleName = type.Name,
                typeKind = type.TypeKind.ToString(),
                @namespace = type.ContainingNamespace?.ToDisplayString(),
                isAbstract = type.IsAbstract,
                isSealed = type.IsSealed,
                isStatic = type.IsStatic,
                baseTypes,
                baseTypeCount = type.BaseType != null ? baseTypes.Count + (currentBase != null ? 1 : 0) : 0,
                interfaceCount = type.AllInterfaces.Length,
                memberSummary,
                godotAttributes,
                location = GetSymbolLocation(type)
            },
            suggestedNextTools: new[]
            {
                $"get_type_members for detailed member list",
                $"get_base_types for full inheritance chain",
                $"get_derived_types to find classes inheriting from {type.Name}",
                godotAttributes.hasExport ? "get_attributes('Export') to see exported properties" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    /// <summary>
    /// Gets comprehensive analysis of a method in a single call.
    /// Combines signature, callers, outgoing calls, and suggests data flow analysis.
    /// </summary>
    public async Task<object> AnalyzeMethodAsync(
        string typeName,
        string methodName,
        bool includeCallers = true,
        bool includeOutgoingCalls = false,
        int maxCallers = 20,
        int maxOutgoingCalls = 50)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName and methodName are required"
            );
        }

        var type = await FindTypeByNameAsync(typeName);
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found"
            );
        }

        var method = type.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (method == null)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found on type '{type.Name}'"
            );
        }

        // Get signature details
        var signature = new
        {
            name = method.Name,
            fullSignature = method.ToDisplayString(),
            returnType = method.ReturnType.ToDisplayString(),
            parameters = method.Parameters.Select(p => new
            {
                name = p.Name,
                type = p.Type.ToDisplayString(),
                isOptional = p.IsOptional
            }).ToList(),
            isAsync = method.IsAsync,
            isStatic = method.IsStatic,
            isVirtual = method.IsVirtual,
            accessibility = method.DeclaredAccessibility.ToString()
        };

        // Get callers if requested
        List<object>? callers = null;
        var totalCallers = 0;
        if (includeCallers)
        {
            var callerSymbols = await SymbolFinder.FindCallersAsync(method, _solution!);
            var callerList = callerSymbols.ToList();
            totalCallers = callerList.Count;

            callers = callerList
                .Take(maxCallers)
                .Select(c => new
                {
                    callingMethod = c.CallingSymbol.ToDisplayString(),
                    containingType = c.CallingSymbol.ContainingType?.Name,
                    locations = c.Locations.Select(l =>
                    {
                        var lineSpan = l.GetLineSpan();
                        return new
                        {
                            filePath = FormatPath(lineSpan.Path),
                            line = lineSpan.StartLinePosition.Line
                        };
                    }).Take(3).ToList()
                })
                .Cast<object>()
                .ToList();
        }

        // Get outgoing calls if requested
        List<object>? outgoingCalls = null;
        var totalOutgoingCalls = 0;
        if (includeOutgoingCalls)
        {
            var location = method.Locations.FirstOrDefault(l => l.IsInSource);
            if (location?.SourceTree != null)
            {
                var root = await location.SourceTree.GetRootAsync();
                var node = root.FindNode(location.SourceSpan);
                var methodDecl = node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();

                if (methodDecl != null)
                {
                    var document = _solution!.GetDocument(location.SourceTree);
                    var semanticModel = document != null ? await document.GetSemanticModelAsync() : null;

                    if (semanticModel != null)
                    {
                        var calls = new List<object>();
                        var visited = new HashSet<string>();

                        // Find method invocations
                        foreach (var invocation in methodDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                            if (symbolInfo.Symbol is IMethodSymbol calledMethod)
                            {
                                var key = calledMethod.ToDisplayString();
                                if (visited.Contains(key)) continue;
                                visited.Add(key);

                                var callLoc = calledMethod.Locations.FirstOrDefault(l => l.IsInSource);
                                calls.Add(new
                                {
                                    method = calledMethod.ToDisplayString(),
                                    shortName = $"{calledMethod.ContainingType?.Name}.{calledMethod.Name}",
                                    returnType = calledMethod.ReturnType.ToDisplayString(),
                                    isAsync = calledMethod.IsAsync,
                                    isExternal = callLoc == null
                                });
                            }
                        }

                        // Find property accesses
                        foreach (var access in methodDecl.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                        {
                            var symbolInfo = semanticModel.GetSymbolInfo(access);
                            if (symbolInfo.Symbol is IPropertySymbol prop)
                            {
                                var key = prop.ToDisplayString();
                                if (visited.Contains(key)) continue;
                                visited.Add(key);

                                var propLoc = prop.Locations.FirstOrDefault(l => l.IsInSource);
                                calls.Add(new
                                {
                                    method = prop.ToDisplayString(),
                                    shortName = $"{prop.ContainingType?.Name}.{prop.Name}",
                                    returnType = prop.Type.ToDisplayString(),
                                    isAsync = false,
                                    isProperty = true,
                                    isExternal = propLoc == null
                                });
                            }
                        }

                        totalOutgoingCalls = calls.Count;
                        outgoingCalls = calls.Take(maxOutgoingCalls).ToList();
                    }
                }
            }
        }

        return CreateSuccessResponse(
            data: new
            {
                signature,
                callers,
                totalCallers,
                callersShown = callers?.Count ?? 0,
                outgoingCalls,
                totalOutgoingCalls,
                outgoingCallsShown = outgoingCalls?.Count ?? 0,
                location = GetSymbolLocation(method),
                overloadCount = type.GetMembers(methodName).OfType<IMethodSymbol>().Count()
            },
            suggestedNextTools: new[]
            {
                method.Locations.FirstOrDefault()?.IsInSource == true
                    ? $"analyze_data_flow to analyze variable flow within the method"
                    : null,
                $"find_implementations if {methodName} is virtual/interface method"
            }.Where(s => s != null).ToArray()!
        );
    }

    /// <summary>
    /// Gets a comprehensive overview of a file.
    /// Combines diagnostics, type declarations, and structure.
    /// </summary>
    public async Task<object> GetFileOverviewAsync(string filePath)
    {
        EnsureSolutionLoaded();

        var document = await GetDocumentAsync(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File '{filePath}' not found in solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync();
        var syntaxTree = await document.GetSyntaxTreeAsync();

        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file"
            );
        }

        var root = await syntaxTree.GetRootAsync();

        // Get diagnostics
        var diagnostics = semanticModel.GetDiagnostics()
            .GroupBy(d => d.Severity)
            .ToDictionary(
                g => g.Key.ToString(),
                g => g.Count()
            );

        // Get type declarations
        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(td =>
            {
                var symbol = semanticModel.GetDeclaredSymbol(td);
                return new
                {
                    name = symbol?.Name ?? td.Identifier.Text,
                    kind = td.Kind().ToString().Replace("Declaration", ""),
                    line = td.GetLocation().GetLineSpan().StartLinePosition.Line,
                    memberCount = td.Members.Count
                };
            })
            .ToList();

        // Get using directives
        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString() ?? "")
            .Where(u => !string.IsNullOrEmpty(u))
            .ToList();

        // Get namespace
        var namespaceDecl = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();

        return CreateSuccessResponse(
            data: new
            {
                filePath = FormatPath(document.FilePath),
                projectName = document.Project.Name,
                @namespace = namespaceDecl?.Name.ToString(),
                diagnosticSummary = diagnostics,
                usingCount = usings.Count,
                typeDeclarations,
                lineCount = (await syntaxTree.GetTextAsync()).Lines.Count
            },
            suggestedNextTools: new[]
            {
                diagnostics.ContainsKey("Error") ? "get_diagnostics for detailed error info" : null,
                typeDeclarations.Count > 0 ? $"get_type_members for {typeDeclarations[0].name} to see members" : null
            }.Where(s => s != null).ToArray()!
        );
    }

}
