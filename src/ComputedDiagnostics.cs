using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLensMcp;

// A suppression-aware diagnostic set (RoslynService.ComputeProjectDiagnosticsAsync /
// ComputeTreeDiagnosticsAsync) and the analyzers that ran to produce it, suppressors
// included; Ran is empty when no analyzer ran.
internal sealed record ComputedDiagnostics(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<DiagnosticAnalyzer> Ran);
