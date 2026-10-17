using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// extract_method: moves a run of whole sibling statements into a new method and
// replaces them with a call. The new method's shape comes from Roslyn's analysis of the
// selected statements:
//   - control flow: control must not leave the selection other than by finishing it — a
//     return, break, continue or goto that leaves it, a yield, or a label jumped to from
//     outside is refused;
//   - data flow: a variable whose value flows in is a parameter; a variable assigned
//     inside whose value is read after flows back as the return value (a tuple for
//     several), and when it is declared outside and not always assigned inside it is
//     also passed in so its incoming value survives; an outside variable the selection
//     only uses after assigning it becomes a local of the new method; a local declared
//     inside and used after keeps a declaration at the call site;
//   - the method is static when its caller cannot supply `this` (a static member, a
//     static class, a static local function or lambda), and async (awaited at the call)
//     when the selection awaits — await expressions, await foreach and await using;
//   - the method keeps the containing method's type parameters (and their constraints)
//     that the selection uses.
// The generated method and call carry Formatter.Annotation, so the apply formats them
// with the document's options. Before anything is returned the edit is compiled; an
// extraction that would introduce a compile error is refused with the errors listed.
public partial class RoslynService
{
    private static readonly IReadOnlyList<string> ExtractAccessibilities = new[]
    {
        "private", "public", "protected", "internal", "protected internal", "private protected"
    };

    /// <summary>
    /// Extracts a code block into a new method.
    /// </summary>
    /// <remarks>
    /// Intentionally span-only (filePath + startLine/endLine), not name-addressable
    /// like the symbol-targeting refactorings. The unit of work is a statement RANGE,
    /// not a declared symbol; ResolveAddressingAsync resolves a name to a single
    /// declaration position and cannot express a selection span, so name addressing
    /// does not apply here.
    /// </remarks>
    public async Task<object> ExtractMethodAsync(
        string filePath,
        int startLine,
        int endLine,
        string methodName,
        string accessibility = "private",
        bool preview = true,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // Validate the new method's name and accessibility up front so preview and apply
        // agree.
        if (InvalidIdentifierError(methodName, "methodName") is { } nameError) return nameError;
        if (ParameterValidation.Choice("accessibility", accessibility, ExtractAccessibilities, out var canonicalAccessibility) is { } accessibilityError)
            return CreateErrorResponse(accessibilityError);
        accessibility = canonicalAccessibility ?? "private";

        var document = TryFindDocument(filePath);
        if (document == null)
        {
            return CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {filePath}",
                hint: "Ensure the file is part of a project in the loaded solution"
            );
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
        if (semanticModel == null || syntaxTree == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get semantic model",
                context: new { filePath = FormatPath(document.FilePath) }
            );
        }

        var text = await syntaxTree.GetTextAsync(cancellationToken);
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        // Validate the line range up front: indexing text.Lines out of range (or a
        // reversed range) would otherwise throw and surface as an InternalError.
        if (ValidateLineRange(text, startLine, endLine) is { } rangeError) return rangeError;
        var location = new { filePath = FormatPath(document.FilePath), startLine, endLine };

        object Refuse(string message, string? hint = null) => CreateErrorResponse(
            ErrorCodes.InvalidParameter, message, hint: hint, context: location);
        int LineOf(SyntaxNode node) => text.Lines.GetLineFromPosition(node.SpanStart).LineNumber;

