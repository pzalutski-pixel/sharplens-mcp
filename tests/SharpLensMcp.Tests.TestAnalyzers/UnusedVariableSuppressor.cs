using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Test-only DiagnosticSuppressor: suppresses the compiler's CS0168 ("variable declared but
// never used") for variables whose name starts with "suppressed", the way framework
// suppressors (e.g. MSTest's for its TestContext property) silence compiler warnings in
// a real build. Used to lock that get_diagnostics and the code-fix tools apply
// suppressors to compiler diagnostics as dotnet build does.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnusedVariableSuppressor : DiagnosticSuppressor
{
    public const string SuppressionId = "TESTSPR0001";
    public const string SuppressedPrefix = "suppressed";

    private static readonly SuppressionDescriptor Rule = new(
        SuppressionId, suppressedDiagnosticId: "CS0168", justification: "Test suppression of unused variables named suppressed*");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => ImmutableArray.Create(Rule);

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var tree = diagnostic.Location.SourceTree;
            if (tree == null) continue;
            var text = tree.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan);
            if (text.StartsWith(SuppressedPrefix, System.StringComparison.Ordinal))
            {
                context.ReportSuppression(Suppression.Create(Rule, diagnostic));
            }
        }
    }
}
