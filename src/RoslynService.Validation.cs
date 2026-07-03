using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// Code-validation tools: standalone-compile check and type-compatibility check.
public partial class RoslynService
{
    // References for a standalone/no-context validation compilation. The richest
    // project's reference set (most likely to resolve a snippet's types) is chosen
    // deterministically, rather than the arbitrary first project in the solution.
    private IReadOnlyList<MetadataReference> StandaloneValidationReferences() =>
        _solution!.Projects
            .OrderByDescending(p => p.MetadataReferences.Count)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .First()
            .MetadataReferences.ToList();

    // The language version the loaded projects parse at, used so standalone
    // validation matches what they would accept. Falls back to the compiler default
    // when no project declares one explicitly.
    private LanguageVersion SolutionLanguageVersion() =>
        _solution!.Projects
            .Select(p => (p.ParseOptions as CSharpParseOptions)?.LanguageVersion)
            .FirstOrDefault(v => v != null) ?? LanguageVersion.Default;

    // Locate a type by its canonical fully-qualified name within a compilation —
    // source declarations AND referenced metadata, merged in the global namespace.
    // The name is already disambiguated by FindTypeByNameAsync, so this is an exact
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

        // Standalone validation sources its metadata references from the richest loaded
        // project; with no projects at all there are none to draw from. Report that
        // directly instead of letting StandaloneValidationReferences().First() throw and
        // surface as a misleading "Check syntax" error.
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
            SyntaxTree syntaxTree;
            Compilation compilation;
            // Lines the wrapper scaffold adds before the user's snippet; subtracted
            // from diagnostic positions so reported lines map to the snippet itself.
            var snippetLineOffset = 0;
            // Indentation the wrapper adds on the line the snippet starts on, so a
            // diagnostic on the snippet's first line maps its column back too.
            var snippetColumnOffset = 0;

            if (standalone)
            {
                // Treat as complete file, parsed at the loaded solution's language
                // version so the snippet is held to the same syntax the real projects
                // accept rather than the newest the compiler understands.
                syntaxTree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(SolutionLanguageVersion()));
                var references = StandaloneValidationReferences();
                compilation = CSharpCompilation.Create(
                    "ValidationAssembly",
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );
            }
            else if (!string.IsNullOrEmpty(contextFilePath))
            {
                // Inject into context of existing file
                Document document;
                try
                {
                    document = await GetDocumentAsync(contextFilePath);
                }
                catch (FileNotFoundException)
                {
                    return CreateErrorResponse(
                        ErrorCodes.FileNotInSolution,
                        $"Context file not found: {contextFilePath}",
                        hint: "Check the file path or use standalone=true",
                        context: new { contextFilePath }
                    );
                }

                var project = document.Project;
                // The snippet must parse with the PROJECT's language version:
                // mixing a default-parsed (Latest) tree into a compilation whose
                // trees carry the project's LangVersion throws "Inconsistent
                // language versions" from AddSyntaxTrees.
                var contextParseOptions = project.ParseOptions as CSharpParseOptions
                    ?? CSharpParseOptions.Default;

                // A snippet that is already a complete file (own usings/
                // namespace/type declarations) joins the project compilation as
                // its own tree — wrapping it inside a method body would only
                // manufacture syntax errors.
                var probe = CSharpSyntaxTree.ParseText(code, contextParseOptions, cancellationToken: cancellationToken);
                var probeRoot = await probe.GetRootAsync(cancellationToken) as CompilationUnitSyntax;
                if (probeRoot != null && probeRoot.Members.Any(
                        m => m is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax))
                {
                    syntaxTree = probe;
                }
                else
                {
                    var existingTree = await document.GetSyntaxTreeAsync(cancellationToken);
                    var existingRoot = await existingTree!.GetRootAsync(cancellationToken) as CompilationUnitSyntax;

                    // Get usings from context
                    var usings = existingRoot?.Usings.ToFullString() ?? "";
                    var ns = existingRoot?.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                    var nsName = ns?.Name.ToString() ?? "";

                    // Wrap code in namespace context
                    var wrapperPrefix = $@"
{usings}
namespace {(string.IsNullOrEmpty(nsName) ? "ValidationNamespace" : nsName)} {{
    public class ValidationClass {{
        public void ValidationMethod() {{
            ";
                    var wrappedCode = wrapperPrefix + code + @"
        }
    }
}";
                    snippetLineOffset = wrapperPrefix.Count(c => c == '\n');
                    snippetColumnOffset = wrapperPrefix.Length - wrapperPrefix.LastIndexOf('\n') - 1;
                    syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode, contextParseOptions, cancellationToken: cancellationToken);
                }
                compilation = (await GetProjectCompilationAsync(project, cancellationToken))!
                    .AddSyntaxTrees(syntaxTree);
            }
            else
            {
                // No context - wrap in minimal class
                var wrapperPrefix = @"
using System;
using System.Collections.Generic;
using System.Linq;

public class ValidationClass {
    public void ValidationMethod() {
        ";
                var wrappedCode = wrapperPrefix + code + @"
    }
}";
                snippetLineOffset = wrapperPrefix.Count(c => c == '\n');
                snippetColumnOffset = wrapperPrefix.Length - wrapperPrefix.LastIndexOf('\n') - 1;
                syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);
                var references = StandaloneValidationReferences();
                compilation = CSharpCompilation.Create(
                    "ValidationAssembly",
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );
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

            // In context mode the compilation carries the whole project; only
            // diagnostics located in the validated tree are about the snippet.
            // Location-less diagnostics (e.g. CS5001 'no Main', analyzer/config errors)
            // are project-wide, not about the snippet, so they are excluded.
            var diagnostics = compilation.GetDiagnostics(cancellationToken)
                .Where(d => d.Location.SourceTree == syntaxTree)
                .ToList();
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => new
                {
                    id = d.Id,
                    message = d.GetMessage(),
                    line = MapLine(d),
                    column = MapColumn(d)
                })
                .ToList();

            var warnings = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Warning)
                .Select(d => new
                {
                    id = d.Id,
                    message = d.GetMessage(),
                    line = MapLine(d),
                    column = MapColumn(d)
                })
                .ToList();

            return CreateSuccessResponse(
                data: new
                {
                    compiles = errors.Count == 0,
                    errorCount = errors.Count,
                    warningCount = warnings.Count,
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
        // here instead of being silently resolved to an arbitrary first match.
        var (sourceSymbol, sourceError) = await FindTypeByNameAsync(sourceType, cancellationToken);
        if (sourceError != null) return sourceError;
        if (sourceSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Source type not found: {sourceType}",
                hint: "Use fully qualified name or check spelling",
                context: new { sourceType });
        }
        var (targetSymbol, targetError) = await FindTypeByNameAsync(targetType, cancellationToken);
        if (targetError != null) return targetError;
        if (targetSymbol == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Target type not found: {targetType}",
                hint: "Use fully qualified name or check spelling",
                context: new { targetType });
        }

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
                isUnboxing = conversion.IsUnboxing
            },
            suggestedNextTools: new[]
            {
                "get_base_types to see inheritance chain",
                "get_type_members to see available members"
            }
        );
    }
}
