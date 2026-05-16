using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// Code-validation tools: standalone-compile check and type-compatibility check.
public partial class RoslynService
{
    /// <summary>
    /// Validates if code would compile without writing to disk.
    /// </summary>
    public async Task<object> ValidateCodeAsync(string code, string? contextFilePath = null, bool standalone = false)
    {
        EnsureSolutionLoaded();

        try
        {
            SyntaxTree syntaxTree;
            Compilation compilation;

            if (standalone)
            {
                // Treat as complete file
                syntaxTree = CSharpSyntaxTree.ParseText(code);
                var references = _solution!.Projects.First().MetadataReferences;
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

                var existingTree = await document.GetSyntaxTreeAsync();
                var existingRoot = await existingTree!.GetRootAsync() as CompilationUnitSyntax;

                // Parse the new code
                var newCode = CSharpSyntaxTree.ParseText(code);
                var newRoot = await newCode.GetRootAsync();

                // Get usings from context
                var usings = existingRoot?.Usings.ToFullString() ?? "";
                var ns = existingRoot?.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                var nsName = ns?.Name.ToString() ?? "";

                // Wrap code in namespace context
                var wrappedCode = $@"
{usings}
namespace {(string.IsNullOrEmpty(nsName) ? "ValidationNamespace" : nsName)} {{
    public class ValidationClass {{
        public void ValidationMethod() {{
            {code}
        }}
    }}
}}";
                syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);
                var project = document.Project;
                compilation = (await GetProjectCompilationAsync(project))!
                    .AddSyntaxTrees(syntaxTree);
            }
            else
            {
                // No context - wrap in minimal class
                var wrappedCode = $@"
using System;
using System.Collections.Generic;
using System.Linq;

public class ValidationClass {{
    public void ValidationMethod() {{
        {code}
    }}
}}";
                syntaxTree = CSharpSyntaxTree.ParseText(wrappedCode);
                var references = _solution!.Projects.First().MetadataReferences;
                compilation = CSharpCompilation.Create(
                    "ValidationAssembly",
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );
            }

            var diagnostics = compilation.GetDiagnostics();
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => new
                {
                    id = d.Id,
                    message = d.GetMessage(),
                    line = d.Location.GetLineSpan().StartLinePosition.Line,
                    column = d.Location.GetLineSpan().StartLinePosition.Character
                })
                .ToList();

            var warnings = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Warning)
                .Select(d => new
                {
                    id = d.Id,
                    message = d.GetMessage(),
                    line = d.Location.GetLineSpan().StartLinePosition.Line,
                    column = d.Location.GetLineSpan().StartLinePosition.Character
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
                    ? new[] { "Fix the errors and validate again" }
                    : new[] { "Code is valid - safe to write to file" }
            );
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
    public async Task<object> CheckTypeCompatibilityAsync(string sourceType, string targetType)
    {
        EnsureSolutionLoaded();

        var source = await FindTypeByNameAsync(sourceType);
        if (source == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Source type not found: {sourceType}",
                hint: "Use fully qualified name or check spelling",
                context: new { sourceType }
            );
        }

        var target = await FindTypeByNameAsync(targetType);
        if (target == null)
        {
            return CreateErrorResponse(
                ErrorCodes.TypeNotFound,
                $"Target type not found: {targetType}",
                hint: "Use fully qualified name or check spelling",
                context: new { targetType }
            );
        }

        var compilation = await GetProjectCompilationAsync(_solution!.Projects.First());
        if (compilation == null)
        {
            return CreateErrorResponse(
                ErrorCodes.AnalysisFailed,
                "Could not get compilation",
                context: new { sourceType, targetType }
            );
        }

        var conversion = compilation.ClassifyConversion(source, target);

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
