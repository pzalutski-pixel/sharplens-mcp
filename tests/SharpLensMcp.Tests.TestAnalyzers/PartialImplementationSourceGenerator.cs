using Microsoft.CodeAnalysis;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Emits a second declaration of the partial class Demo.GenPartialHost carrying the
// implementation part of its partial method Compute, but only when the consuming
// compilation declares that type — so the generator stays inert for every other
// workspace that loads this analyzer DLL. Lets tests exercise name-based addressing of
// a member whose implementation lives only in generated code (the [GeneratedRegex] /
// [LoggerMessage] shape): the user-authored declaration is the only part in the solution.
[Generator]
public sealed class PartialImplementationSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.Compilation.GetTypeByMetadataName("Demo.GenPartialHost") == null) return;
        context.AddSource("GenPartialHost.g.cs", @"
namespace Demo
{
    public partial class GenPartialHost
    {
        public partial int Compute(int x) => x + 1;
    }
}");
    }
}
