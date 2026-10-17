using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLensMcp.Tests;

// Test-only analyzer that reports TEST0100 (Info) once per file and records, on this
// instance, the path of every syntax tree whose semantic model it was run on — so a test
// can assert which files a file-scoped diagnostics request actually analyzed.
internal sealed class TreeRecordingAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TEST0100";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId, "Recorded file", "File '{0}' was analyzed", "Test", DiagnosticSeverity.Info, isEnabledByDefault: true);

    public ConcurrentBag<string> AnalyzedFiles { get; } = new();

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSemanticModelAction(c =>
        {
            var path = c.SemanticModel.SyntaxTree.FilePath;
            AnalyzedFiles.Add(Path.GetFileName(path));
            c.ReportDiagnostic(Diagnostic.Create(Rule,
                Location.Create(c.SemanticModel.SyntaxTree, new Microsoft.CodeAnalysis.Text.TextSpan(0, 0)),
                Path.GetFileName(path)));
        });
    }
}
