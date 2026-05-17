using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Test-only analyzer: emits TEST0001 once per method declaration. Used by
// SharpLensMcp.Tests.AnalyzerDiagnosticsTests to verify that get_diagnostics
// actually invokes the analyzer pipeline (compiler alone wouldn't emit this rule).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AlwaysFiresAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TEST0001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Test rule fires on every method",
        messageFormat: "Method '{0}' triggers TEST0001",
        category: "Test",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(ctx =>
        {
            var method = (MethodDeclarationSyntax)ctx.Node;
            ctx.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), method.Identifier.Text));
        }, SyntaxKind.MethodDeclaration);
    }
}
