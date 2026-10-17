using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// change_signature: add, remove, rename and reorder the parameters of a method,
// constructor or local function, and update everything that depends on the parameter
// list. Built on Roslyn's symbol machinery:
//   - the request is validated first (ValidateSignatureChanges): every action, name,
//     type, default value and position, and the order rules C# enforces;
//   - the members that must change together are the method plus, transitively, the
//     member it overrides, its overrides, the interface members it implements and their
//     implementations (all in source; a member tied to metadata cannot change);
//   - renames run first, through Roslyn's rename engine with its conflict check, on the
//     parameter of every related member that shares the old name, so the body, nameof,
//     <param> tags and named arguments all follow;
//   - then each related declaration's parameter list is rebuilt from its own parameter
//     syntax (attributes, modifiers, type spelling, default text and layout kept), and
//     every reference SymbolFinder reports is classified: a call (invocation, object
//     creation, constructor initializer, primary-constructor base, attribute) has its
//     argument list rebuilt from the bound argument-to-parameter mapping; a cref's
//     parameter list is rebuilt; nameof needs nothing; anything else (a method group,
//     an implicit call) cannot be updated by an argument edit and is reported in
//     unrewrittenReferences.
// Argument rewriting: arguments stay positional while every earlier parameter still has
// an argument at its position; from the first parameter that is skipped (its default
// applies) or passed by name, the remaining arguments are named and keep the order they
// were written in (so their evaluation order does not change), with added arguments
// last. Positional arguments are reordered with their parameters. An expanded params
// list is kept as written and an empty one stays empty.
public partial class RoslynService
{
    private static readonly IReadOnlyList<string> SignatureActions = new[] { "add", "remove", "rename", "reorder" };

    private const string SignatureCallSiteAnnotationKind = "SharpLens.ChangeSignature.CallSite";

    private const string MethodGroupReason = "method group: a delegate conversion of the method, which no argument edit can update";

    /// <summary>
    /// Changes a method/constructor/local-function signature and updates all call sites.
    /// Supports add, remove, rename, and reorder parameter operations.
    /// </summary>
    public async Task<object> ChangeSignatureAsync(
        string? filePath,
        int? line,
        int? column,
        List<SignatureChange> changes,
        bool preview = true,
        string? typeName = null,
        string? memberName = null,
        int? overloadIndex = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        var (address, addressError) = await ResolveAddressingAsync(
            filePath, line, column, typeName, memberName, overloadIndex,
            memberNameRequired: true, cancellationToken: cancellationToken);
        if (address == null) return addressError!;

        var (resolved, posError) = await ResolvePositionAsync(
            address.FilePath, address.Line, address.Column, cancellationToken);
        if (resolved == null) return posError!;

        // Accept any of the three declaration kinds: instance/static method, constructor, local function.
        var declNode = resolved.Token.Parent?.AncestorsAndSelf()
            .FirstOrDefault(n => n is MethodDeclarationSyntax
                              || n is ConstructorDeclarationSyntax
                              || n is LocalFunctionStatementSyntax);

        if (declNode == null)
        {
            return CreateErrorResponse(
                ErrorCodes.NotAMethod,
                "No method, constructor, or local function found at position",
                hint: "Position cursor on a method/constructor/local-function declaration",
                context: new { line = address.Line, column = address.Column }
            );
        }

        if (resolved.Model.GetDeclaredSymbol(declNode, cancellationToken) is not IMethodSymbol methodSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not resolve declaration to an IMethodSymbol",
                context: new { declarationKind = declNode.GetType().Name }
            );
        }

        var parameterList = SignatureParameterList(declNode)!;
        var (plan, planError) = ValidateSignatureChanges(methodSymbol, parameterList, changes);
        if (plan == null) return planError!;

        var (related, relatedError) = await RelatedSignatureMembersAsync(methodSymbol, cancellationToken);
        if (related == null) return relatedError!;

        // 1. Renames, each through the rename engine on every related member's parameter
        //    that has the old name.
        var working = _solution!;
        foreach (var (ordinal, newName) in plan.Renames.OrderBy(r => r.Key))
        {
            var oldName = methodSymbol.Parameters[ordinal].Name;
            foreach (var member in related.Where(m => m.Parameters.Length > ordinal && m.Parameters[ordinal].Name == oldName))
            {
                if (await FindInSolutionAsync(working, member, cancellationToken) is not IMethodSymbol current)
                {
                    return CreateErrorResponse(
                        ErrorCodes.AnalysisFailed,
                        $"Could not find '{member.ToDisplayString()}' while renaming its parameter '{oldName}'");
                }
                var (renamed, conflicts, refusal) = await RenameConflictResolution.RenameAsync(
                    working, current.Parameters[ordinal], newName, new SymbolRenameOptions(), cancellationToken);
                if (renamed == null)
                {
                    return CreateErrorResponse(
                        ErrorCodes.InvalidParameter,
                        $"Cannot rename parameter '{oldName}' to '{newName}': {refusal}");
                }
                if (conflicts.Count > 0)
                {
                    var described = await DescribeRenameConflictsAsync(working, conflicts, cancellationToken);
                    return CreateErrorResponse(
                        ErrorCodes.InvalidParameter,
                        $"Renaming parameter '{oldName}' to '{newName}' leaves {described.Count} unresolved conflicts; nothing was written",
                        hint: "Choose a parameter name that no local, parameter or member in scope already uses.",
                        context: new { parameter = oldName, newName, conflicts = described });
                }
                working = renamed;
            }
        }

        // 2. Declarations and references, against the renamed solution.
        var currentRelated = new List<IMethodSymbol>();
        foreach (var member in related)
        {
            if (await FindInSolutionAsync(working, member, cancellationToken) is not IMethodSymbol current)
            {
                return CreateErrorResponse(
                    ErrorCodes.AnalysisFailed,
                    $"Could not find '{member.ToDisplayString()}' after renaming its parameters");
            }
            currentRelated.Add(current);
        }

