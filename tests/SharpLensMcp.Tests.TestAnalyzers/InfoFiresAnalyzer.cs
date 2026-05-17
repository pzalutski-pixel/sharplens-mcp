using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Test-only analyzer: emits TEST0002 at Info severity once per method declaration.
// Used by GetDiagnosticsFilterTests to lock the severity="Info" filter against a
// known source — without this, the Info severity branch can only be verified by
// vacuous "filter returned 0 entries or all entries are Info" assertions.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InfoFiresAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TEST0002";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Test rule fires at Info severity on every method",
        messageFormat: "Method '{0}' triggers TEST0002 (Info)",
        category: "Test",
        defaultSeverity: DiagnosticSeverity.Info,
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
