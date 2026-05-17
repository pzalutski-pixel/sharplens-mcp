using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Test-only analyzer: emits TEST0003 at Hidden severity once per method declaration.
// Used by GetDiagnosticsFilterTests to lock the includeHidden=true / includeHidden=false
// branch behavior — Hidden diagnostics surface only when includeHidden=true.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HiddenFiresAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TEST0003";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Test rule fires at Hidden severity on every method",
        messageFormat: "Method '{0}' triggers TEST0003 (Hidden)",
        category: "Test",
        defaultSeverity: DiagnosticSeverity.Hidden,
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
