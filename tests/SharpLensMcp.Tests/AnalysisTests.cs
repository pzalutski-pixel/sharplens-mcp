using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for analysis tools: get_diagnostics, analyze_data_flow, analyze_control_flow, etc.
/// </summary>
public class AnalysisTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetDiagnostics_ReturnsArrayWithConsistentCounts()
    {
        var result = await Service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: null,
            includeHidden: false);

        AssertSuccess(result);
        var data = GetData(result);
        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull("a clean build still produces a diagnostics array, even if empty");
        // The reported errorCount/warningCount must match the array contents (cap aside).
        var errors = diagnostics!.Count(d => d["severity"]?.Value<string>() == "Error");
        var warnings = diagnostics!.Count(d => d["severity"]?.Value<string>() == "Warning");
        data["errorCount"]?.Value<int>().Should().Be(errors);
        data["warningCount"]?.Value<int>().Should().Be(warnings);
    }

    [Fact]
    public async Task GetDiagnostics_WithRunAnalyzersTrue_HasConsistentAnalyzerFields()
    {
        var result = await Service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: null,
            includeHidden: false,
            runAnalyzers: true);

        AssertSuccess(result);
        var data = GetData(result);
        // The contract: analyzersRan and analyzerCount must agree.
        var ran = data["analyzersRan"]!.Value<bool>();
        var count = data["analyzerCount"]!.Value<int>();
        if (ran)
        {
            count.Should().BeGreaterThan(0, "analyzersRan=true requires at least one analyzer");
        }
        else
        {
            count.Should().Be(0, "analyzersRan=false requires zero analyzer count");
        }
    }

    [Fact]
    public async Task GetDiagnostics_WithRunAnalyzersFalse_FlagsAnalyzersDidNotRun()
    {
        var result = await Service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: null,
            includeHidden: false,
            runAnalyzers: false);

        AssertSuccess(result);
        var data = GetData(result);
        data["analyzersRan"]?.Value<bool>().Should().BeFalse(
            "runAnalyzers:false must skip analyzer execution");
        data["analyzerCount"]?.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetDiagnostics_ForSpecificFile_OnlyContainsThatFile()
    {
        var result = await Service.GetDiagnosticsAsync(
            filePath: RoslynServicePath,
            projectPath: null,
            severity: null,
            includeHidden: false);

        AssertSuccess(result);
        var data = GetData(result);
        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull();

        // Every diagnostic (if any) must reference RoslynService.cs — the filter
        // is the whole point of this test. Empty result is also acceptable for a
        // warning-free file, but a non-RoslynService entry would mean the filter is broken.
        foreach (var diag in diagnostics!)
        {
            diag["filePath"]?.Value<string>().Should().EndWith("RoslynService.cs");
        }
    }

    [Fact]
    public async Task GetDiagnostics_FilterBySeverity_OnlyErrorsOrEmpty()
    {
        var result = await Service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: "Error",
            includeHidden: false);

        AssertSuccess(result);
        var data = GetData(result);
        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull();

        foreach (var diag in diagnostics!)
        {
            diag["severity"]?.Value<string>().Should().Be("Error",
                "severity filter must include only Error-level diagnostics");
        }
    }

    private static (string file, int firstStmt, int lastStmt) LocateSumBody(RoslynService service)
    {
        // Resolve the fixture path off the loaded solution, then read the file to find
        // the inclusive line range of Sum's three body statements. This avoids hard-coding
        // line offsets that drift whenever the fixture file changes.
        var solution = service.GetSolutionForTesting()!;
        var fixtureDoc = solution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.FilePath != null && d.FilePath.EndsWith("RefactoringFixture.cs"));
        var file = fixtureDoc.FilePath!;
        var lines = File.ReadAllLines(file);

        var first = Array.FindIndex(lines, l => l.Contains("var partial"));
        var last = Array.FindIndex(lines, l => l.Contains("return total"));
        first.Should().BeGreaterThan(-1);
        last.Should().BeGreaterThan(first);
        return (file, first, last);
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnFixtureSum_ReturnsExpectedVariables()
    {
        var (file, firstStmt, lastStmt) = LocateSumBody(Service);

        var result = await Service.AnalyzeDataFlowAsync(file, startLine: firstStmt, endLine: lastStmt);
        AssertSuccess(result);

        var data = GetData(result);
        data["succeeded"]?.Value<bool>().Should().BeTrue();

        var declared = (data["variablesDeclared"] as JArray)!.Select(v => v.Value<string>()).ToList();
        declared.Should().Contain("partial");
        declared.Should().Contain("total");

        var read = (data["readInside"] as JArray)!.Select(v => v.Value<string>()).ToList();
        read.Should().Contain("a");
        read.Should().Contain("c");
    }

    [Fact]
    public async Task AnalyzeControlFlow_OnFixtureSum_ReturnsExitPoint()
    {
        var (file, firstStmt, lastStmt) = LocateSumBody(Service);

        var result = await Service.AnalyzeControlFlowAsync(file, startLine: firstStmt, endLine: lastStmt);
        AssertSuccess(result);

        var data = GetData(result);
        data["startPointIsReachable"]?.Value<bool>().Should().BeTrue();
        data["endPointIsReachable"]?.Value<bool>().Should().BeFalse(
            "the region ends with a return so the end-point isn't reachable");
    }

    [Fact]
    public async Task GetFileOverview_OnRoslynService_IncludesRoslynServiceTypeDeclaration()
    {
        var result = await Service.GetFileOverviewAsync(RoslynServicePath);

        AssertSuccess(result);
        var data = GetData(result);
        data["filePath"]?.Value<string>().Should().Contain("RoslynService");
        data["lineCount"]?.Value<int>().Should().BeGreaterThan(400,
            "RoslynService.cs is hundreds of lines after the partial-class split");

        var typeDecls = data["typeDeclarations"] as JArray;
        typeDecls.Should().NotBeNullOrEmpty();
        typeDecls!.Any(t => t["name"]?.Value<string>() == "RoslynService")
            .Should().BeTrue("file declares the RoslynService partial class");
    }

    [Fact]
    public async Task GetTypeOverview_OnRoslynService_HasExpectedMemberCounts()
    {
        var result = await Service.GetTypeOverviewAsync("RoslynService");

        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        var memberSummary = data["memberSummary"]!;
        memberSummary["methods"]?.Value<int>().Should().BeGreaterOrEqualTo(20,
            "RoslynService has dozens of methods across partials");
        memberSummary["fields"]?.Value<int>().Should().BeGreaterOrEqualTo(1,
            "RoslynService has at least _workspace, _solution, etc. fields");
    }

    [Fact]
    public async Task AnalyzeMethod_OnLoadSolutionAsync_SignatureHasExpectedShape()
    {
        var result = await Service.AnalyzeMethodAsync(
            "RoslynService",
            "LoadSolutionAsync",
            includeCallers: true);

        AssertSuccess(result);
        var data = GetData(result);
        var signature = data["signature"]!;
        signature["name"]?.Value<string>().Should().Be("LoadSolutionAsync");
        signature["returnType"]?.Value<string>().Should().Contain("Task<object>");
        signature["isAsync"]?.Value<bool>().Should().BeTrue();

        var parameters = signature["parameters"] as JArray;
        parameters!.Count.Should().Be(1);
        parameters[0]["name"]?.Value<string>().Should().Be("solutionPath");
    }

    [Fact]
    public async Task GetMethodSource_OnHealthCheck_ReturnsFullSourceWithSignature()
    {
        var result = await Service.GetMethodSourceAsync("RoslynService", "GetHealthCheckAsync");

        AssertSuccess(result);
        var data = GetData(result);
        // The response field is `fullSource` (RoslynService.Inspection.cs:1399), not `source`.
        // The prior assertion read `data["source"]` which silently passed via null-conditional.
        var source = data["fullSource"]!.Value<string>()!;
        source.Should().Contain("public async Task<object> GetHealthCheckAsync()");
        source.Should().Contain("CreateSuccessResponse",
            "the body uses CreateSuccessResponse after the 1.5.3 health_check shape fix");
        data["lineCount"]?.Value<int>().Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task FindUnusedCode_DefaultArgs_EveryEntryHasFullShape()
    {
        var result = await Service.FindUnusedCodeAsync(
            projectName: null,
            includePrivate: true,
            includeInternal: false,
            symbolKindFilter: null,
            maxResults: 10);

        AssertSuccess(result);
        var data = GetData(result);
        var unusedSymbols = data["unusedSymbols"] as JArray;
        unusedSymbols.Should().NotBeNull("response must always include an unusedSymbols array");
        unusedSymbols!.Count.Should().BeLessOrEqualTo(10, "maxResults must be enforced");
        // Every returned entry must conform to the documented UnusedSymbolEntry shape.
        foreach (var entry in unusedSymbols)
        {
            entry["name"]?.Value<string>().Should().NotBeNullOrEmpty();
            entry["fullyQualifiedName"]?.Value<string>().Should().NotBeNullOrEmpty();
            entry["kind"]?.Value<string>().Should().NotBeNullOrEmpty();
            entry["accessibility"]?.Value<string>().Should().NotBeNullOrEmpty();
            entry["filePath"]?.Value<string>().Should().EndWith(".cs");
        }
    }

    [Fact]
    public async Task ValidateCode_WithValidCode_ReturnsSuccess()
    {
        // Act
        var code = "public class Test { public void Foo() { } }";
        var result = await Service.ValidateCodeAsync(code, standalone: true);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["compiles"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ValidateCode_WithInvalidCode_ReturnsErrors()
    {
        // Act
        var code = "public class Test { public void Foo() { invalidSyntax!!!! } }";
        var result = await Service.ValidateCodeAsync(code, standalone: true);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["compiles"]?.Value<bool>().Should().BeFalse();
        data["errors"].Should().NotBeNull();
    }

    [Fact]
    public async Task CheckTypeCompatibility_WithCompatibleTypes_ReturnsTrue()
    {
        // Act - string is compatible with object (need fully qualified names)
        var result = await Service.CheckTypeCompatibilityAsync("System.String", "System.Object");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["compatible"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_WithIncompatibleTypes_ReturnsFalse()
    {
        // Act - int is not compatible with string directly (need fully qualified names)
        var result = await Service.CheckTypeCompatibilityAsync("System.Int32", "System.String");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["compatible"]?.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetInstantiationOptions_OnRoslynService_HasParameterlessConstructor()
    {
        var result = await Service.GetInstantiationOptionsAsync("RoslynService");

        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        data["implementsIDisposable"]?.Value<bool>().Should().BeFalse();
        var ctors = data["constructors"] as JArray;
        ctors.Should().NotBeNullOrEmpty();
        ctors!.Any(c => (c["parameters"] as JArray)?.Count == 0)
            .Should().BeTrue("RoslynService has a parameterless public constructor");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_RenameCreateSuccessResponse_AllInfoNonBreaking()
    {
        var searchResult = await Service.SearchSymbolsAsync("CreateSuccessResponse", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.AnalyzeChangeImpactAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            changeType: "rename",
            newValue: "CreateSuccess");

        AssertSuccess(result);
        var data = GetData(result);
        // Rename is always non-breaking per Inspection.cs:1180-1183.
        data["safe"]?.Value<bool>().Should().BeTrue();
        data["breakingChanges"]?.Value<int>().Should().Be(0);

        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        foreach (var l in locations!)
        {
            l["severity"]?.Value<string>().Should().Be("info");
        }
    }
}
