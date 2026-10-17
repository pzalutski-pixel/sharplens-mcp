using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SharpLensMcp.Tests.Mcp;

// Builds, at test time, an analyzer assembly that writes a line to Console.Out from inside
// the host process and reports one diagnostic per compilation. Emitted on the fly so the
// stdout-purity test needs no checked-in binary and no change to the shared test analyzers.
internal static class StdoutWritingAnalyzer
{
    public const string DiagnosticId = "STDOUTPROBE1";
    public const string ProbeText = "ANALYZER-STDOUT-PROBE";

    private const string Source = $$"""
        using System.Collections.Immutable;
        using Microsoft.CodeAnalysis;
        using Microsoft.CodeAnalysis.Diagnostics;

        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        public sealed class StdoutWriter : DiagnosticAnalyzer
        {
            private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
                "{{DiagnosticId}}", "probe", "probe", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

            public override void Initialize(AnalysisContext context)
            {
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                context.RegisterCompilationAction(c =>
                {
                    System.Console.Out.WriteLine("{{ProbeText}}");
                    System.Console.Out.Flush();
                    c.ReportDiagnostic(Diagnostic.Create(Rule, Location.None));
                });
            }
        }
        """;

    // Returns the path of the emitted StdoutProbe.dll under the given directory.
    public static string Emit(string directory)
    {
        Directory.CreateDirectory(directory);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer).Assembly.Location))
            .GroupBy(reference => Path.GetFileName(reference.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        var compilation = CSharpCompilation.Create(
            "StdoutProbe",
            new[] { CSharpSyntaxTree.ParseText(Source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(directory, "StdoutProbe.dll");
        var result = compilation.Emit(path);
        result.Success.Should().BeTrue(
            "the probe analyzer must compile: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }
}