        // The selection: from the first to the last non-whitespace character of the lines.
        var selection = TrimmedSelection(text, startLine, endLine);
        var contained = selection == null
            ? new List<StatementSyntax>()
            : root.DescendantNodes(selection.Value).OfType<StatementSyntax>().Where(s => selection.Value.Contains(s.Span)).ToList();
        var statements = contained
            .Where(s => !contained.Any(other => other != s && other.Span.Contains(s.Span)))
            .OrderBy(s => s.SpanStart)
            .ToList();
        if (statements.Count == 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "No statements found in selection",
                hint: "Select one or more complete statements to extract",
                context: new { startLine, endLine }
            );
        }

        // Whole sibling statements, and nothing else.
        if (statements.FirstOrDefault(s => s.Parent != statements[0].Parent) is { } stray)
        {
            return Refuse(
                $"The selection must cover complete statements of one block, but lines {LineOf(statements[0])} and {LineOf(stray)} belong to different blocks",
                "Select a run of whole statements that sit in the same block.");
        }
        var uncovered = root.DescendantTokens(selection!.Value)
            .FirstOrDefault(t => t.Span.Length > 0 && selection.Value.Contains(t.Span) && !statements.Any(s => s.Span.Contains(t.Span)));
        if (uncovered != default)
        {
            var cut = uncovered.Parent?.FirstAncestorOrSelf<StatementSyntax>();
            return cut == null
                ? Refuse($"The selection must cover complete statements, but it includes '{uncovered.Text}' (line {text.Lines.GetLineFromPosition(uncovered.SpanStart).LineNumber}), which is not part of one")
                : Refuse($"The selection cuts through the statement at lines {LineOf(cut)}-{text.Lines.GetLineFromPosition(cut.Span.End).LineNumber}; select it whole or only statements inside it");
        }
        if (statements[0].Parent is not BlockSyntax block)
        {
            return Refuse("The selected statements must be inside a block { }", "Select statements of a method body or another braced block.");
        }

        var anchor = statements[0].Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => m.Parent is TypeDeclarationSyntax);
        if (anchor == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Selection must be inside a method",
                context: new { startLine, endLine }
            );
        }

        var first = statements[0];
        var last = statements[^1];
        // The selection's own nodes, not those of lambdas or local functions inside it
        // (their returns and awaits stay inside them).
        var sameFunction = statements
            .SelectMany(s => s.DescendantNodesAndSelf(n => n is not AnonymousFunctionExpressionSyntax && n is not LocalFunctionStatementSyntax))
            .ToList();

        if (sameFunction.OfType<ReturnStatementSyntax>().Any())
        {
            return Refuse(
                "The selection contains a return statement",
                "Extract a smaller range that does not include the method's return, or restructure so the returned value is computed by the extracted code and returned by the caller.");
        }
        if (sameFunction.OfType<YieldStatementSyntax>().FirstOrDefault() is { } yieldStatement)
        {
            var keyword = yieldStatement.IsKind(SyntaxKind.YieldReturnStatement) ? "yield return" : "yield break";
            return Refuse($"The selection contains '{keyword}' (line {LineOf(yieldStatement)}); an iterator's yield cannot move into another method");
        }
        if (statements.Any(s => s.DescendantTrivia().Any(t => t.IsDirective && t.SpanStart >= selection.Value.Start)))
        {
            return Refuse("The selection contains preprocessor directives", "Extract statements without #if, #region or other directive lines.");
        }

        ControlFlowAnalysis? controlFlow;
        DataFlowAnalysis? dataFlow;
        try
        {
            controlFlow = semanticModel.AnalyzeControlFlow(first, last);
            dataFlow = semanticModel.AnalyzeDataFlow(first, last);
        }
        catch (ArgumentException)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not analyze the selected statements",
                hint: "Select a contiguous run of complete statements at the same block level.",
                context: new { startLine, endLine });
        }
        if (controlFlow == null || !controlFlow.Succeeded || dataFlow == null || !dataFlow.Succeeded)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Flow analysis of the selected statements failed",
                hint: "The selection may contain unsupported constructs",
                context: new { startLine, endLine }
            );
        }

        if (controlFlow.ExitPoints.OrderBy(e => e.SpanStart).FirstOrDefault(e => e is not ReturnStatementSyntax) is { } exit)
        {
            var keyword = exit switch
            {
                BreakStatementSyntax => "break",
                ContinueStatementSyntax => "continue",
                _ => "goto"
            };
            return Refuse($"The selection contains '{keyword}' (line {LineOf(exit)}), which transfers control out of the selection");
        }
        if (controlFlow.EntryPoints.FirstOrDefault() is LabeledStatementSyntax entry)
        {
            return Refuse($"The selection contains the label '{entry.Identifier.Text}' (line {LineOf(entry)}), which code outside the selection jumps to");
        }

        static bool IsVariable(ISymbol s) => s is ILocalSymbol || s is IParameterSymbol { IsThis: false };
        if (dataFlow.UnsafeAddressTaken.FirstOrDefault() is { } addressTaken)
            return Refuse($"The selection takes the address of '{addressTaken.Name}', which cannot move into another method");
        if (dataFlow.WrittenInside.Intersect(dataFlow.CapturedOutside, SymbolEqualityComparer.Default).FirstOrDefault() is { } captured)
            return Refuse($"The selection assigns '{captured.Name}', which a lambda or local function outside the selection captures");

        var declaredInside = dataFlow.VariablesDeclared.ToHashSet(SymbolEqualityComparer.Default);
        var alwaysAssigned = dataFlow.AlwaysAssigned.ToHashSet(SymbolEqualityComparer.Default);
        var flowsIn = dataFlow.DataFlowsIn.Where(IsVariable).ToHashSet(SymbolEqualityComparer.Default);
        var returns = OrderByDeclaration(dataFlow.DataFlowsOut.Where(IsVariable));
        var parameters = OrderByDeclaration(flowsIn.Concat(returns.Where(s => !declaredInside.Contains(s) && !alwaysAssigned.Contains(s))));
        var newLocals = OrderByDeclaration(dataFlow.ReadInside.Concat(dataFlow.WrittenInside)
            .Where(s => IsVariable(s) && !declaredInside.Contains(s) && !parameters.Contains(s, SymbolEqualityComparer.Default)));
        var hoisted = OrderByDeclaration(declaredInside
            .Where(s => (dataFlow.ReadOutside.Contains(s, SymbolEqualityComparer.Default) || dataFlow.WrittenOutside.Contains(s, SymbolEqualityComparer.Default))
                     && !returns.Contains(s, SymbolEqualityComparer.Default)));

        static ITypeSymbol TypeOf(ISymbol s) => s is ILocalSymbol local ? local.Type : ((IParameterSymbol)s).Type;
        if (parameters.Concat(returns).Concat(newLocals).Concat(hoisted).FirstOrDefault(s => s is ILocalSymbol { IsRef: true }) is { } refLocal)
            return Refuse($"The selection uses the ref local '{refLocal.Name}', which cannot pass to another method by value");
        if (parameters.Concat(returns).Concat(newLocals).Concat(hoisted)
                .FirstOrDefault(s => TypeOf(s).TypeKind == TypeKind.Error || ContainsAnonymousType(TypeOf(s))) is { } unnamed)
            return Refuse($"The selection uses '{unnamed.Name}', whose type cannot be written in a method signature");
        if (returns.Count > 1 && returns.FirstOrDefault(s => TypeOf(s).IsRefLikeType) is { } refLike)
            return Refuse($"'{refLike.Name}' flows out of the selection with other values, but its ref struct type cannot be returned in a tuple");

        var containsAwait = sameFunction.Any(n => n switch
        {
            AwaitExpressionSyntax => true,
            CommonForEachStatementSyntax forEach => forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
            UsingStatementSyntax usingStatement => usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
            LocalDeclarationStatementSyntax declaration => declaration.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword),
            _ => false
        });
        if (containsAwait && parameters.Concat(returns).FirstOrDefault(s => TypeOf(s).IsRefLikeType) is { } asyncRefLike)
            return Refuse($"The selection awaits and uses '{asyncRefLike.Name}' of a ref struct type, which an async method cannot take or return");

        var anchorSymbol = semanticModel.GetDeclaredSymbol(anchor, cancellationToken);
        var isStatic = anchorSymbol?.IsStatic == true
            || anchorSymbol?.ContainingType?.IsStatic == true
            || first.Ancestors().TakeWhile(a => a != anchor).Any(a =>
                a is LocalFunctionStatementSyntax localFunction && localFunction.Modifiers.Any(SyntaxKind.StaticKeyword)
                || a is AnonymousFunctionExpressionSyntax lambda && lambda.Modifiers.Any(SyntaxKind.StaticKeyword));

        // Types are spelled as they bind where the new method goes.
        var typePosition = anchor.SpanStart;
        string TypeText(ITypeSymbol type) => type.ToMinimalDisplayString(semanticModel, typePosition);

        var neededTypeParams = CollectReferencedMethodTypeParameters(
            anchorSymbol as IMethodSymbol, sameFunction, dataFlow, returns, semanticModel, cancellationToken);
        var constraintClauses = anchor is MethodDeclarationSyntax anchorMethod
            ? anchorMethod.ConstraintClauses.Where(c => neededTypeParams.Any(tp => tp.Name == c.Name.Identifier.ValueText)).ToList()
            : new List<TypeParameterConstraintClauseSyntax>();

        var returnType = returns.Count switch
        {
            0 => "void",
            1 => TypeText(TypeOf(returns[0])),
            _ => $"({string.Join(", ", returns.Select(r => $"{TypeText(TypeOf(r))} {r.Name}"))})"
        };
        var declaredReturnType = returnType;
        if (containsAwait)
        {
            var task = semanticModel.Compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var taskText = task == null ? "System.Threading.Tasks.Task" : TypeText(task);
            declaredReturnType = returns.Count == 0 ? taskText : $"{taskText}<{returnType}>";
        }

        // The new method.
        var modifiers = accessibility.Split(' ').Select(word => SyntaxFactory.Token(SyntaxFacts.GetKeywordKind(word))).ToList();
        if (isStatic) modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        if (containsAwait) modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        // The first statement's leading trivia on lines before the selection (a comment
        // the selection does not include) stays at the call site.
        var firstLeading = first.GetLeadingTrivia();
        var callSiteLeading = SyntaxFactory.TriviaList(firstLeading.Where(t => t.SpanStart < text.Lines[startLine].Start));
        var movedLeading = SyntaxFactory.TriviaList(firstLeading.Where(t => t.SpanStart >= text.Lines[startLine].Start));
        var body = new List<StatementSyntax>();
        body.AddRange(newLocals.Select(s => (StatementSyntax)SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(TypeText(TypeOf(s))),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(VariableIdentifier(s.Name)))))));
        body.AddRange(statements.Select(s => s == first ? s.WithLeadingTrivia(movedLeading) : s));
        if (returns.Count == 1)
        {
            body.Add(SyntaxFactory.ReturnStatement(EscapedName(returns[0].Name)));
        }
        else if (returns.Count > 1)
        {
            body.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(
                returns.Select(r => SyntaxFactory.Argument(EscapedName(r.Name)))))));
        }

        var methodMarker = new SyntaxAnnotation("SharpLens.ExtractMethod.Method");
        var newMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(declaredReturnType), VariableIdentifier(methodName))
            .WithModifiers(SyntaxFactory.TokenList(modifiers))
            .WithTypeParameterList(neededTypeParams.Count > 0
                ? SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(neededTypeParams.Select(tp => SyntaxFactory.TypeParameter(tp.Name))))
                : null)
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters.Select(s =>
                SyntaxFactory.Parameter(VariableIdentifier(s.Name)).WithType(SyntaxFactory.ParseTypeName(TypeText(TypeOf(s))))))))
            .WithConstraintClauses(SyntaxFactory.List(constraintClauses.Select(c => c.WithoutTrivia())))
            .WithBody(SyntaxFactory.Block(body))
            .WithAdditionalAnnotations(Formatter.Annotation, methodMarker);

        // The call that replaces the statements.
        ExpressionSyntax call = SyntaxFactory.InvocationExpression(
            neededTypeParams.Count > 0
                ? SyntaxFactory.GenericName(VariableIdentifier(methodName), SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SeparatedList<TypeSyntax>(neededTypeParams.Select(tp => SyntaxFactory.IdentifierName(tp.Name)))))
                : SyntaxFactory.IdentifierName(VariableIdentifier(methodName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(parameters.Select(s => SyntaxFactory.Argument(EscapedName(s.Name))))));
        if (containsAwait) call = SyntaxFactory.AwaitExpression(call);

        StatementSyntax callStatement;
        if (returns.Count == 0)
        {
            callStatement = SyntaxFactory.ExpressionStatement(call);
        }
        else if (returns.Count == 1)
        {
            callStatement = declaredInside.Contains(returns[0])
                ? SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(VariableIdentifier(returns[0].Name))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(call)))))
                : SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, EscapedName(returns[0].Name), call));
        }
        else
        {
            var targets = SyntaxFactory.TupleExpression(SyntaxFactory.SeparatedList(returns.Select(r => SyntaxFactory.Argument(
                declaredInside.Contains(r)
                    ? SyntaxFactory.DeclarationExpression(SyntaxFactory.IdentifierName("var"),
                        SyntaxFactory.SingleVariableDesignation(VariableIdentifier(r.Name)))
                    : EscapedName(r.Name)))));
            callStatement = SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, targets, call));
        }

        var callMarker = new SyntaxAnnotation("SharpLens.ExtractMethod.Call");
        var callStatements = hoisted
            .Select(s => (StatementSyntax)SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ParseTypeName(TypeText(TypeOf(s))),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(VariableIdentifier(s.Name))))))
            .Append(callStatement)
            .Select(s => s.WithAdditionalAnnotations(Formatter.Annotation, callMarker))
            .ToList();
        callStatements[0] = callStatements[0].WithLeadingTrivia(callSiteLeading);
        callStatements[^1] = callStatements[^1].WithTrailingTrivia(last.GetTrailingTrivia());

        var firstIndex = block.Statements.IndexOf(first);
        var editor = new SyntaxEditor(root, document.Project.Solution.Services);
        editor.InsertAfter(anchor, newMethod);
        editor.ReplaceNode(block, (current, _) =>
        {
            var currentBlock = (BlockSyntax)current;
            var kept = currentBlock.Statements.ToList();
            kept.RemoveRange(firstIndex, statements.Count);
            kept.InsertRange(firstIndex, callStatements);
            return currentBlock.WithStatements(SyntaxFactory.List(kept));
        });
        var newSolution = _solution!.WithDocumentSyntaxRoot(document.Id, editor.GetChangedRoot());

        // The edit must compile: an extraction that introduces an error is refused.
        var (prepared, prepareError) = await PrepareForApplyAsync(newSolution, cancellationToken);
        if (prepared == null) return prepareError!;
        var introduced = await IntroducedCompileErrorsAsync(
            prepared, await ComputeFileChangesAsync(prepared, cancellationToken), cancellationToken);
        if (introduced.Count > 0)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"The extraction would not compile: {introduced[0]}",
                hint: "Adjust the selection; nothing was written.",
                context: new { location.filePath, startLine, endLine, errors = introduced });
        }

        // The method and call exactly as they will be written.
        var formatted = await Formatter.FormatAsync(newSolution.GetDocument(document.Id)!, Formatter.Annotation, cancellationToken: cancellationToken);
        var formattedRoot = (await formatted.GetSyntaxRootAsync(cancellationToken))!;
        var formattedText = await formatted.GetTextAsync(cancellationToken);
        var lineBreak = LineEndings.UniformStyle(text) ?? LineEndings.DominantStyle(text) ?? "\n";
        var methodNode = (MethodDeclarationSyntax)formattedRoot.GetAnnotatedNodes(methodMarker).Single();
        var callNodes = formattedRoot.GetAnnotatedNodes(callMarker).ToList();
        var extractedCode = Dedent(formattedText, methodNode.Span, lineBreak);
        var replacementCode = Dedent(formattedText, TextSpan.FromBounds(callNodes[0].SpanStart, callNodes[^1].Span.End), lineBreak);
        var signature = System.Text.RegularExpressions.Regex.Replace(
            formattedText.ToString(TextSpan.FromBounds(methodNode.SpanStart, methodNode.Body!.SpanStart)).Trim(), @"\s+", " ");

        var parameterReport = parameters.Select(s => new
        {
            name = s.Name,
            type = TypeText(TypeOf(s)),
            reason = flowsIn.Contains(s)
                ? "read inside selection"
                : "assigned inside selection and used after it; its incoming value is kept when the selection does not assign it"
        }).ToList();
        var returnVariable = returns.Count == 1 ? returns[0].Name : null;
        var returnReason = returns.Count switch
        {
            0 => null,
            1 => $"variable '{returns[0].Name}' assigned inside and used after selection",
            _ => "several variables are assigned inside and used after the selection; they return as a tuple"
        };

        if (preview)
        {
            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    methodName,
                    signature,
                    parameters = parameterReport,
                    returnType,
                    returnVariable,
                    returnReason,
                    statementsExtracted = statements.Count,
                    extractedCode,
                    replacementCode,
                    location
                },
                suggestedNextTools: new[]
                {
                    "Call again with preview: false to apply the extraction"
                }
            );
        }

        var (applied, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!applied) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName,
                signature,
                parameters = parameterReport,
                returnType,
                returnVariable,
                statementsExtracted = statements.Count,
                extractedCode,
                replacementCode,
                changedFiles = FormatAppliedChanges(applyReport),
                location,
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics to verify no new errors",
                $"find_references on {methodName} to confirm the extracted method is called"
            }
        );
    }

    // The selection from the first to the last non-whitespace character of the line
    // range, or null when the lines are blank.
    private static TextSpan? TrimmedSelection(SourceText text, int startLine, int endLine)
    {
        var start = text.Lines[startLine].Start;
        var end = text.Lines[endLine].End;
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return end > start ? TextSpan.FromBounds(start, end) : null;
    }

    // Variables ordered by where they are declared.
    private static List<ISymbol> OrderByDeclaration(IEnumerable<ISymbol> symbols) =>
        symbols.Distinct(SymbolEqualityComparer.Default)
            .OrderBy(s => s.Locations.FirstOrDefault()?.SourceSpan.Start ?? int.MaxValue)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    private static bool ContainsAnonymousType(ITypeSymbol type) => type switch
    {
        { IsAnonymousType: true } => true,
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsAnonymousType),
        IArrayTypeSymbol array => ContainsAnonymousType(array.ElementType),
        _ => false
    };

    // An identifier token for a symbol's name, verbatim (@name) when the name is a keyword.
    private static SyntaxToken VariableIdentifier(string name) => EscapedName(name).Identifier;

    // The text of a span's lines, from the span's start, with the span's own column of
    // indentation removed from the following lines, joined by the given line break.
    private static string Dedent(SourceText text, TextSpan span, string lineBreak)
    {
        var column = span.Start - text.Lines.GetLineFromPosition(span.Start).Start;
        var lines = text.ToString(span).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join(lineBreak, lines.Select((l, i) =>
        {
            if (i == 0) return l;
            var indent = l.Length - l.TrimStart(' ', '\t').Length;
            return l.Substring(Math.Min(indent, column));
        }));
    }

    // The containing method's generic type parameters that the selection actually
    // uses — via flowing-in/out variable types or any type mentioned in the body.
    // Returned in declaration order. The enclosing TYPE's parameters are excluded
    // (they stay in scope for the sibling method and must not be re-declared).
    private static List<ITypeParameterSymbol> CollectReferencedMethodTypeParameters(
        IMethodSymbol? methodSymbol,
        List<SyntaxNode> selectionNodes,
        DataFlowAnalysis dataFlow,
        List<ISymbol> returnCandidates,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (methodSymbol == null || methodSymbol.TypeParameters.Length == 0)
            return new List<ITypeParameterSymbol>();

        var methodTps = new HashSet<ITypeParameterSymbol>(methodSymbol.TypeParameters, SymbolEqualityComparer.Default);
        var used = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);

        void Collect(ITypeSymbol? type)
        {
            switch (type)
            {
                case ITypeParameterSymbol tp when methodTps.Contains(tp):
                    used.Add(tp);
                    break;
                case INamedTypeSymbol named:
                    foreach (var arg in named.TypeArguments) Collect(arg);
                    break;
                case IArrayTypeSymbol arr:
                    Collect(arr.ElementType);
                    break;
                case IPointerTypeSymbol ptr:
                    Collect(ptr.PointedAtType);
                    break;
            }
        }

        foreach (var s in dataFlow.DataFlowsIn)
            Collect((s as ILocalSymbol)?.Type ?? (s as IParameterSymbol)?.Type);
        foreach (var s in returnCandidates)
            Collect((s as ILocalSymbol)?.Type ?? (s as IParameterSymbol)?.Type);
        foreach (var node in selectionNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Collect(semanticModel.GetTypeInfo(node, cancellationToken).Type);
            if (semanticModel.GetSymbolInfo(node, cancellationToken).Symbol is ITypeParameterSymbol tps
                && methodTps.Contains(tps))
            {
                used.Add(tps);
            }
        }

        return methodSymbol.TypeParameters.Where(used.Contains).ToList();
    }
}
