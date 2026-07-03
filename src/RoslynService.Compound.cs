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
        int endLine,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                hint: "Check for syntax errors with get_diagnostics"
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree"
            );
        }

        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var text = await syntaxTree.GetTextAsync(cancellationToken);

        // Find the node spanning the specified lines
        var startPosition = text.Lines[Math.Max(0, startLine)].Start;
        var endPosition = text.Lines[Math.Min(text.Lines.Count - 1, endLine)].End;
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        var node = root.FindNode(span);

        var (firstStatement, lastStatement) = ResolveAnalysisStatements(node, span);

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
        catch (OperationCanceledException)
        {
            throw; // a per-call timeout surfaces as the dispatcher's TIMEOUT, not AnalysisFailed
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
        int endLine,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model"
            );
        }

        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get syntax tree"
            );
        }

        var root = await syntaxTree.GetRootAsync(cancellationToken);
        var text = await syntaxTree.GetTextAsync(cancellationToken);

        var startPosition = text.Lines[Math.Max(0, startLine)].Start;
        var endPosition = text.Lines[Math.Min(text.Lines.Count - 1, endLine)].End;
        var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(startPosition, endPosition);

        var node = root.FindNode(span);

        var (firstStatement, lastStatement) = ResolveAnalysisStatements(node, span);

        if (firstStatement == null || lastStatement == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "No statements found in the specified region",
                context: new { startLine, endLine }
            );
        }

        ControlFlowAnalysis? controlFlow = null;
        try
        {
            controlFlow = semanticModel.AnalyzeControlFlow(firstStatement, lastStatement);
        }
        catch (OperationCanceledException)
        {
            throw; // a per-call timeout surfaces as the dispatcher's TIMEOUT, not AnalysisFailed
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
    /// Combines type info, base types summary, and member counts.
    /// </summary>
    public async Task<object> GetTypeOverviewAsync(string typeName, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName is required"
            );
        }

        var (type, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
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

        return CreateSuccessResponse(
            data: new
            {
                typeName = type.ToDisplayString(),
                simpleName = type.Name,
                typeKind = GetTypeKindString(type),
                @namespace = type.ContainingNamespace?.ToDisplayString(),
                isAbstract = type.IsAbstract,
                isSealed = type.IsSealed,
                isStatic = type.IsStatic,
                baseTypes,
                baseTypeCount = type.BaseType != null ? baseTypes.Count + (currentBase != null ? 1 : 0) : 0,
                interfaceCount = type.AllInterfaces.Length,
                memberSummary,
                location = GetSymbolLocation(type)
            },
            suggestedNextTools: new[]
            {
                "get_type_members for detailed member list",
                "get_base_types for full inheritance chain",
                $"get_derived_types to find classes inheriting from {type.Name}"
            }
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
        int maxOutgoingCalls = 50,
        int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                "typeName and methodName are required"
            );
        }

        var (type, ambiguousType) = await FindTypeByNameAsync(typeName, cancellationToken);
        if (ambiguousType != null) return ambiguousType;
        if (type == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Type '{typeName}' not found"
            );
        }

        var overloads = type.GetMembers(methodName).OfType<IMethodSymbol>().ToList();
        if (overloads.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.SymbolNotFound,
                $"Method '{methodName}' not found on type '{type.Name}'"
            );
        }

        IMethodSymbol method;
        if (overloadIndex.HasValue)
        {
            if (overloadIndex.Value < 0 || overloadIndex.Value >= overloads.Count)
            {
                return CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"overloadIndex {overloadIndex.Value} is out of range for '{methodName}' (0..{overloads.Count - 1})",
                    context: new { overloadIndex = overloadIndex.Value, overloadCount = overloads.Count });
            }
            method = overloads[overloadIndex.Value];
        }
        else if (overloads.Count > 1)
        {
            // Picking the first overload silently analyzes an arbitrary one. Surface
            // the ambiguity with every signature so the caller can choose by index.
            return CreateErrorResponse(
                ErrorCodes.AmbiguousMatch,
                $"'{methodName}' has {overloads.Count} overloads on '{type.Name}'; pass overloadIndex to choose one",
                hint: "overloadIndex is 0-based, matching the listed order",
                context: new
                {
                    overloads = overloads
                        .Select((o, i) => new { index = i, signature = o.ToDisplayString() })
                        .ToList()
                });
        }
        else
        {
            method = overloads[0];
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

        // Get callers if requested. This is a grouped view: one entry per distinct
        // calling symbol (with up to three sample locations), capped by maxCallers.
        // The count is therefore caller GROUPS, not call sites — named
        // totalCallerGroups so it never collides with find_callers.totalCallers,
        // which counts individual call sites (its list is one entry per site).
        List<object>? callers = null;
        var totalCallerGroups = 0;
        if (includeCallers)
        {
            var callerSymbols = await SymbolFinder.FindCallersAsync(method, _solution!, cancellationToken);

            // The generated scan is authoritative for callers inside generator output, which
            // SymbolFinder now also returns. Dedup the SymbolFinder groups against it by
            // display string (the two come from different compilations, so symbol identity
            // can't be compared) so a generated caller group is listed and counted once.
            var generatedGroups = (await ScanGeneratedReferencesAsync(method, cancellationToken))
                .Where(h => h.Kind is "invocation" or "read" or "write" && h.EnclosingSymbol is IMethodSymbol)
                .GroupBy(h => h.EnclosingSymbol!, SymbolEqualityComparer.Default)
                .ToList();
            var generatedCallerNames = new HashSet<string>(
                generatedGroups.Select(g => g.Key.ToDisplayString()), StringComparer.Ordinal);

            var callerList = callerSymbols
                .Where(c => !generatedCallerNames.Contains(c.CallingSymbol.ToDisplayString()))
                .ToList();
            totalCallerGroups = callerList.Count;

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

            totalCallerGroups += generatedGroups.Count;
            foreach (var group in generatedGroups)
            {
                if (callers.Count >= maxCallers) break;
                callers.Add(new
                {
                    callingMethod = group.Key.ToDisplayString(),
                    containingType = group.Key.ContainingType?.Name,
                    locations = group.Take(3).Select(h => new
                    {
                        filePath = FormatPath(h.HintPath),
                        line = h.Line
                    }).ToList(),
                    inGeneratedCode = true
                });
            }
        }

        // Get outgoing calls if requested — via the shared collector so this list
        // matches get_outgoing_calls/get_call_graph (symbol-equality dedup,
        // qualified + unqualified property references).
        List<object>? outgoingCalls = null;
        var totalOutgoingCalls = 0;
        if (includeOutgoingCalls)
        {
            var references = await CollectOutgoingReferencesAsync(method, includeProperties: true, cancellationToken);
            // Shared projection so analyze_method's outgoing calls match get_outgoing_calls
            // exactly (depth is null here — this is a single-hop list, not a depth walk).
            var calls = references.Select(reference => BuildOutgoingCall(reference, null)).ToList();

            totalOutgoingCalls = calls.Count;
            outgoingCalls = calls.Take(maxOutgoingCalls).ToList();
        }

        return CreateSuccessResponse(
            data: new
            {
                signature,
                callers,
                totalCallerGroups,
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
    public async Task<object> GetFileOverviewAsync(
        string filePath, int maxMembersPerType = 100, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        if (maxMembersPerType is < 1 or > 1000)
        {
            return CreateErrorResponse(
                ErrorCodes.InvalidParameter,
                $"maxMembersPerType must be between 1 and 1000 (got {maxMembersPerType})",
                context: new { maxMembersPerType });
        }

        Document document;
        try
        {
            document = await GetDocumentAsync(filePath);
        }
        catch (FileNotFoundException)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Check the file path or reload the solution",
                context: new { filePath }
            );
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);

        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze file"
            );
        }

        var root = await syntaxTree.GetRootAsync(cancellationToken);

        // Get diagnostics
        var diagnostics = semanticModel.GetDiagnostics()
            .GroupBy(d => d.Severity)
            .ToDictionary(
                g => g.Key.ToString(),
                g => g.Count()
            );

        // Per-member outline from THIS file's declaration only. A field/event
        // declaring several variables yields one entry per variable; everything
        // else maps one syntax member to one entry. Nested types appear here as
        // members AND, being TypeDeclarationSyntax, as their own top-level entry.
        IEnumerable<object> DescribeMembers(TypeDeclarationSyntax typeDecl)
        {
            foreach (var member in typeDecl.Members)
            {
                if (member is BaseFieldDeclarationSyntax field)
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        yield return DescribeMember(
                            semanticModel.GetDeclaredSymbol(variable, cancellationToken),
                            variable.Identifier.Text, member);
                    }
                }
                else
                {
                    yield return DescribeMember(
                        semanticModel.GetDeclaredSymbol(member, cancellationToken),
                        MemberFallbackName(member), member);
                }
            }
        }

        // Get type declarations
        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(td =>
            {
                var symbol = semanticModel.GetDeclaredSymbol(td, cancellationToken);
                var allMembers = DescribeMembers(td).ToList();
                return new
                {
                    name = symbol?.Name ?? td.Identifier.Text,
                    kind = td.Kind().ToString().Replace("Declaration", ""),
                    line = td.GetLocation().GetLineSpan().StartLinePosition.Line,
                    memberCount = td.Members.Count,
                    members = allMembers.Take(maxMembersPerType).ToList(),
                    membersTruncated = allMembers.Count > maxMembersPerType
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
                lineCount = (await syntaxTree.GetTextAsync(cancellationToken)).Lines.Count
            },
            suggestedNextTools: new[]
            {
                diagnostics.ContainsKey("Error") ? "get_diagnostics for detailed error info" : null,
                typeDeclarations.Count > 0 ? $"get_type_members for {typeDeclarations[0].name} to see members" : null
            }.Where(s => s != null).ToArray()!
        );
    }

    // One member-outline entry. Signature/accessibility come from the symbol
    // when bound; span lines come from the syntax so the entry is useful even
    // when the symbol fails to resolve.
    private static object DescribeMember(ISymbol? symbol, string fallbackName, MemberDeclarationSyntax memberSyntax)
    {
        var lineSpan = memberSyntax.GetLocation().GetLineSpan();
        return new
        {
            name = symbol?.Name ?? fallbackName,
            kind = symbol?.Kind.ToString() ?? memberSyntax.Kind().ToString().Replace("Declaration", ""),
            signature = symbol?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? fallbackName,
            accessibility = symbol?.DeclaredAccessibility.ToString() ?? "NotApplicable",
            startLine = lineSpan.StartLinePosition.Line,
            endLine = lineSpan.EndLinePosition.Line
        };
    }

    private static string MemberFallbackName(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        DelegateDeclarationSyntax d => d.Identifier.Text,
        EnumMemberDeclarationSyntax e => e.Identifier.Text,
        _ => member.Kind().ToString().Replace("Declaration", "")
    };

    // Roslyn's AnalyzeDataFlow/AnalyzeControlFlow(first, last) requires both statements to
    // be direct siblings in the same statement list, and the analyzed range must match the
    // selection: handing Roslyn the enclosing list's first/last statement silently widens
    // a sub-range selection to the whole block. FindNode returns the smallest node covering
    // the selection — a statement-list owner (block or switch section) when the selection
    // spans sibling statements, the statement itself for a one-statement selection, or a
    // node inside a statement when the selection covers part of a multi-line statement.
    // Clamp each case to exactly the statements the selection touches.
    private static (StatementSyntax? first, StatementSyntax? last) ResolveAnalysisStatements(
        SyntaxNode node, TextSpan selection)
    {
        if (node is BlockSyntax block)
        {
            return ClampToSelection(block.Statements, selection);
        }

        if (node is SwitchSectionSyntax section)
        {
            return ClampToSelection(section.Statements, selection);
        }

        if (node is StatementSyntax stmt)
        {
            return (stmt, stmt);
        }

        // The selection sits inside a single statement (e.g. one line of a multi-line
        // initializer) — analyze that statement, not its whole enclosing block.
        var enclosingStatement = node.FirstAncestorOrSelf<StatementSyntax>();
        if (enclosingStatement != null)
        {
            return (enclosingStatement, enclosingStatement);
        }

        return (null, null);
    }

    private static (StatementSyntax? first, StatementSyntax? last) ClampToSelection(
        SyntaxList<StatementSyntax> statements, TextSpan selection)
    {
        var touched = statements.Where(s => selection.IntersectsWith(s.Span)).ToList();
        if (touched.Count == 0) return (null, null);
        return (touched[0], touched[^1]);
    }
}
