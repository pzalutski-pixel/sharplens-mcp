using Microsoft.CodeAnalysis;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Emits a method that calls Demo.Target.Hit, so tests can exercise the
// generated-caller paths of find_callers / get_call_graph / analyze_method against a
// real, SymbolFinder-recognized generator (loaded via AnalyzerFileReference). The
// consuming workspace must declare Demo.Target.Hit for the generated call to bind.
[Generator]
public sealed class CallerSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context) =>
        context.AddSource("GenCaller.g.cs", @"
namespace Demo
{
    public static class GenCaller
    {
        public static int Invoke() => new Target().Hit();
    }
}");
}
