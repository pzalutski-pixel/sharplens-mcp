using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLensMcp.Tests;

// Builds, at test time, an analyzer assembly in the shape of an analyzer NuGet package:
// one DiagnosticAnalyzer (TEST9001 at Warning on every method whose name starts with
// "Fixable") and, in the same assembly, the [ExportCodeFixProvider] that fixes it by
// renaming the method to "Fixed<rest>". It is attached to a project as an
// AnalyzerFileReference — the reference type MSBuildWorkspace gives package analyzers —
// so the code-fix tools must discover the fixer from the analyzer assembly itself.
internal static class EmittedFixPackage
{
    public const string DiagnosticId = "TEST9001";
    public const string FixTitle = "Rename to Fixed*";

    private const string Source = $$"""
        using System.Collections.Immutable;
        using System.Composition;
        using System.Linq;
        using System.Threading.Tasks;
        using Microsoft.CodeAnalysis;
        using Microsoft.CodeAnalysis.CodeActions;
        using Microsoft.CodeAnalysis.CodeFixes;
        using Microsoft.CodeAnalysis.CSharp;
        using Microsoft.CodeAnalysis.CSharp.Syntax;
        using Microsoft.CodeAnalysis.Diagnostics;

        [DiagnosticAnalyzer(LanguageNames.CSharp)]
        public sealed class FixableMethodAnalyzer : DiagnosticAnalyzer
        {
            public static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
                "{{DiagnosticId}}", "Fixable method", "Method '{0}' is fixable", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

            public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

            public override void Initialize(AnalysisContext context)
            {
                context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                context.EnableConcurrentExecution();
                context.RegisterSyntaxNodeAction(c =>
                {
                    var method = (MethodDeclarationSyntax)c.Node;
                    if (method.Identifier.ValueText.StartsWith("Fixable"))
                        c.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), method.Identifier.ValueText));
                }, SyntaxKind.MethodDeclaration);
            }
        }

        [ExportCodeFixProvider(LanguageNames.CSharp), Shared]
        public sealed class FixableMethodFixer : CodeFixProvider
        {
            public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("{{DiagnosticId}}");

            public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

            public override async Task RegisterCodeFixesAsync(CodeFixContext context)
            {
                var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken);
                var token = root.FindToken(context.Span.Start);
                context.RegisterCodeFix(CodeAction.Create("{{FixTitle}}", ct =>
                {
                    var renamed = SyntaxFactory.Identifier("Fixed" + token.ValueText.Substring("Fixable".Length))
                        .WithTriviaFrom(token);
                    return Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceToken(token, renamed)));
                }, "{{FixTitle}}"), context.Diagnostics);
            }
        }
        """;

    // Emits FixPackage.dll under the directory and returns it as an AnalyzerFileReference.
    public static AnalyzerFileReference Emit(string directory)
    {
        Directory.CreateDirectory(directory);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Concat(new[]
            {
                typeof(DiagnosticAnalyzer).Assembly.Location,
                typeof(CSharpSyntaxTree).Assembly.Location,
                typeof(Microsoft.CodeAnalysis.CodeFixes.CodeFixProvider).Assembly.Location,
                typeof(System.Composition.SharedAttribute).Assembly.Location,
            })
            .Select(path => MetadataReference.CreateFromFile(path))
            .GroupBy(reference => Path.GetFileName(reference.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        var compilation = CSharpCompilation.Create(
            "FixPackage" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(Source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var path = Path.Combine(directory, "FixPackage.dll");
        var result = compilation.Emit(path);
        result.Success.Should().BeTrue(
            "the fix package must compile: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return new AnalyzerFileReference(path, new LoadFromPathAnalyzerLoader());
    }
}
