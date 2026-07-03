using Microsoft.CodeAnalysis;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Emits a class implementing Demo.IGenMarker, but only when the consuming compilation
// declares that marker interface — so the generator stays inert for every other workspace
// that loads this analyzer DLL (e.g. the generated-caller tests). Lets tests exercise the
// generated-implementer branch of find_implementations against a real, SymbolFinder-
// recognized generator (loaded via AnalyzerFileReference / a project Analyzer reference).
[Generator]
public sealed class ImplementerSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.Compilation.GetTypeByMetadataName("Demo.IGenMarker") == null) return;
        context.AddSource("GenImpl.g.cs", @"
namespace Demo
{
    public class GenImpl : IGenMarker { }
}");
    }
}
