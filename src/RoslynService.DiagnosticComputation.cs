using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SharpLensMcp;

// The diagnostics every diagnostics-reading tool sees, computed the way `dotnet build` sees
// them: compiler diagnostics and analyzer diagnostics in one analyzer-driver pass, with the
// project's DiagnosticSuppressors applied to ALL of them (a suppressed diagnostic is not
// reported). Suppressors always run, whether or not the caller asked for analyzers: they
// decide what the build reports, not what extra findings exist.
//   - Project scope: one CompilationWithAnalyzers.GetAllDiagnosticsAsync over the
//     generator-aware compilation — compiler diagnostics are computed once, inside the pass.
//   - Tree scope (one file): the compiler diagnostics of that tree, and the analyzers run on
//     that tree only (its syntax and semantic-model actions; suppressions are applied to
//     their diagnostics by the driver). A suppressor can only see compiler diagnostics in a
//     full-compilation pass, so when the project has a suppressor for an id among the tree's
//     compiler diagnostics, the compiler diagnostics are taken from a project pass that runs
//     the suppressors alone.
// The code-fix tools also take the fixers the project's analyzer assemblies export
// ([ExportCodeFixProvider] types in an AnalyzerFileReference's assembly — how analyzer
// packages ship their fixes), next to the built-in MEF providers.
public partial class RoslynService
{
    // Every analyzer the project's analyzer references provide for C#.
    private static ImmutableArray<DiagnosticAnalyzer> ProjectAnalyzers(Project project) =>
        project.AnalyzerReferences.SelectMany(r => r.GetAnalyzers(LanguageNames.CSharp)).ToImmutableArray();

