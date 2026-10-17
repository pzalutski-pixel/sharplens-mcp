using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Text;

namespace SharpLensMcp;

// Code-validation tools: snippet compile check and type-compatibility check.
public partial class RoslynService
{
    // The project a snippet without a context file compiles as a new file of: the one
    // with the most references (the most likely to resolve a snippet's types), by name on
    // a tie — chosen deterministically, and the single source of the references, language
    // version, preprocessor symbols, compilation options and global usings.
    private Project ValidationReferenceProject() =>
        _solution!.Projects
            .OrderByDescending(p => p.MetadataReferences.Count)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .First();

    // A compilation of the tree as a new file of the project: the project's references and
    // compilation options (nullable context, unsafe code, ...) as a library, and its global
    // using directives — but none of its other source.
    private async Task<Compilation> NewFileCompilationAsync(Project project, SyntaxTree tree, CancellationToken cancellationToken)
    {
        var projectCompilation = await GetProjectCompilationAsync(project, cancellationToken);
        var options = ((projectCompilation?.Options as CSharpCompilationOptions) ?? new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithOutputKind(OutputKind.DynamicallyLinkedLibrary);
        var trees = new List<SyntaxTree> { tree };
        if (projectCompilation != null)
        {
            var globalUsings = new StringBuilder();
            foreach (var projectTree in projectCompilation.SyntaxTrees)
            {
                if (await projectTree.GetRootAsync(cancellationToken) is not CompilationUnitSyntax unit) continue;
                foreach (var directive in unit.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
                {
                    globalUsings.AppendLine(directive.WithoutTrivia().ToFullString());
                }
            }
            if (globalUsings.Length > 0)
            {
                trees.Add(CSharpSyntaxTree.ParseText(globalUsings.ToString(), (CSharpParseOptions)tree.Options,
                    path: "ValidationGlobalUsings.g.cs", cancellationToken: cancellationToken));
            }
        }
        return CSharpCompilation.Create("ValidationAssembly", trees, projectCompilation?.References ?? project.MetadataReferences, options);
    }

    // The fully-qualified names (with generic arity) of the types a file declares, nested
    // types included: what decides whether a complete-file snippet is a new version of its
    // context file (it redeclares one of the file's types) or a new file beside it.
    private static HashSet<string> DeclaredTypeNames(SyntaxNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var (identifier, arity) = declaration switch
            {
                TypeDeclarationSyntax type => (type.Identifier.ValueText, type.Arity),
                EnumDeclarationSyntax @enum => (@enum.Identifier.ValueText, 0),
                DelegateDeclarationSyntax @delegate => (@delegate.Identifier.ValueText, @delegate.Arity),
                _ => ((string?)null, 0)
            };
            if (identifier == null) continue;
            var qualifiers = declaration.Ancestors().Select(ancestor => ancestor switch
            {
                BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
                TypeDeclarationSyntax outer => $"{outer.Identifier.ValueText}`{outer.Arity}",
                _ => null
            }).OfType<string>().Reverse();
            names.Add(string.Join(".", qualifiers.Append($"{identifier}`{arity}")));
        }
        return names;
    }

    // What a snippet is: a complete file (namespace or type declarations), else statements
    // when it parses as statements, else members when it parses as class members, else
    // statements (whose errors then describe the snippet).
    private static SnippetShape ClassifySnippet(string code, CSharpParseOptions parseOptions)
    {
        var probe = CSharpSyntaxTree.ParseText(code, parseOptions);
        if (probe.GetRoot() is CompilationUnitSyntax unit
            && unit.Members.Any(m => m is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
        {
            return SnippetShape.CompleteFile;
        }
        if (!SyntaxFactory.ParseStatement("{\n" + code + "\n}", options: parseOptions).GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return SnippetShape.Statements;
        }
        var members = CSharpSyntaxTree.ParseText("class ValidationProbe {\n" + code + "\n}", parseOptions);
        return members.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)
            ? SnippetShape.Statements
            : SnippetShape.Members;
    }

    // The scaffold around a statement or member snippet: the text before it (ending with
    // the indentation of its first line) and after it. fileUsings go before the namespace,
    // namespaceUsings inside it.
    private static (string Prefix, string Suffix) SnippetWrapper(
        SnippetShape shape, string fileUsings, string namespaceName, string namespaceUsings = "") =>
        shape == SnippetShape.Members
            ? ($"{fileUsings}\nnamespace {namespaceName} {{\n{namespaceUsings}    public class ValidationClass {{\n        ",
               "\n    }\n}")
            : ($"{fileUsings}\nnamespace {namespaceName} {{\n{namespaceUsings}    public class ValidationClass {{\n        public void ValidationMethod() {{\n            ",
               "\n        }\n    }\n}");

    // The usings a snippet without a context file gets, inside its namespace.
    private const string CommonNamespaceUsings = "    using System;\n    using System.Collections.Generic;\n    using System.Linq;\n";

    // Locate a type by its canonical fully-qualified name within a compilation —
    // source declarations AND referenced metadata, merged in the global namespace.
    // The name is already disambiguated by ResolveTypeByNameAsync, so this is an exact
    // match (no contains/first-wins guesswork).
    private static INamedTypeSymbol? FindTypeByFqn(Compilation compilation, string fullyQualifiedName)
    {
        foreach (var type in GetAllNamedTypes(compilation))
        {
            if (type.OriginalDefinition.ToDisplayString() == fullyQualifiedName) return type;
        }
        return null;
    }

    /// <summary>
    /// Validates if code would compile without writing to disk.
    /// </summary>
    public async Task<object> ValidateCodeAsync(
        string code, string? contextFilePath = null, bool standalone = false,
        CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // A snippet compiles in a project of the solution; with no projects at all there is
        // none to compile in. Report that directly instead of letting
        // ValidationReferenceProject() throw and surface as a misleading "Check syntax" error.
        if (!_solution!.Projects.Any())
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "The loaded solution has no projects to validate against",
                hint: "Load a solution that contains at least one project before calling validate_code.",
                context: new { projectCount = 0 });
        }

        try
        {
            // The compilations to check — one per target framework that compiles the
            // context file, else one of the reference project — each with the tree that
            // holds the snippet.
            var checks = new List<(Compilation Compilation, SyntaxTree Tree)>();
            Project contextProject;
            // Lines the wrapper scaffold adds before the user's snippet; subtracted
            // from diagnostic positions so reported lines map to the snippet itself.
            var snippetLineOffset = 0;
            // Indentation the wrapper adds on the line the snippet starts on, so a
            // diagnostic on the snippet's first line maps its column back too.
            var snippetColumnOffset = 0;

            if (!standalone && !string.IsNullOrEmpty(contextFilePath))
            {
                var document = TryFindDocument(contextFilePath);
                if (document == null)
                {
                    return CreateErrorResponse(
                        ErrorCodes.FileNotInSolution,
                        $"Context file not found: {contextFilePath}",
                        hint: "Check the file path or use standalone=true",
                        context: new { contextFilePath }
                    );
                }
                contextProject = document.Project;

                foreach (var linked in DocumentsSharingPath(document))
                {
                    // The snippet parses with the PROJECT's parse options: mixing a tree of
                    // another language version into the compilation throws "Inconsistent
                    // language versions".
                    var parseOptions = linked.Project.ParseOptions as CSharpParseOptions ?? CSharpParseOptions.Default;
                    var projectCompilation = await GetProjectCompilationAsync(linked.Project, cancellationToken);
                    var contextTree = await linked.GetSyntaxTreeAsync(cancellationToken);
                    if (projectCompilation == null || contextTree == null) continue;

                    var shape = ClassifySnippet(code, parseOptions);
                    if (shape == SnippetShape.CompleteFile)
                    {
                        // A complete file that redeclares a type of the context file is that
                        // file's new version: it replaces the file in the project, so its types
                        // do not collide with the version they replace. Any other complete file
                        // is a new file of the context file's project, compiled beside it.
                        var snippetTree = CSharpSyntaxTree.ParseText(code, parseOptions, cancellationToken: cancellationToken);
                        var contextRootForTypes = await contextTree.GetRootAsync(cancellationToken);
                        var isNewVersion = DeclaredTypeNames(await snippetTree.GetRootAsync(cancellationToken))
                            .Overlaps(DeclaredTypeNames(contextRootForTypes));
                        if (isNewVersion && projectCompilation.ContainsSyntaxTree(contextTree))
                        {
                            var replacement = CSharpSyntaxTree.ParseText(code, parseOptions, path: contextTree.FilePath, cancellationToken: cancellationToken);
                            checks.Add((projectCompilation.ReplaceSyntaxTree(contextTree, replacement), replacement));
                        }
                        else
                        {
                            checks.Add((projectCompilation.AddSyntaxTrees(snippetTree), snippetTree));
                        }
                        continue;
                    }

                    // Statements or members, wrapped in a class (and method) of the context
                    // file's namespace, with its usings.
                    var contextRoot = await contextTree.GetRootAsync(cancellationToken) as CompilationUnitSyntax;
                    var usings = contextRoot?.Usings.ToFullString() ?? "";
                    var namespaceName = contextRoot?.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                    var (prefix, suffix) = SnippetWrapper(shape, usings, string.IsNullOrEmpty(namespaceName) ? "ValidationNamespace" : namespaceName);
                    snippetLineOffset = prefix.Count(c => c == '\n');
                    snippetColumnOffset = prefix.Length - prefix.LastIndexOf('\n') - 1;
                    var wrapped = CSharpSyntaxTree.ParseText(prefix + code + suffix, parseOptions, cancellationToken: cancellationToken);
                    checks.Add((projectCompilation.AddSyntaxTrees(wrapped), wrapped));
                }
            }
            else
            {
                // A new file of the reference project: the snippet as a complete file
                // (standalone), or wrapped as statements or members with the common
                // usings (placed inside the namespace, so they never clash with the
                // project's global usings).
                contextProject = ValidationReferenceProject();
                var parseOptions = contextProject.ParseOptions as CSharpParseOptions ?? CSharpParseOptions.Default;
                SyntaxTree tree;
                if (standalone)
                {
                    tree = CSharpSyntaxTree.ParseText(code, parseOptions, cancellationToken: cancellationToken);
                }
                else
                {
                    var shape = ClassifySnippet(code, parseOptions);
                    var (prefix, suffix) = shape == SnippetShape.CompleteFile
                        ? ("", "")
                        : SnippetWrapper(shape, "", "ValidationNamespace", CommonNamespaceUsings);
                    snippetLineOffset = prefix.Count(c => c == '\n');
                    snippetColumnOffset = prefix.Length - prefix.LastIndexOf('\n') - 1;
                    tree = CSharpSyntaxTree.ParseText(prefix + code + suffix, parseOptions, cancellationToken: cancellationToken);
                }
                checks.Add((await NewFileCompilationAsync(contextProject, tree, cancellationToken), tree));
            }

            // Map a wrapped-file line back to the snippet: subtract the scaffold
            // prefix and clamp into the snippet's range (a brace left open in the
            // snippet errors at the wrapper suffix — its closest snippet line is
            // the last one).
            var snippetLastLine = code.Count(c => c == '\n');
            int MapLine(Diagnostic d)
            {
                var line = d.Location.GetLineSpan().StartLinePosition.Line - snippetLineOffset;
                return Math.Max(0, Math.Min(line, snippetLastLine));
            }
            int MapColumn(Diagnostic d)
            {
                var column = d.Location.GetLineSpan().StartLinePosition.Character;
                // The snippet's first line begins after the wrapper's trailing indent
                // on the same physical line, so only line 0 carries that column offset.
                return MapLine(d) == 0 ? Math.Max(0, column - snippetColumnOffset) : column;
            }

            // The compilation carries the project; only diagnostics located in the
            // validated tree are about the snippet. Location-less diagnostics (e.g.
            // CS5001 'no Main', analyzer/config errors) are project-wide, not about the
            // snippet, so they are excluded. A diagnostic every target framework reports
            // is listed once.
            var diagnostics = new List<(string Id, string Message, int Line, int Column, DiagnosticSeverity Severity)>();
            foreach (var (compilation, tree) in checks)
            {
                foreach (var d in compilation.GetDiagnostics(cancellationToken).Where(d => d.Location.SourceTree == tree))
                {
                    var entry = (d.Id, d.GetMessage(), MapLine(d), MapColumn(d), d.Severity);
                    if (!diagnostics.Contains(entry)) diagnostics.Add(entry);
                }
            }
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => new { id = d.Id, message = d.Message, line = d.Line, column = d.Column })
                .ToList();
            var warnings = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Warning)
                .Select(d => new { id = d.Id, message = d.Message, line = d.Line, column = d.Column })
                .ToList();

