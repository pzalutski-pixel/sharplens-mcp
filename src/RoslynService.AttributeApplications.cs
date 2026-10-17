using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpLensMcp;

// The one walk over attribute applications that get_attributes and find_attribute_usages
// share, so both see the same targets: every attribute written in the source trees of the
// scoped projects (generator output included), whatever it is applied to — the assembly or
// module, a type, member, accessor, parameter, return value or type parameter, a lambda or
// local function, the backing field of an auto-property or field-like event ([field:]), the
// accessors of a field-like event ([method:]), the property of a record's positional
// parameter ([property:]), each variable of a multi-variable field declaration. The bound
// attribute class is matched by MatchesAttribute. A multi-targeted project's applications
// are reported once with the target frameworks they occurred in, in the canonical order of
// where the attributes are written.
public partial class RoslynService
{
    private async Task<List<MergedResult<AttributeApplication>>> FindAttributeApplicationsAsync(
        IReadOnlyList<Project> projects, Func<SyntaxTree, bool>? includeTree, string attributeName,
        CancellationToken cancellationToken)
    {
        var found = new List<(AttributeApplication Application, string? TargetFramework)>();
        foreach (var project in projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;
            var targetFramework = LogicalProject.TargetFrameworkOf(project);

            foreach (var tree in compilation.SyntaxTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (includeTree != null && !includeTree(tree)) continue;
                var root = await tree.GetRootAsync(cancellationToken);
                SemanticModel? model = null;
                foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
                {
                    model ??= compilation.GetSemanticModel(tree);
                    foreach (var application in ApplicationsOf(attribute, model, compilation, cancellationToken))
                    {
                        if (MatchesAttribute(application.Attribute, attributeName)) found.Add((application, targetFramework));
                    }
                }
            }
        }

        var merged = TargetFrameworkDeduplication.By(
            found,
            f => $"{SourceSpanKey.From(f.Application.AttributeLocation)}|{f.Application.Target}|{CrossCompilationSymbol.SolutionKey(f.Application.Owner) ?? $"{f.Application.Owner.Kind}:{f.Application.Owner.ToDisplayString()}"}",
            StringComparer.Ordinal,
            f => f.TargetFramework);
        return CanonicalOrder.ByLocation(
            merged.Select(m => new MergedResult<AttributeApplication>(m.Item.Application, m.TargetFrameworks, m.Occurrences)),
            m => ReportedLocation(m.Item.AttributeLocation));
    }

    // The applications an attribute syntax makes: for every symbol the declaration it is
    // attached to can apply attributes to, the attribute data whose syntax is this one.
    private static IEnumerable<AttributeApplication> ApplicationsOf(
        AttributeSyntax attribute, SemanticModel model, Compilation compilation, CancellationToken cancellationToken)
    {
        if (attribute.Parent is not AttributeListSyntax { Parent: { } declaration }) yield break;

        var location = attribute.GetLocation();
        foreach (var (owner, target, attributes) in AttributeCarriers(declaration, model, compilation, cancellationToken))
        {
            foreach (var data in attributes)
            {
                if (data.ApplicationSyntaxReference is { } reference
                    && reference.SyntaxTree == attribute.SyntaxTree
                    && reference.Span == attribute.Span)
                {
                    yield return new AttributeApplication(owner, target, data, location);
                }
            }
        }
    }

    // Every (reported owner, attribute target, attributes) a declaration's attribute lists
    // can land in.
    private static IEnumerable<(ISymbol Owner, string Target, IEnumerable<AttributeData> Attributes)> AttributeCarriers(
        SyntaxNode declaration, SemanticModel model, Compilation compilation, CancellationToken cancellationToken)
    {
        if (declaration is CompilationUnitSyntax)
        {
            yield return (compilation.Assembly, "assembly", compilation.Assembly.GetAttributes());
            yield return (compilation.SourceModule, "module", compilation.SourceModule.GetAttributes());
            yield break;
        }

        var declared = declaration switch
        {
            BaseFieldDeclarationSyntax field => field.Declaration.Variables
                .Select(v => model.GetDeclaredSymbol(v, cancellationToken)).ToList(),
            AnonymousFunctionExpressionSyntax lambda => new List<ISymbol?> { model.GetSymbolInfo(lambda, cancellationToken).Symbol },
            _ => new List<ISymbol?> { model.GetDeclaredSymbol(declaration, cancellationToken) }
        };

        // A record's positional parameter also declares a property (and its backing field).
        if (declaration is ParameterSyntax { Parent: ParameterListSyntax { Parent: RecordDeclarationSyntax record } } parameter
            && model.GetDeclaredSymbol(record, cancellationToken) is INamedTypeSymbol recordType)
        {
            declared.AddRange(recordType.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>());
        }

        foreach (var symbol in declared.OfType<ISymbol>())
        {
            switch (symbol)
            {
                case INamedTypeSymbol type:
                    yield return (type, "type", type.GetAttributes());
                    if (type.DelegateInvokeMethod is { } invoke) yield return (type, "return", invoke.GetReturnTypeAttributes());
                    break;
                case IMethodSymbol method:
                    yield return (method, "method", method.GetAttributes());
                    yield return (method, "return", method.GetReturnTypeAttributes());
                    break;
                case IPropertySymbol property:
                    yield return (property, "property", property.GetAttributes());
                    if (BackingFieldOf(property) is { } propertyField) yield return (property, "field", propertyField.GetAttributes());
                    break;
                case IEventSymbol @event:
                    yield return (@event, "event", @event.GetAttributes());
                    if (BackingFieldOf(@event) is { } eventField) yield return (@event, "field", eventField.GetAttributes());
                    foreach (var accessor in new[] { @event.AddMethod, @event.RemoveMethod }.OfType<IMethodSymbol>())
                        yield return (accessor, "method", accessor.GetAttributes());
                    break;
                case IFieldSymbol field:
                    yield return (field, "field", field.GetAttributes());
                    break;
                case IParameterSymbol parameterSymbol:
                    yield return (parameterSymbol, "param", parameterSymbol.GetAttributes());
                    break;
                case ITypeParameterSymbol typeParameter:
                    yield return (typeParameter, "typevar", typeParameter.GetAttributes());
                    break;
            }
        }
    }

