using Microsoft.CodeAnalysis;

namespace SharpLensMcp.Tests.TestAnalyzers;

// The second generator of the Dup.g.cs pair (see DuplicateHintFirstSourceGenerator):
// the same hint name, a different generator folder and content.
[Generator]
public sealed class DuplicateHintSecondSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.Compilation.GetTypeByMetadataName("Demo.DuplicateHintMarker") == null) return;
        context.AddSource("Dup.g.cs", @"
namespace Demo.Second
{
    public static class FromSecond { }
}");
    }
}