        var editors = new Dictionary<DocumentId, SyntaxEditor>();
        async Task<SyntaxEditor> EditorAsync(DocumentId documentId)
        {
            if (editors.TryGetValue(documentId, out var existing)) return existing;
            var root = await working.GetDocument(documentId)!.GetSyntaxRootAsync(cancellationToken);
            var editor = new SyntaxEditor(root!, working.Services);
            editors[documentId] = editor;
            return editor;
        }

        var filesAffected = new List<string>();
        void AffectsFile(string? path)
        {
            if (path == null) return;
            var formatted = FormatPath(path);
            if (!filesAffected.Contains(formatted)) filesAffected.Add(formatted);
        }

        // Declarations: each related member's parameter list, in every document that
        // shares its file (one per target framework of a multi-targeted project).
        ParameterListSyntax? newTargetList = null;
        foreach (var member in currentRelated)
        {
            var explicitImplementation = member.ExplicitInterfaceImplementations.Length > 0;
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                if (SignatureParameterList(await reference.GetSyntaxAsync(cancellationToken)) is not { } list) continue;
                var path = reference.SyntaxTree.FilePath;
                AffectsFile(path);
                if (ReferenceEquals(member, currentRelated[0]) && newTargetList == null)
                    newTargetList = RebuildParameterList(list, plan, explicitImplementation);
                foreach (var documentId in working.GetDocumentIdsWithFilePath(path))
                {
                    var editor = await EditorAsync(documentId);
                    if (editor.OriginalRoot.FindNode(list.Span, getInnermostNodeForTie: true) is not ParameterListSyntax here) continue;
                    editor.ReplaceNode(here, (current, _) =>
                        RebuildParameterList((ParameterListSyntax)current, plan, explicitImplementation));
                }
            }
        }

        // References.
        var seenReferences = new HashSet<(DocumentId, TextSpan)>();
        var sites = new Dictionary<(string path, int start), SignatureCallSiteRecord>();
        var unrewritten = new Dictionary<(string path, int start), (string path, int line, int column, string reason)>();
        foreach (var member in currentRelated)
        {
            foreach (var referenced in await SymbolFinder.FindReferencesAsync(member, working, cancellationToken))
            {
                foreach (var location in referenced.Locations)
                {
                    var span = location.Location.SourceSpan;
                    var document = location.Document;
                    if (document.FilePath == null || !seenReferences.Add((document.Id, span))) continue;
                    var path = FormatPath(document.FilePath);
                    var lineSpan = location.Location.GetLineSpan();
                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    var (call, cref, reason) = ClassifySignatureReference(root!, span, location.IsImplicit, member);

                    if (reason != null)
                    {
                        unrewritten.TryAdd((path, span.Start),
                            (path, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character, reason));
                        continue;
                    }

                    if (cref != null)
                    {
                        if (cref.Parameters.Count != member.Parameters.Length) continue;
                        AffectsFile(document.FilePath);
                        var crefEditor = await EditorAsync(document.Id);
                        crefEditor.ReplaceNode(cref, (current, _) => RebuildCrefParameters((CrefParameterListSyntax)current, plan));
                        continue;
                    }
                    if (call == null) continue;

                    var model = await document.GetSemanticModelAsync(cancellationToken);
                    var bound = BindSignatureCall(model!, call, currentRelated, cancellationToken);
                    if (bound == null)
                    {
                        unrewritten.TryAdd((path, span.Start),
                            (path, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character,
                             "the call does not bind to the method, so its arguments cannot be mapped"));
                        continue;
                    }

                    var (target, arguments) = bound.Value;
                    var items = PlanCallArguments(plan, target, arguments, model!, call);
                    var unchanged = IsIdentityPlan(items, ArgumentNodesOf(call).Count);
                    var renamedNamedArgument = arguments.Any(a =>
                        a.Value.Kind == BoundArgumentKind.Explicit && a.Value.IsNamed && plan.Renames.ContainsKey(a.Key));
                    var before = ArgumentListText(call);
                    SyntaxAnnotation? annotation = null;
                    if (!unchanged)
                    {
                        annotation = new SyntaxAnnotation(SignatureCallSiteAnnotationKind);
                        var editor = await EditorAsync(document.Id);
                        var marker = annotation;
                        editor.ReplaceNode(call, (current, _) =>
                            WithRebuiltArguments(current, items).WithAdditionalAnnotations(marker));
                    }
                    AffectsFile(document.FilePath);

                    var key = (path, span.Start);
                    if (sites.TryGetValue(key, out var existing))
                    {
                        sites[key] = existing with { Changed = existing.Changed || !unchanged || renamedNamedArgument };
                    }
                    else
                    {
                        sites[key] = new SignatureCallSiteRecord(
                            path, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character,
                            document.Id, before, annotation, !unchanged || renamedNamedArgument);
                    }
                }
            }
        }

        var newSolution = working;
        foreach (var (documentId, editor) in editors)
        {
            newSolution = newSolution.WithDocumentSyntaxRoot(documentId, editor.GetChangedRoot());
        }

        var orderedSites = sites.Values
            .OrderBy(s => s.FilePath, StringComparer.Ordinal).ThenBy(s => s.Line).ThenBy(s => s.Column)
            .ToList();
        var unrewrittenReferences = unrewritten.Values
            .OrderBy(u => u.path, StringComparer.Ordinal).ThenBy(u => u.line).ThenBy(u => u.column)
            .Select(u => (object)new { filePath = u.path, line = u.line, column = u.column, reason = u.reason })
            .ToList();
        var updatedDeclarations = related.Select(m => m.ToDisplayString()).ToList();
        var oldSignature = RenderSignature(methodSymbol, parameterList.Parameters);
        var newSignature = RenderSignature(methodSymbol, newTargetList!.Parameters);
        var notice = address.TypeResolution?.ShadowNotice;

        if (preview)
        {
            var transformedCallSites = new List<object>();
            foreach (var site in orderedSites.Take(20))
            {
                var after = site.Before;
                if (site.After != null)
                {
                    var formatted = await Formatter.FormatAsync(newSolution.GetDocument(site.DocumentId)!, Formatter.Annotation,
                        cancellationToken: cancellationToken);
                    var node = (await formatted.GetSyntaxRootAsync(cancellationToken))!.GetAnnotatedNodes(site.After).FirstOrDefault();
                    if (node != null) after = ArgumentListText(node);
                }
                transformedCallSites.Add(new { filePath = site.FilePath, line = site.Line, before = site.Before, after });
            }

            return CreateSuccessResponse(
                data: new
                {
                    preview = true,
                    methodName = methodSymbol.Name,
                    containingType = methodSymbol.ContainingType?.ToDisplayString(),
                    oldSignature,
                    newSignature,
                    oldParameters = DescribeParameters(methodSymbol, parameterList.Parameters),
                    newParameters = newTargetList.Parameters.Select(p => new
                    {
                        name = p.Identifier.ValueText,
                        type = p.Type?.ToString(),
                        defaultValue = p.Default?.Value.ToString()
                    }).ToList(),
                    callSitesCount = orderedSites.Count,
                    callSites = orderedSites.Take(20).Select(s => new { filePath = s.FilePath, line = s.Line, column = s.Column }).ToList(),
                    transformedCallSites,
                    hasMoreCallSites = orderedSites.Count > 20,
                    updatedDeclarations,
                    unrewrittenReferences,
                    filesAffected,
                    notice
                },
                suggestedNextTools: unrewrittenReferences.Count > 0
                    ? new[]
                    {
                        "Some references are not calls (see unrewrittenReferences) and are not updated: change them by hand after applying",
                        "Call again with preview: false to apply changes"
                    }
                    : new[]
                    {
                        "Call again with preview: false to apply changes",
                        "Review the call sites before applying"
                    }
            );
        }

        var (ok, applyError, applyReport, introducedErrors) = await ApplyAndReportAsync(newSolution, cancellationToken);
        if (!ok) return applyError!;

        return CreateSuccessResponse(
            data: new
            {
                applied = true,
                methodName = methodSymbol.Name,
                containingType = methodSymbol.ContainingType?.ToDisplayString(),
                oldSignature,
                newSignature,
                callSitesUpdated = orderedSites.Count(s => s.Changed),
                updatedDeclarations,
                unrewrittenReferences,
                changedFiles = FormatAppliedChanges(applyReport),
                postApplyErrors = introducedErrors.Count > 0 ? introducedErrors : null,
                notice
            },
            suggestedNextTools: new[]
            {
                "get_diagnostics to verify no errors after the rewrite",
                "find_references to verify all call sites were updated"
            }
        );
    }

    // The parameter list of a method, constructor or local function declaration.
    private static ParameterListSyntax? SignatureParameterList(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.ParameterList,
        ConstructorDeclarationSyntax c => c.ParameterList,
        LocalFunctionStatementSyntax lf => lf.ParameterList,
        _ => null
    };

    // Validates every change against the method's parameters and C#'s parameter-order
    // rules, returning the new parameter list as slots and the renames, or the
    // INVALID_PARAMETER error naming the offending change. Names in remove, rename and
    // reorder are the method's current parameter names (reorder also accepts a name a
    // rename introduces). An add's position indexes the parameter list as the earlier
    // changes leave it; -1 or no position appends.
    private (SignaturePlan? plan, object? error) ValidateSignatureChanges(
        IMethodSymbol method, ParameterListSyntax parameterList, List<SignatureChange>? changes)
    {
        object Invalid(string message, string? hint = null) =>
            CreateErrorResponse(ErrorCodes.InvalidParameter, message, hint: hint);

        if (changes == null || changes.Count == 0) return (null, Invalid("changes must list at least one change"));

        var parameters = method.Parameters;
        var display = method.ToDisplayString();
        int OrdinalOf(string name)
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name == name) return i;
            }
            return -1;
        }

        var removed = new HashSet<int>();
        var renames = new Dictionary<int, string>();
        List<string>? orderNames = null;
        var orderPath = "";
        var adds = new List<(string path, SignatureChange change)>();

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var path = $"changes[{i}]";
            if (ParameterValidation.Choice($"{path}.action", change.Action, SignatureActions, out var action) is { } actionError)
                return (null, CreateErrorResponse(actionError));

            switch (action)
            {
                case "remove":
                {
                    if (string.IsNullOrEmpty(change.Name)) return (null, Invalid($"{path}: remove requires name"));
                    var ordinal = OrdinalOf(change.Name);
                    if (ordinal < 0) return (null, Invalid($"{path}: '{change.Name}' is not a parameter of {display}"));
                    if (!removed.Add(ordinal)) return (null, Invalid($"{path}: '{change.Name}' is removed twice"));
                    if (method.IsExtensionMethod && ordinal == 0)
                        return (null, Invalid($"{path}: the extension receiver '{change.Name}' cannot be removed"));
                    break;
                }
                case "rename":
                {
                    if (string.IsNullOrEmpty(change.Name)) return (null, Invalid($"{path}: rename requires name"));
                    if (string.IsNullOrEmpty(change.NewName)) return (null, Invalid($"{path}: rename requires newName"));
                    if (!IsUsableIdentifier(change.NewName))
                        return (null, Invalid($"{path}: '{change.NewName}' is not a valid C# identifier"));
                    var ordinal = OrdinalOf(change.Name);
                    if (ordinal < 0) return (null, Invalid($"{path}: '{change.Name}' is not a parameter of {display}"));
                    if (renames.ContainsKey(ordinal)) return (null, Invalid($"{path}: '{change.Name}' is renamed twice"));
                    if (OrdinalOf(change.NewName) >= 0 || renames.ContainsValue(change.NewName))
                        return (null, Invalid($"{path}: the method already has a parameter named '{change.NewName}'"));
                    renames[ordinal] = change.NewName;
                    break;
                }
                case "reorder":
                {
                    if (orderNames != null) return (null, Invalid($"{path}: only one reorder is allowed"));
                    if (change.Order == null || change.Order.Count == 0) return (null, Invalid($"{path}: reorder requires order"));
                    orderNames = change.Order;
                    orderPath = path;
                    break;
                }
                default:
                {
                    if (string.IsNullOrEmpty(change.Name)) return (null, Invalid($"{path}: add requires name"));
                    if (!IsUsableIdentifier(change.Name)) return (null, Invalid($"{path}: '{change.Name}' is not a valid C# identifier"));
                    if (string.IsNullOrEmpty(change.Type)) return (null, Invalid($"{path}: add requires type"));
                    if (SyntaxFactory.ParseTypeName(change.Type).ContainsDiagnostics)
                        return (null, Invalid($"{path}: '{change.Type}' is not a valid C# type"));
                    if (change.DefaultValue != null && SyntaxFactory.ParseExpression(change.DefaultValue).ContainsDiagnostics)
                        return (null, Invalid($"{path}: '{change.DefaultValue}' is not a valid C# expression"));
                    adds.Add((path, change));
                    break;
                }
            }
        }

        // The kept parameters, in their new order.
        var kept = Enumerable.Range(0, parameters.Length).Where(o => !removed.Contains(o)).ToList();
        if (orderNames != null)
        {
            var order = new List<int>();
            foreach (var name in orderNames)
            {
                var ordinal = OrdinalOf(name);
                if (ordinal < 0)
                {
                    var renamedFrom = renames.FirstOrDefault(r => r.Value == name);
                    ordinal = renamedFrom.Value == name ? renamedFrom.Key : -1;
                }
                if (ordinal < 0) return (null, Invalid($"{orderPath}: reorder names '{name}', which is not a parameter of {display}"));
                if (removed.Contains(ordinal)) return (null, Invalid($"{orderPath}: reorder names '{name}', which is removed"));
                if (order.Contains(ordinal)) return (null, Invalid($"{orderPath}: reorder names '{name}' twice"));
                order.Add(ordinal);
            }
            var missing = kept.Where(o => !order.Contains(o)).Select(o => parameters[o].Name).ToList();
            if (missing.Count > 0)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"{orderPath}: reorder must list every kept parameter; missing: {string.Join(", ", missing)}",
                    hint: "List all parameters that are not being removed in the order array.",
                    context: new { order = orderNames, missing }));
            }
            kept = order;
        }

        var slots = kept.Select(o => new SignatureSlot(o)).ToList();
        var finalNames = kept.Select(o => renames.TryGetValue(o, out var renamed) ? renamed : parameters[o].Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (path, change) in adds)
        {
            if (!finalNames.Add(change.Name!))
                return (null, Invalid($"{path}: the method already has a parameter named '{change.Name}'"));
            var slot = new SignatureSlot(-1, change.Name, change.Type, change.DefaultValue);
            if (change.Position is null or -1)
            {
                slots.Add(slot);
            }
            else if (change.Position.Value < 0 || change.Position.Value > slots.Count)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"{path}.position must be -1 (the end) or between 0 and {slots.Count} (got {change.Position.Value})",
                    hint: "Omit position, or pass -1, to append the parameter at the end.",
                    context: new { position = change.Position.Value, parameterCount = slots.Count }));
            }
            else
            {
                slots.Insert(change.Position.Value, slot);
            }
        }

        // A required parameter after an optional one does not compile (CS1737); a params
        // tail is exempt. A params array must be last (CS0231) and an extension method's
        // receiver first (CS1100).
        string SlotName(SignatureSlot s) => s.IsAdded ? s.AddedName! : renames.TryGetValue(s.OldOrdinal, out var n) ? n : parameters[s.OldOrdinal].Name;
        bool IsOptional(SignatureSlot s) => s.IsAdded ? s.AddedDefault != null : parameterList.Parameters[s.OldOrdinal].Default != null;
        bool IsParams(SignatureSlot s) => !s.IsAdded && parameters[s.OldOrdinal].IsParams;
        var seenOptional = false;
        foreach (var slot in slots)
        {
            if (IsOptional(slot)) { seenOptional = true; continue; }
            if (seenOptional && !IsParams(slot))
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"Parameter '{SlotName(slot)}' has no default value but would follow a parameter that does; required parameters must precede optional ones (CS1737).",
                    hint: "Order the change so every defaulted parameter comes last, or give this parameter a default.",
                    context: new { parameter = SlotName(slot) }));
            }
        }
        for (var i = 0; i < slots.Count; i++)
        {
            if (IsParams(slots[i]) && i != slots.Count - 1)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"Parameter '{SlotName(slots[i])}' is a params array but is not last; params must be the final parameter (CS0231).",
                    hint: "Keep the params parameter at the end, or don't place a parameter after it.",
                    context: new { parameter = SlotName(slots[i]), position = i }));
            }
            if (method.IsExtensionMethod && !slots[i].IsAdded && slots[i].OldOrdinal == 0 && i != 0)
            {
                return (null, CreateErrorResponse(
                    ErrorCodes.InvalidParameter,
                    $"Parameter '{SlotName(slots[i])}' has the extension 'this' modifier but is not first; 'this' must be the first parameter (CS1100).",
                    hint: "Keep the extension receiver as the first parameter.",
                    context: new { parameter = SlotName(slots[i]), position = i }));
            }
        }

        return (new SignaturePlan(slots, renames), null);
    }

    private static bool IsUsableIdentifier(string name) =>
        SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

    // The method plus every member whose parameter list must change with it: the member
    // it overrides, its overrides, the interface members it implements and their
    // implementations, transitively. Constructors and local functions stand alone. A
    // member tied to one declared in metadata cannot change and is refused. The target
    // comes first, the others in source order.
    private async Task<(List<IMethodSymbol>? members, object? error)> RelatedSignatureMembersAsync(
        IMethodSymbol method, CancellationToken cancellationToken)
    {
        var members = new List<IMethodSymbol> { method };
        if (method.MethodKind is MethodKind.Constructor or MethodKind.LocalFunction) return (members, null);

        var seen = new HashSet<(string, int)> { DeclarationKey(method)!.Value };
        var queue = new Queue<IMethodSymbol>();
        queue.Enqueue(method);
        while (queue.Count > 0)
        {
            var member = queue.Dequeue();
            var candidates = new List<ISymbol>();
            if (member.OverriddenMethod != null) candidates.Add(member.OverriddenMethod);
            candidates.AddRange(await SymbolFinder.FindOverridesAsync(member, _solution!, cancellationToken: cancellationToken));
            candidates.AddRange(await SymbolFinder.FindImplementedInterfaceMembersAsync(member, _solution!, cancellationToken: cancellationToken));
            if (member.ContainingType?.TypeKind == TypeKind.Interface)
                candidates.AddRange(await SymbolFinder.FindImplementationsAsync(member, _solution!, cancellationToken: cancellationToken));

            foreach (var candidate in candidates.OfType<IMethodSymbol>().Select(c => c.OriginalDefinition))
            {
                if (DeclarationKey(candidate) is not { } key)
                {
                    return (null, CreateErrorResponse(
                        ErrorCodes.InvalidParameter,
                        $"'{method.ToDisplayString()}' must keep the signature of '{candidate.ToDisplayString()}', which is declared in metadata",
                        hint: "A member that overrides or implements a library member cannot change its parameters.",
                        context: new { method = method.ToDisplayString(), member = candidate.ToDisplayString() }));
                }
                if (!seen.Add(key)) continue;
                members.Add(candidate);
                queue.Enqueue(candidate);
            }
        }

        var ordered = members.Skip(1).OrderBy(m => DeclarationKey(m)!.Value.Item1, StringComparer.Ordinal)
            .ThenBy(m => DeclarationKey(m)!.Value.Item2);
        return (members.Take(1).Concat(ordered).ToList(), null);

        static (string, int)? DeclarationKey(ISymbol symbol) =>
            symbol.Locations.FirstOrDefault(l => l.IsInSource) is { SourceTree.FilePath: { } path } location
                ? (path, location.SourceSpan.Start)
                : null;
    }

    // The symbol in `solution` that `symbol` (a symbol of the loaded solution) denotes:
    // resolved in the compilation of the project that declares it, which the edits so
    // far may have changed.
    private async Task<ISymbol?> FindInSolutionAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        var tree = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree;
        if (tree == null || _solution!.GetDocumentId(tree) is not { } documentId) return null;
        var compilation = await solution.GetProject(documentId.ProjectId)!.GetCompilationAsync(cancellationToken);
        return compilation == null ? null : SymbolFinder.FindSimilarSymbols(symbol, compilation, cancellationToken).FirstOrDefault();
    }

    // What a reference to the method is, by its syntax: a call whose argument list can be
    // rewritten, a cref whose parameter list can be rewritten, nameof (nothing to do:
    // all three null), or the reason it cannot be updated.
    private static (SyntaxNode? call, CrefParameterListSyntax? cref, string? reason) ClassifySignatureReference(
        SyntaxNode root, TextSpan span, bool isImplicit, IMethodSymbol member)
    {
        if (isImplicit) return (null, null, "implicit call: there is no argument list to edit");

        var node = root.FindNode(span, findInsideTrivia: true, getInnermostNodeForTie: true);
        if (node.AncestorsAndSelf(ascendOutOfTrivia: false).OfType<CrefSyntax>().FirstOrDefault() is { } cref)
        {
            var parameters = cref switch
            {
                NameMemberCrefSyntax nameCref => nameCref.Parameters,
                QualifiedCrefSyntax { Member: NameMemberCrefSyntax qualified } => qualified.Parameters,
                _ => null
            };
            return (null, parameters, null);
        }

        switch (node)
        {
            case ConstructorInitializerSyntax initializer:
                return (initializer, null, null);
            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return (implicitCreation, null, null);
        }

        if (node is not SimpleNameSyntax name) return (null, null, "not a call of the method");

        // The whole name the reference is the right-most part of.
        SyntaxNode expression = name;
        while (expression.Parent is QualifiedNameSyntax qualifiedName && qualifiedName.Right == expression
               || expression.Parent is AliasQualifiedNameSyntax aliasName && aliasName.Name == expression
               || expression.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == expression
               || expression.Parent is MemberBindingExpressionSyntax memberBinding && memberBinding.Name == expression)
        {
            expression = expression.Parent!;
        }

        switch (expression.Parent)
        {
            case InvocationExpressionSyntax invocation when invocation.Expression == expression:
                return (invocation, null, null);
            case ObjectCreationExpressionSyntax creation when creation.Type == expression:
                return (creation, null, null);
            case AttributeSyntax attribute when attribute.Name == expression:
                return (attribute, null, null);
            case PrimaryConstructorBaseTypeSyntax baseType when baseType.Type == expression:
                return (baseType, null, null);
            case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } } }:
                return (null, null, null);
        }

        return member.MethodKind == MethodKind.Constructor
            ? (null, null, "not a call of the constructor")
            : (null, null, MethodGroupReason);
    }

    // The method a call binds to (one of the related members) and, per parameter
    // ordinal, how the call supplies it; null when the call binds to something else.
    private static (IMethodSymbol target, Dictionary<int, BoundArgument> arguments)? BindSignatureCall(
        SemanticModel model, SyntaxNode call, IReadOnlyList<IMethodSymbol> related, CancellationToken cancellationToken)
    {
        var (method, arguments) = model.GetOperation(call, cancellationToken) switch
        {
            IInvocationOperation invocation => (invocation.TargetMethod, invocation.Arguments),
            IObjectCreationOperation creation => (creation.Constructor, creation.Arguments),
            IAttributeOperation { Operation: IObjectCreationOperation attributeCreation } =>
                (attributeCreation.Constructor, attributeCreation.Arguments),
            _ => ((IMethodSymbol?)null, ImmutableArray<IArgumentOperation>.Empty)
        };
        if (method == null) return null;
        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        if (!related.Any(r => CrossCompilationSymbolComparer.Instance.Equals(r.OriginalDefinition, definition))) return null;

        var nodes = ArgumentNodesOf(call);
        var bound = new Dictionary<int, BoundArgument>();
        foreach (var argument in arguments)
        {
            if (argument.Parameter is not { } parameter) continue;
            switch (argument.ArgumentKind)
            {
                case ArgumentKind.DefaultValue:
                    bound[parameter.Ordinal] = new BoundArgument(BoundArgumentKind.Omitted);
                    break;
                case ArgumentKind.ParamArray:
                    var elements = (argument.Value as IArrayCreationOperation)?.Initializer?.ElementValues
                        .Select(e => IndexOfArgument(nodes, e.Syntax))
                        .Where(i => i >= 0)
                        .ToList() ?? new List<int>();
                    bound[parameter.Ordinal] = new BoundArgument(BoundArgumentKind.Expanded, Elements: elements);
                    break;
                default:
                    var index = IndexOfArgument(nodes, argument.Syntax);
                    bound[parameter.Ordinal] = index < 0
                        ? new BoundArgument(BoundArgumentKind.Receiver)
                        : new BoundArgument(BoundArgumentKind.Explicit, index, NameColonOf(nodes[index]) != null);
                    break;
            }
        }
        foreach (var parameter in method.Parameters)
        {
            bound.TryAdd(parameter.Ordinal, new BoundArgument(BoundArgumentKind.Omitted));
        }
        return (method, bound);
    }

    // The position of the argument (in the call's own argument list) that contains a
    // bound argument's syntax, or -1 when the syntax is not one of the call's arguments
    // (the receiver of a reduced-form extension call).
    private static int IndexOfArgument(IReadOnlyList<SyntaxNode> nodes, SyntaxNode syntax)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Span.Contains(syntax.Span) && (syntax == nodes[i] || syntax.Ancestors().Contains(nodes[i]))) return i;
        }
        return -1;
    }

    // The argument nodes of a call, in order: an argument list's arguments, an
    // attribute's constructor arguments (property assignments excluded).
    private static IReadOnlyList<SyntaxNode> ArgumentNodesOf(SyntaxNode call) => call switch
    {
        AttributeSyntax attribute => attribute.ArgumentList?.Arguments.Where(a => a.NameEquals == null).Cast<SyntaxNode>().ToList()
            ?? new List<SyntaxNode>(),
        _ => (IReadOnlyList<SyntaxNode>?)ArgumentListOf(call)?.Arguments.Cast<SyntaxNode>().ToList() ?? new List<SyntaxNode>()
    };

    private static ArgumentListSyntax? ArgumentListOf(SyntaxNode call) => call switch
    {
        InvocationExpressionSyntax invocation => invocation.ArgumentList,
        BaseObjectCreationExpressionSyntax creation => creation.ArgumentList,
        ConstructorInitializerSyntax initializer => initializer.ArgumentList,
        PrimaryConstructorBaseTypeSyntax baseType => baseType.ArgumentList,
        _ => null
    };

    private static NameColonSyntax? NameColonOf(SyntaxNode argument) => argument switch
    {
        ArgumentSyntax a => a.NameColon,
        AttributeArgumentSyntax a => a.NameColon,
        _ => null
    };

    // The argument-list text of a call as written ("" when it has none).
    private static string ArgumentListText(SyntaxNode call) => call switch
    {
        AttributeSyntax attribute => attribute.ArgumentList?.ToString() ?? "",
        _ => ArgumentListOf(call)?.ToString() ?? ""
    };

    // The new argument list of one call as items over its original arguments (see the
    // file header for the rules).
    private static List<ArgumentPlanItem> PlanCallArguments(
        SignaturePlan plan, IMethodSymbol target, IReadOnlyDictionary<int, BoundArgument> arguments,
        SemanticModel model, SyntaxNode call)
    {
        var items = new List<ArgumentPlanItem>();
        var deferred = new List<SignatureSlot>();
        var named = false;
        foreach (var slot in plan.Slots)
        {
            if (named)
            {
                deferred.Add(slot);
                continue;
            }
            if (slot.IsAdded)
            {
                items.Add(ArgumentPlanItem.Added(slot.AddedDefault ?? "default", name: null));
                continue;
            }
            var argument = arguments[slot.OldOrdinal];
            switch (argument.Kind)
            {
                case BoundArgumentKind.Receiver:
                    continue;
                case BoundArgumentKind.Explicit when !argument.IsNamed:
                    items.Add(ArgumentPlanItem.Original(argument.Index));
                    continue;
                case BoundArgumentKind.Expanded:
                    items.AddRange(argument.Elements!.Select(e => ArgumentPlanItem.Original(e)));
                    continue;
                default:
                    named = true;
                    deferred.Add(slot);
                    continue;
            }
        }

        // From the first skipped or named parameter on: the written arguments named, in
        // the order they were written, then the added ones.
        var written = new List<(int order, ArgumentPlanItem item)>();
        foreach (var slot in deferred.Where(s => !s.IsAdded))
        {
            var argument = arguments[slot.OldOrdinal];
            var parameter = target.Parameters[slot.OldOrdinal];
            switch (argument.Kind)
            {
                case BoundArgumentKind.Explicit:
                    written.Add((argument.Index, argument.IsNamed
                        ? ArgumentPlanItem.Original(argument.Index)
                        : ArgumentPlanItem.Original(argument.Index, parameter.Name)));
                    break;
                case BoundArgumentKind.Expanded when argument.Elements!.Count > 0:
                    var elementType = ((IArrayTypeSymbol)parameter.Type).ElementType
                        .ToMinimalDisplayString(model, call.SpanStart);
                    written.Add((argument.Elements[0], ArgumentPlanItem.ParamsArray(argument.Elements, parameter.Name, elementType)));
                    break;
            }
        }
        items.AddRange(written.OrderBy(w => w.order).Select(w => w.item));
        items.AddRange(deferred.Where(s => s.IsAdded).Select(s => ArgumentPlanItem.Added(s.AddedDefault ?? "default", s.AddedName)));
        return items;
    }

    // Whether a plan reproduces the original argument list exactly.
    private static bool IsIdentityPlan(IReadOnlyList<ArgumentPlanItem> items, int originalCount) =>
        items.Count == originalCount
        && items.Select((item, i) => item.OriginalIndex == i && item.NameToAdd == null && item.Expression == null && item.ElementIndices == null)
            .All(same => same);

    // The call with its argument list rebuilt from the plan; the original arguments are
    // read from `current`, the call as earlier edits (to calls nested in its arguments)
    // left it.
    private static SyntaxNode WithRebuiltArguments(SyntaxNode current, IReadOnlyList<ArgumentPlanItem> items)
    {
        switch (current)
        {
            case AttributeSyntax attribute:
            {
                var list = attribute.ArgumentList ?? SyntaxFactory.AttributeArgumentList();
                var constructorArguments = list.Arguments.Where(a => a.NameEquals == null).ToList();
                var nodes = items.Select(item => BuildArgument(item,
                    i => constructorArguments[i],
                    (argument, name) => argument.WithoutLeadingTrivia().WithNameColon(NameColon(name)).WithLeadingTrivia(argument.GetLeadingTrivia()),
                    (name, expression) => SyntaxFactory.AttributeArgument(null, name == null ? null : NameColon(name), expression)))
                    .ToList();
                var laidOut = LayOut(constructorArguments, nodes);
                laidOut.AddRange(list.Arguments.Where(a => a.NameEquals != null));
                return attribute.WithArgumentList(list.WithArguments(Separated(list.Arguments, laidOut)));
            }
            default:
            {
                // A creation written without parentheses (new C { ... }) gets a new list.
                var list = ArgumentListOf(current) ?? SyntaxFactory.ArgumentList().WithAdditionalAnnotations(Formatter.Annotation);
                var original = list.Arguments.ToList();
                var nodes = items.Select(item => BuildArgument(item,
                    i => original[i],
                    (argument, name) => argument.WithoutLeadingTrivia().WithNameColon(NameColon(name)).WithLeadingTrivia(argument.GetLeadingTrivia()),
                    (name, expression) => SyntaxFactory.Argument(name == null ? null : NameColon(name), default, expression)))
                    .ToList();
                var rebuilt = list.WithArguments(Separated(list.Arguments, LayOut(original, nodes)));
                return current switch
                {
                    InvocationExpressionSyntax invocation => invocation.WithArgumentList(rebuilt),
                    ObjectCreationExpressionSyntax creation => creation.WithArgumentList(rebuilt),
                    ImplicitObjectCreationExpressionSyntax creation => creation.WithArgumentList(rebuilt),
                    ConstructorInitializerSyntax initializer => initializer.WithArgumentList(rebuilt),
                    PrimaryConstructorBaseTypeSyntax baseType => baseType.WithArgumentList(rebuilt),
                    _ => current
                };
            }
        }
    }

    // One argument of a rebuilt list. Created and renamed arguments carry
    // Formatter.Annotation; kept ones are the original nodes.
    private static TArgument BuildArgument<TArgument>(
        ArgumentPlanItem item,
        Func<int, TArgument> original,
        Func<TArgument, string, TArgument> withName,
        Func<string?, ExpressionSyntax, TArgument> create)
        where TArgument : SyntaxNode
    {
        if (item.ElementIndices != null)
        {
            var elements = item.ElementIndices.Select(i => ExpressionOf(original(i))).ToList();
            var array = SyntaxFactory.ArrayCreationExpression(
                SyntaxFactory.ArrayType(SyntaxFactory.ParseTypeName(item.ElementType!))
                    .WithRankSpecifiers(SyntaxFactory.SingletonList(SyntaxFactory.ArrayRankSpecifier(
                        SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(SyntaxFactory.OmittedArraySizeExpression())))),
                SyntaxFactory.InitializerExpression(SyntaxKind.ArrayInitializerExpression,
                    SyntaxFactory.SeparatedList(elements.Select(e => e.WithoutTrivia()))));
            return create(item.NameToAdd, array).WithAdditionalAnnotations(Formatter.Annotation);
        }
        if (item.Expression != null)
        {
            return create(item.NameToAdd, SyntaxFactory.ParseExpression(item.Expression)).WithAdditionalAnnotations(Formatter.Annotation);
        }
        var node = original(item.OriginalIndex);
        return item.NameToAdd == null ? node : withName(node, item.NameToAdd).WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static ExpressionSyntax ExpressionOf(SyntaxNode argument) => argument switch
    {
        ArgumentSyntax a => a.Expression,
        AttributeArgumentSyntax a => a.Expression,
        _ => throw new ArgumentException("Not an argument", nameof(argument))
    };

    private static NameColonSyntax NameColon(string name) => SyntaxFactory.NameColon(EscapedName(name));

    // An identifier name for a symbol's name, verbatim (@name) when the name is a keyword.
    private static IdentifierNameSyntax EscapedName(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
            ? SyntaxFactory.IdentifierName(SyntaxFactory.VerbatimIdentifier(default, "@" + name, name, default))
            : SyntaxFactory.IdentifierName(name);

    // The rebuilt elements of a list laid out like the original: an element that moved to
    // another position and has only whitespace before it takes the whitespace of the
    // element that was at that position, so a multi-line list stays multi-line and a
    // first element never starts with another position's indentation.
    private static List<T> LayOut<T>(IReadOnlyList<T> original, IReadOnlyList<T> rebuilt) where T : SyntaxNode
    {
        var result = new List<T>(rebuilt.Count);
        for (var k = 0; k < rebuilt.Count; k++)
        {
            var node = rebuilt[k];
            var movedFrom = IndexOfNode(original, node);
            if (movedFrom >= 0 && movedFrom != k && IsLayoutTrivia(node.GetLeadingTrivia()))
            {
                var layout = k < original.Count && IsLayoutTrivia(original[k].GetLeadingTrivia())
                    ? original[k].GetLeadingTrivia()
                    : SyntaxFactory.TriviaList();
                node = node.WithLeadingTrivia(layout);
            }
            result.Add(node);
        }
        return result;
    }

    private static int IndexOfNode<T>(IReadOnlyList<T> nodes, T node) where T : SyntaxNode
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] == node) return i;
        }
        return -1;
    }

    private static bool IsLayoutTrivia(SyntaxTriviaList trivia) =>
        trivia.All(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia));

    // The elements separated by the original list's separators where it had them (their
    // trivia kept), and ", " beyond.
    private static SeparatedSyntaxList<T> Separated<T>(SeparatedSyntaxList<T> original, IReadOnlyList<T> nodes) where T : SyntaxNode
    {
        var separators = new List<SyntaxToken>();
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            separators.Add(i < original.SeparatorCount
                ? original.GetSeparator(i)
                : SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space));
        }
        return SyntaxFactory.SeparatedList(nodes, separators);
    }

    // A declaration's parameter list in the new order: kept parameters are their original
    // syntax; an added parameter is built from the given type and default (an explicit
    // interface implementation takes no default, which would have no effect there).
    private static ParameterListSyntax RebuildParameterList(
        ParameterListSyntax current, SignaturePlan plan, bool explicitImplementation)
    {
        var original = current.Parameters.ToList();
        var nodes = plan.Slots.Select(slot =>
        {
            if (!slot.IsAdded) return original[slot.OldOrdinal];
            var parameter = SyntaxFactory.Parameter(EscapedName(slot.AddedName!).Identifier)
                .WithType(SyntaxFactory.ParseTypeName(slot.AddedType!));
            if (slot.AddedDefault != null && !explicitImplementation)
                parameter = parameter.WithDefault(SyntaxFactory.EqualsValueClause(SyntaxFactory.ParseExpression(slot.AddedDefault)));
            return parameter.WithAdditionalAnnotations(Formatter.Annotation);
        }).ToList();
        return current.WithParameters(Separated(current.Parameters, LayOut(original, nodes)));
    }

    // A cref's parameter list (<see cref="M(int, string)"/>) in the new order.
    private static CrefParameterListSyntax RebuildCrefParameters(CrefParameterListSyntax current, SignaturePlan plan)
    {
        var original = current.Parameters.ToList();
        var nodes = plan.Slots.Select(slot => slot.IsAdded
            ? SyntaxFactory.CrefParameter(SyntaxFactory.ParseTypeName(slot.AddedType!)).WithAdditionalAnnotations(Formatter.Annotation)
            : original[slot.OldOrdinal]).ToList();
        return current.WithParameters(Separated(current.Parameters, LayOut(original, nodes)));
    }

    // "ReturnType Name(parameters)" (a constructor: "Type(parameters)"), each parameter as
    // written with its whitespace normalized.
    private static string RenderSignature(IMethodSymbol method, SeparatedSyntaxList<ParameterSyntax> parameters)
    {
        var parameterText = string.Join(", ", parameters.Select(p => p.NormalizeWhitespace().ToString()));
        return method.MethodKind == MethodKind.Constructor
            ? $"{method.ContainingType.Name}({parameterText})"
            : $"{method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {method.Name}({parameterText})";
    }

    private static List<object> DescribeParameters(IMethodSymbol method, SeparatedSyntaxList<ParameterSyntax> parameters) =>
        parameters.Select((p, i) => (object)new
        {
            name = method.Parameters[i].Name,
            type = p.Type?.NormalizeWhitespace().ToString(),
            hasDefault = p.Default != null,
            defaultValue = p.Default?.Value.ToString(),
            refKind = method.Parameters[i].RefKind,
            isParams = method.Parameters[i].IsParams,
            isThis = method.IsExtensionMethod && i == 0
        }).ToList();
}
