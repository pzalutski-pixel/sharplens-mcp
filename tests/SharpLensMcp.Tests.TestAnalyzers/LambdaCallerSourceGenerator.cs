using Microsoft.CodeAnalysis;

namespace SharpLensMcp.Tests.TestAnalyzers;

// Emits a method whose LAMBDA calls Demo.LambdaTarget.Hit, but only when the consuming
// compilation declares Demo.LambdaTarget — so the generator stays inert for every other
// workspace that loads this analyzer DLL. Lets tests check that a call inside generated
// code is attributed to the generated member containing it (GenLambdaCaller.Make), not
// to the lambda, the shape serializer generators emit.
[Generator]
public sealed class LambdaCallerSourceGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context) { }

    public void Execute(GeneratorExecutionContext context)
    {
        if (context.Compilation.GetTypeByMetadataName("Demo.LambdaTarget") == null) return;
        context.AddSource("GenLambdaCaller.g.cs", @"
namespace Demo
{
    public static class GenLambdaCaller
    {
        public static System.Func<int> Make() => () => new LambdaTarget().Hit();
    }
}");
    }
}
