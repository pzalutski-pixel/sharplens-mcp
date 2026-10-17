using Microsoft.CodeAnalysis;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Emits Dup.g.cs, but only when the consuming compilation declares Demo.DuplicateHintMarker.
// Together with DuplicateHintSecondSourceGenerator it gives one project two generated files
// with the same hint name under different generator folders, so tests can exercise how
// get_generated_code reports a file name that matches more than one generated file.
[Generator]
public sealed class DuplicateHintFirstSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.Compilation.GetTypeByMetadataName("Demo.DuplicateHintMarker") == null) return;
        context.AddSource("Dup.g.cs", @"
namespace Demo.First
{
    public static class FromFirst { }
}");
    }
}
