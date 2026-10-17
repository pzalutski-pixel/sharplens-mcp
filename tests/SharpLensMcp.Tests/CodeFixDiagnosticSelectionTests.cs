using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using SharpLensMcp.Tests.TestAnalyzers;
using Xunit;

namespace SharpLensMcp.Tests;

// get_code_fixes / apply_code_fix select the diagnostic AT the requested position — never a
// same-ID diagnostic elsewhere in the file — see each diagnostic once (syntax diagnostics
// are part of the semantic model's set), include analyzer diagnostics with the fixers the
// analyzer's own assembly exports, and never offer a fix for a suppressed diagnostic.
public class CodeFixDiagnosticSelectionTests
{
    private const string Source = """
        namespace Lib
        {
            public class Diag
            {
                public void First()
                {
                    int unusedA;
                }

                public void Second()
                {
                    int unusedB;
                }

                public void Third()
                {
                    System.Console.WriteLine("no diagnostic here");
                }

                public void FixableThing() { }

                public void M()
                {
                    int suppressedVar;
                }
            }
        }
        """;

    private const string BrokenSource = """
        namespace Lib
        {
            public class Q
            {
                public int A() { return 1 }
            }
        }
        """;

    private static QualitySolutionBuilder Build(out RoslynService service, out InMemoryDispatcher dispatcher, out string file, out string broken)
    {
        var builder = new QualitySolutionBuilder();
        var fixPackage = EmittedFixPackage.Emit(Path.Combine(builder.Directory, "fixpackage"));
        builder.AddProject("Lib", new[] { ("Diag.cs", Source), ("Q.cs", BrokenSource) },
            analyzers: new AnalyzerReference[]
            {
                fixPackage,
                new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new UnusedVariableSuppressor()))
            });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        file = builder.FilePath("Lib", "Diag.cs");
        broken = builder.FilePath("Lib", "Q.cs");
        return builder;
    }

    [Fact]
    public async Task NoDiagnosticAtThePosition_IsNotFound_EvenWhenTheIdOccursElsewhereInTheFile()
    {
        using var builder = Build(out var service, out _, out var file, out _);
        var (line, column) = RetargetedSources.Locate(Source, "System.Console.WriteLine");

        var fixes = JObject.FromObject(await service.GetCodeFixesAsync(file, "CS0168", line, column));
        fixes["success"]!.Value<bool>().Should().BeFalse();
        fixes["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        fixes["error"]!["Message"]!.Value<string>().Should().Be($"No diagnostic with ID 'CS0168' found at line {line}, column {column}");

        var apply = JObject.FromObject(await service.ApplyCodeFixAsync(file, "CS0168", line, column, fixIndex: 0, preview: true));
        apply["success"]!.Value<bool>().Should().BeFalse();
        apply["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        apply["error"]!["Message"]!.Value<string>().Should().Be($"No diagnostic with ID 'CS0168' found at line {line}, column {column}");
    }

    [Fact]
    public async Task APositionInsideTheSecondDiagnostic_SelectsTheSecondDiagnostic()
    {
        using var builder = Build(out var service, out _, out var file, out _);
        var (line, column) = RetargetedSources.Locate(Source, "unusedB");

        var data = JObject.FromObject(await service.GetCodeFixesAsync(file, "CS0168", line, column + 3))["data"]!;

        data["location"]!["startLine"]!.Value<int>().Should().Be(line);
        data["location"]!["startColumn"]!.Value<int>().Should().Be(column);
        data["message"]!.Value<string>().Should().Contain("unusedB");
    }

    [Fact]
    public async Task SyntaxDiagnostics_AreSeenOnce()
    {
        using var builder = Build(out var service, out _, out _, out var broken);
        var (line, column) = RetargetedSources.Locate(BrokenSource, " }");

        var json = JObject.FromObject(await service.GetCodeFixesAsync(broken, "CS9999", line, column));

        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        ((JArray)json["error"]!["Context"]!["diagnosticsNearby"]!).Select(d => d["id"]!.Value<string>())
            .Should().Equal(new[] { "CS1002" }, "the missing ';' is one diagnostic, not one per diagnostic source");
    }

    [Fact]
    public async Task AnalyzerDiagnostics_GetTheFixersTheirAnalyzerAssemblyExports()
    {
        using var builder = Build(out var service, out _, out var file, out _);
        var (line, column) = RetargetedSources.Locate(Source, "FixableThing");

        var data = JObject.FromObject(await service.GetCodeFixesAsync(file, EmittedFixPackage.DiagnosticId, line, column))["data"]!;
        data["diagnosticId"]!.Value<string>().Should().Be(EmittedFixPackage.DiagnosticId);
        ((JArray)data["fixes"]!).Select(f => $"{f["index"]}:{f["title"]}").Should().Equal($"0:{EmittedFixPackage.FixTitle}");

        var preview = JObject.FromObject(await service.ApplyCodeFixAsync(file, EmittedFixPackage.DiagnosticId, line, column, fixIndex: 0, preview: true))["data"]!;
        var changed = ((JArray)preview["changedFiles"]!).Single();
        changed["newText"]!.Value<string>().Should().Contain("public void FixedThing() { }");
        changed["newText"]!.Value<string>().Should().NotContain("FixableThing");
    }

    [Fact]
    public async Task SuppressedDiagnostics_AreNotOfferedForFixing()
    {
        using var builder = Build(out var service, out _, out var file, out _);
        var (line, column) = RetargetedSources.Locate(Source, "suppressedVar");

        var json = JObject.FromObject(await service.GetCodeFixesAsync(file, "CS0168", line, column));

        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task ViaDispatcher_PositionedSelection_AndAnalyzerFixers()
    {
        using var builder = Build(out _, out var dispatcher, out var file, out _);
        var (thirdLine, thirdColumn) = RetargetedSources.Locate(Source, "System.Console.WriteLine");
        var (fixLine, fixColumn) = RetargetedSources.Locate(Source, "FixableThing");

        var error = await dispatcher.ErrorAsync("apply_code_fix",
            new { filePath = file, diagnosticId = "CS0168", line = thirdLine, column = thirdColumn, fixIndex = 0 }, ErrorCodes.SymbolNotFound);
        error["message"]!.Value<string>().Should().Be($"No diagnostic with ID 'CS0168' found at line {thirdLine}, column {thirdColumn}");

        var fixes = await dispatcher.DataAsync("get_code_fixes",
            new { filePath = file, diagnosticId = EmittedFixPackage.DiagnosticId, line = fixLine, column = fixColumn });
        ((JArray)fixes["fixes"]!).Select(f => $"{f["index"]}:{f["title"]}").Should().Equal($"0:{EmittedFixPackage.FixTitle}");
    }
}