            return CreateSuccessResponse(
                data: new
                {
                    compiles = errors.Count == 0,
                    errorCount = errors.Count,
                    warningCount = warnings.Count,
                    contextProject = GetLogicalProjects().Of(contextProject).Name,
                    errors,
                    warnings
                },
                suggestedNextTools: errors.Count > 0
                    ? new[] { "validate_code again after correcting the reported errors" }
                    : new[] { "sync_documents after writing the code to a file, then get_diagnostics to confirm it in context" }
            );
        }
        catch (OperationCanceledException)
        {
            // A per-call timeout must surface as the dispatcher's TIMEOUT, not be masked
            // as a misleading "Check syntax" AnalysisFailed.
            throw;
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"Validation failed: {ex.Message}",
                hint: "Check syntax of the code snippet",
                context: new { codeLength = code.Length }
            );
        }
    }

    /// <summary>
    /// Checks if one type can be assigned to another.
    /// </summary>
    public async Task<object> CheckTypeCompatibilityAsync(
        string sourceType, string targetType, CancellationToken cancellationToken = default)
    {
        EnsureSolutionLoaded();

        // Resolve each name to a canonical type up front through the shared,
        // ambiguity-aware resolver — a name matching several distinct types errors
        // here instead of being silently resolved to an arbitrary first match. A name
        // resolved to a source type that shadows same-named framework types is disclosed
        // in notice.
        var sourceResolution = await ResolveTypeByNameAsync(sourceType, cancellationToken);
        if (sourceResolution.Error != null) return sourceResolution.Error;
        if (sourceResolution.Type is not { } sourceSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Source type not found: {sourceType}",
                hint: "Use fully qualified name or check spelling",
                context: new { sourceType });
        }
        var targetResolution = await ResolveTypeByNameAsync(targetType, cancellationToken);
        if (targetResolution.Error != null) return targetResolution.Error;
        if (targetResolution.Type is not { } targetSymbol)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Target type not found: {targetType}",
                hint: "Use fully qualified name or check spelling",
                context: new { targetType });
        }
        var notices = new[] { sourceResolution.ShadowNotice, targetResolution.ShadowNotice }.OfType<string>().Distinct().ToList();
        var notice = notices.Count > 0 ? string.Join(" ", notices) : null;

        // ClassifyConversion only gives correct answers for symbols from the SAME
        // compilation — resolving each type from whichever project happens to have
        // it and classifying in an unrelated compilation misreports cross-project
        // pairs as incompatible. Find one compilation that can see both canonical
        // types (matched by their fully-qualified name, including referenced metadata).
        var sourceFqn = sourceSymbol.OriginalDefinition.ToDisplayString();
        var targetFqn = targetSymbol.OriginalDefinition.ToDisplayString();
        INamedTypeSymbol? source = null;
        INamedTypeSymbol? target = null;
        Compilation? owningCompilation = null;

        foreach (var project in _solution!.Projects)
        {
            var compilation = await GetProjectCompilationAsync(project, cancellationToken);
            if (compilation == null) continue;

            var sourceHere = FindTypeByFqn(compilation, sourceFqn);
            var targetHere = FindTypeByFqn(compilation, targetFqn);
            if (sourceHere != null && targetHere != null)
            {
                source = sourceHere;
                target = targetHere;
                owningCompilation = compilation;
                break;
            }
        }

        if (owningCompilation == null || source == null || target == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                $"No single project can see both '{sourceType}' and '{targetType}'",
                hint: "Conversion classification requires one compilation that references both types. Ensure some project references both types' projects.",
                context: new { sourceType, targetType }
            );
        }

        var conversion = owningCompilation.ClassifyConversion(source, target);


        string conversionKind;
        string reason;

        if (conversion.IsIdentity)
        {
            conversionKind = "Identity";
            reason = "Same type";
        }
        else if (conversion.IsImplicit)
        {
            if (conversion.IsReference)
            {
                conversionKind = "ImplicitReference";
                reason = $"{sourceType} inherits from or implements {targetType}";
            }
            else if (conversion.IsNumeric)
            {
                conversionKind = "ImplicitNumeric";
                reason = "Numeric widening conversion";
            }
            else if (conversion.IsBoxing)
            {
                conversionKind = "Boxing";
                reason = "Value type to object/interface";
            }
            else
            {
                conversionKind = "Implicit";
                reason = "Implicit conversion available";
            }
        }
        else if (conversion.IsExplicit)
        {
            if (conversion.IsUnboxing)
            {
                conversionKind = "Unboxing";
                reason = "Requires explicit cast (unboxing)";
            }
            else if (conversion.IsNumeric)
            {
                conversionKind = "ExplicitNumeric";
                reason = "Numeric narrowing - may lose precision";
            }
            else
            {
                conversionKind = "Explicit";
                reason = "Requires explicit cast";
            }
        }
        else
        {
            conversionKind = "None";
            reason = "No conversion exists between these types";
        }

        return CreateSuccessResponse(
            data: new
            {
                sourceType = source.ToDisplayString(),
                targetType = target.ToDisplayString(),
                compatible = conversion.Exists,
                requiresCast = conversion.IsExplicit,
                conversionKind,
                reason,
                isReferenceConversion = conversion.IsReference,
                isNumericConversion = conversion.IsNumeric,
                isBoxing = conversion.IsBoxing,
                isUnboxing = conversion.IsUnboxing,
                notice
            },
            suggestedNextTools: new[]
            {
                "get_base_types to see inheritance chain",
                "get_type_members to see available members"
            }
        );
    }
}
