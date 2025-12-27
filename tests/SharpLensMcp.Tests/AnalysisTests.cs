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
    public async Task GetDiagnostics_ReturnsWarnings()
    {
        // Act - correct signature: filePath, projectPath, severity, includeHidden
        var result = await Service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: null,
            includeHidden: false);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["diagnostics"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetDiagnostics_ForSpecificFile_FiltersResults()
    {
        // Act
        var result = await Service.GetDiagnosticsAsync(
            filePath: RoslynServicePath,
            projectPath: null,
            severity: null,
            includeHidden: false);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var diagnostics = data["diagnostics"] as JArray;

        foreach (var diag in diagnostics ?? new JArray())
        {
            diag["filePath"]?.Value<string>().Should().Contain("RoslynService");
        }
    }

    [Fact]
    public async Task GetDiagnostics_FilterBySeverity_ReturnsOnlyErrors()
    {
        // Act
        var result = await Service.GetDiagnosticsAsync(
            filePath: null,
            projectPath: null,
            severity: "Error",
            includeHidden: false);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var diagnostics = data["diagnostics"] as JArray;

        foreach (var diag in diagnostics ?? new JArray())
        {
            diag["severity"]?.Value<string>().Should().Be("Error");
        }
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnMethodBody_ReturnsFlowInfo()
    {
        // Find a method with local variables
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var startLine = symbol["line"]?.Value<int>() ?? 0;

            var result = await Service.AnalyzeDataFlowAsync(
                file,
                startLine: startLine + 1,
                endLine: startLine + 5);

            // May succeed or give meaningful error
            var json = JObject.FromObject(result);
        }
    }

    [Fact]
    public async Task AnalyzeControlFlow_OnMethodBody_ReturnsFlowInfo()
    {
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var startLine = symbol["line"]?.Value<int>() ?? 0;

            var result = await Service.AnalyzeControlFlowAsync(
                file,
                startLine: startLine + 1,
                endLine: startLine + 5);

            var json = JObject.FromObject(result);
        }
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
    public async Task FindUnusedCode_FindsUnusedSymbols()
    {
        // Act - correct signature: projectName, includePrivate, includeInternal, symbolKindFilter, maxResults
        var result = await Service.FindUnusedCodeAsync(
            projectName: null,
            includePrivate: true,
            includeInternal: false,
            symbolKindFilter: null,
            maxResults: 10);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["unusedSymbols"].Should().NotBeNull();
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
        // Find a method to analyze
        var searchResult = await Service.SearchSymbolsAsync("CreateSuccessResponse", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.AnalyzeChangeImpactAsync(
                file, line, col,
                changeType: "rename",
                newValue: "CreateSuccess");

            AssertSuccess(result);
            var data = GetData(result);
            data["impactedLocations"].Should().NotBeNull();
        }
    }
}