    private static CompilationWithAnalyzers WithAnalyzers(Compilation compilation, Project project, ImmutableArray<DiagnosticAnalyzer> analyzers) =>
        compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
            new AnalyzerOptions(project.AnalyzerOptions.AdditionalFiles, project.AnalyzerOptions.AnalyzerConfigOptionsProvider),
            onAnalyzerException: null,
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false,
            reportSuppressedDiagnostics: false));

    // The project's diagnostics: compiler diagnostics plus, when runAnalyzers, every
    // analyzer's — suppressors applied to all of them.
    internal async Task<ComputedDiagnostics> ComputeProjectDiagnosticsAsync(
        Project project, bool runAnalyzers, CancellationToken cancellationToken)
    {
        var compilation = await GetProjectCompilationAsync(project, cancellationToken);
        if (compilation == null) return new ComputedDiagnostics(Array.Empty<Diagnostic>(), Array.Empty<DiagnosticAnalyzer>());

        var all = ProjectAnalyzers(project);
        var analyzers = runAnalyzers ? all : all.Where(a => a is DiagnosticSuppressor).ToImmutableArray();
        if (analyzers.IsEmpty)
        {
            return new ComputedDiagnostics(compilation.GetDiagnostics(cancellationToken), Array.Empty<DiagnosticAnalyzer>());
        }

        try
        {
            var diagnostics = await WithAnalyzers(compilation, project, analyzers).GetAllDiagnosticsAsync(cancellationToken);
            return new ComputedDiagnostics(diagnostics.Where(d => !d.IsSuppressed).ToList(), analyzers);
        }
        catch (OperationCanceledException)
        {
            // A per-call timeout during analyzer execution surfaces as cancellation, not as a
            // compiler-only result.
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Warning] Analyzer execution failed for '{project.Name}': {ex.Message}");
            return new ComputedDiagnostics(compilation.GetDiagnostics(cancellationToken), Array.Empty<DiagnosticAnalyzer>());
        }
    }

    // The diagnostics of one syntax tree of the project: its compiler diagnostics plus, when
    // runAnalyzers, the analyzer diagnostics of that tree — suppressors applied to all.
    internal async Task<ComputedDiagnostics> ComputeTreeDiagnosticsAsync(
        Project project, SyntaxTree tree, bool runAnalyzers, CancellationToken cancellationToken)
    {
        var compilation = await GetProjectCompilationAsync(project, cancellationToken);
        if (compilation == null || !compilation.ContainsSyntaxTree(tree))
        {
            return new ComputedDiagnostics(Array.Empty<Diagnostic>(), Array.Empty<DiagnosticAnalyzer>());
        }

        var all = ProjectAnalyzers(project);
        var suppressors = all.OfType<DiagnosticSuppressor>().ToImmutableArray();
        IReadOnlyList<Diagnostic> compilerDiagnostics = compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: cancellationToken);

        var suppressible = suppressors
            .SelectMany(s => s.SupportedSuppressions.Select(d => d.SuppressedDiagnosticId))
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<DiagnosticAnalyzer> suppressorsRan = Array.Empty<DiagnosticAnalyzer>();
        if (compilerDiagnostics.Any(d => suppressible.Contains(d.Id)))
        {
            try
            {
                var suppressed = await WithAnalyzers(compilation, project, suppressors.CastArray<DiagnosticAnalyzer>())
                    .GetAllDiagnosticsAsync(cancellationToken);
                compilerDiagnostics = suppressed.Where(d => !d.IsSuppressed && d.Location.SourceTree == tree).ToList();
                suppressorsRan = suppressors.CastArray<DiagnosticAnalyzer>();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Warning] Diagnostic suppressors failed for '{project.Name}': {ex.Message}");
            }
        }

        var analyzers = runAnalyzers ? all : ImmutableArray<DiagnosticAnalyzer>.Empty;
        if (analyzers.IsEmpty) return new ComputedDiagnostics(compilerDiagnostics, suppressorsRan);

        try
        {
            var driver = WithAnalyzers(compilation, project, analyzers);
            var syntax = await driver.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken);
            var semantic = await driver.GetAnalyzerSemanticDiagnosticsAsync(
                driver.Compilation.GetSemanticModel(tree), filterSpan: null, cancellationToken);
            var analyzerDiagnostics = syntax.Concat(semantic).Where(d => !d.IsSuppressed);
            return new ComputedDiagnostics(compilerDiagnostics.Concat(analyzerDiagnostics).ToList(), analyzers);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Warning] Analyzer execution failed for '{project.Name}': {ex.Message}");
            return new ComputedDiagnostics(compilerDiagnostics, Array.Empty<DiagnosticAnalyzer>());
        }
    }

    // The code-fix providers that can fix a diagnostic in the project: the built-in MEF
    // providers, then the providers the project's analyzer assemblies export.
    private IReadOnlyList<CodeFixProvider> CodeFixProvidersFor(Project project) =>
        GetBuiltInCodeFixProviders()
            .Concat(project.AnalyzerReferences.SelectMany(r => AnalyzerReferenceFixProviders.GetValue(r, LoadFixProviders)))
            .ToList();

    // Fix providers per analyzer reference, loaded once per reference instance.
    private static readonly ConditionalWeakTable<AnalyzerReference, IReadOnlyList<CodeFixProvider>> AnalyzerReferenceFixProviders = new();

    // The non-abstract CodeFixProvider types of an analyzer assembly that carry
    // [ExportCodeFixProvider] for C#, each created through its parameterless constructor.
    // A reference that is not an assembly on disk, an assembly that cannot load, or a
    // provider that cannot be created contributes nothing.
    private static IReadOnlyList<CodeFixProvider> LoadFixProviders(AnalyzerReference reference)
    {
        if (reference is not AnalyzerFileReference file) return Array.Empty<CodeFixProvider>();

        IEnumerable<Type> types;
        try
        {
            types = file.GetAssembly().GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.OfType<Type>();
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException or ArgumentException)
        {
            Console.Error.WriteLine($"[Warning] Could not load code fixes from '{file.FullPath}': {ex.Message}");
            return Array.Empty<CodeFixProvider>();
        }

        var providers = new List<CodeFixProvider>();
        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type)) continue;
            var export = type.GetCustomAttribute<ExportCodeFixProviderAttribute>();
            if (export == null || !export.Languages.Contains(LanguageNames.CSharp)) continue;
            if (type.GetConstructor(Type.EmptyTypes) == null) continue;
            try
            {
                providers.Add((CodeFixProvider)Activator.CreateInstance(type)!);
            }
            catch (TargetInvocationException ex)
            {
                Console.Error.WriteLine($"[Warning] Could not create code fix provider '{type.FullName}': {ex.InnerException?.Message}");
            }
        }
        return providers;
    }
}