    // The compiler-made backing field of an auto-property or field-like event.
    private static IFieldSymbol? BackingFieldOf(ISymbol member) =>
        member.ContainingType?.GetMembers().OfType<IFieldSymbol>()
            .FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, member));

    // The kind an attribute tool reports an owner as: the symbol-kind vocabulary for types
    // and members, else Assembly, Module, Accessor, Parameter, TypeParameter, LocalFunction
    // or Lambda.
    private static string AttributeOwnerKind(ISymbol owner) => owner switch
    {
        IAssemblySymbol => "Assembly",
        IModuleSymbol => "Module",
        IParameterSymbol => "Parameter",
        ITypeParameterSymbol => "TypeParameter",
        IMethodSymbol
        {
            MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet
                or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
        } => "Accessor",
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } => "LocalFunction",
        IMethodSymbol { MethodKind: MethodKind.AnonymousFunction } => "Lambda",
        _ => SymbolKindVocabulary.Label(owner) ?? owner.Kind.ToString()
    };

    // The scope get_attributes searches: every project (null, empty or "solution"),
    // "project:<name>" resolved like every project-scoped tool, or "file:<path>" — the
    // document the path names (absolute, solution-relative, or the trailing path segments
    // of exactly one document, such as its file name). Anything else is INVALID_PARAMETER.
    private (IReadOnlyList<Project>? Projects, Func<SyntaxTree, bool>? IncludeTree, object? Error) ResolveAttributeScope(string? scope)
    {
        if (string.IsNullOrEmpty(scope) || scope.Equals("solution", StringComparison.OrdinalIgnoreCase))
        {
            return (ResolveProjectScope(null).Projects, null, null);
        }
        if (scope.StartsWith("project:", StringComparison.OrdinalIgnoreCase))
        {
            var (projects, error) = ResolveProjectScope(scope["project:".Length..]);
            return (projects, null, error);
        }
        if (scope.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var (path, error) = ResolveScopeDocumentPath(scope["file:".Length..]);
            if (path == null) return (null, null, error);
            var fullPath = Path.GetFullPath(path);
            return (ResolveProjectScope(null).Projects,
                tree => IsNameablePath(tree.FilePath) && string.Equals(Path.GetFullPath(tree.FilePath), fullPath, PathComparison),
                null);
        }
        return (null, null, CreateErrorResponse(
            ErrorCodes.InvalidParameter,
            $"scope must be 'solution', 'project:<name>' or 'file:<path>' (got '{scope}')",
            hint: "Omit scope to search the whole solution.",
            context: new { parameter = "scope", value = scope }));
    }

    // The path of the solution document a file scope names: the document TryFindDocument
    // resolves (absolute or solution-relative), else the one document whose path ends with
    // the given path's whole segments. None is FILE_NOT_IN_SOLUTION; several are
    // AMBIGUOUS_MATCH listing them.
    private (string? Path, object? Error) ResolveScopeDocumentPath(string path)
    {
        if (TryFindDocument(path)?.FilePath is { } resolved) return (resolved, null);

        var suffix = "/" + path.Replace('\\', '/').TrimStart('/');
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var matches = _solution!.Projects
            .SelectMany(p => p.Documents)
            .Select(d => d.FilePath)
            .OfType<string>()
            .Where(p => p.Replace('\\', '/').EndsWith(suffix, PathComparison))
            .Distinct(pathComparer)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        return matches.Count switch
        {
            1 => (matches[0], null),
            0 => (null, CreateErrorResponse(
                ErrorCodes.FileNotInSolution,
                $"File not found in solution: {path}",
                hint: "Pass the document's path (absolute or relative to the solution directory) or its file name.",
                context: new { filePath = path })),
            _ => (null, CreateErrorResponse(
                ErrorCodes.AmbiguousMatch,
                $"'{path}' matches {matches.Count} documents",
                hint: "Pass more of the document's path to name one of them.",
                context: new { filePath = path, candidates = matches.Select(FormatPath).ToList() }))
        };
    }
}
