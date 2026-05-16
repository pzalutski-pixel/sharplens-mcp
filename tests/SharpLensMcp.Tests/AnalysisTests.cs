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
    public async Task GetDiagnostics_ReturnsArray()
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
    public async Task GetFileOverview_ReturnsComprehensiveInfo()
    {
        // Act
        var result = await Service.GetFileOverviewAsync(RoslynServicePath);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["filePath"]?.Value<string>().Should().Contain("RoslynService");
        data["lineCount"]?.Value<int>().Should().BeGreaterThan(1000);
        data["typeDeclarations"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetTypeOverview_ReturnsComprehensiveInfo()
    {
        // Act
        var result = await Service.GetTypeOverviewAsync("RoslynService");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        data["memberSummary"].Should().NotBeNull();
    }

    [Fact]
    public async Task AnalyzeMethod_ReturnsSignatureAndCallers()
    {
        // Act
        var result = await Service.AnalyzeMethodAsync(
            "RoslynService",
            "LoadSolutionAsync",
            includeCallers: true);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["methodName"]?.Value<string>().Should().Be("LoadSolutionAsync");
        data["signature"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetMethodSource_ReturnsSourceCode()
    {
        // Act
        var result = await Service.GetMethodSourceAsync("RoslynService", "GetHealthCheckAsync");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["source"]?.Value<string>().Should().Contain("HealthCheck");
        data["lineCount"]?.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FindUnusedCode_ReturnsArrayWithExpectedShape()
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
    public async Task GetInstantiationOptions_ReturnsConstructorInfo()
    {
        // Act
        var result = await Service.GetInstantiationOptionsAsync("RoslynService");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["constructors"].Should().NotBeNull();
    }

    [Fact]
    public async Task AnalyzeChangeImpact_ShowsAffectedLocations()
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
        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty(
            "CreateSuccessResponse is called from many sites, so impactedLocations must be non-empty");
    }
}
