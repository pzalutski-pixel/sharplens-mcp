using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Analysis category (11 tools).
public class AnalysisToolsViaMcpTests : McpTestBase
{
    public AnalysisToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetDiagnostics_FullSolution_AnalyzerFieldsPresent()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = true
        });
        data["analyzersRan"].Should().NotBeNull("analyzer fields are part of the 1.5.3 contract");
        data["analyzerCount"].Should().NotBeNull();
        data["diagnostics"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetDiagnostics_SeverityErrorFilter_OnlyErrorsOrEmpty()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = "Error",
            includeHidden = false,
            runAnalyzers = false
        });
        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull();
        foreach (var d in diagnostics!)
        {
            d["severity"]?.Value<string>().Should().Be("Error");
        }
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnFixtureSumBody_ReturnsDeclaredVariables()
    {
        // Resolve fixture method body line range dynamically — Sum lives in
        // RefactoringFixture.cs which is part of the loaded solution.
        var (file, methodLine, _) = await LocateSymbolAsync(
            "Sum", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);

        var lines = File.ReadAllLines(file);
        var firstStmt = Array.FindIndex(lines, l => l.Contains("var partial"));
        var lastStmt = Array.FindIndex(lines, l => l.Contains("return total"));
        firstStmt.Should().BeGreaterThan(-1);
        lastStmt.Should().BeGreaterThan(firstStmt);

        var data = await CallAndGetDataAsync("roslyn:analyze_data_flow", new
        {
            filePath = file,
            startLine = firstStmt,
            endLine = lastStmt
        });
        data["succeeded"]?.Value<bool>().Should().BeTrue();
        var declared = (data["variablesDeclared"] as JArray)!.Select(v => v.Value<string>()).ToList();
        declared.Should().Contain("partial");
        declared.Should().Contain("total");
    }

    [Fact]
    public async Task AnalyzeControlFlow_OnFixtureSumBody_ReportsExitPoint()
    {
        var (file, methodLine, _) = await LocateSymbolAsync(
            "Sum", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);

        var lines = File.ReadAllLines(file);
        var firstStmt = Array.FindIndex(lines, l => l.Contains("var partial"));
        var lastStmt = Array.FindIndex(lines, l => l.Contains("return total"));

        var data = await CallAndGetDataAsync("roslyn:analyze_control_flow", new
        {
            filePath = file,
            startLine = firstStmt,
            endLine = lastStmt
        });
        data["endPointIsReachable"]?.Value<bool>().Should().BeFalse(
            "region ends with `return` so the end-point is unreachable");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_OnCreateSuccessResponse_ListsImpacted()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "CreateSuccessResponse", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:analyze_change_impact", new
        {
            filePath = file, line, column = col,
            changeType = "rename",
            newValue = "CreateSuccess"
        });
        var impacted = data["impactedLocations"] as JArray;
        impacted.Should().NotBeNullOrEmpty(
            "CreateSuccessResponse is called from many tool methods, so impactedLocations must be non-empty");
    }

    [Fact]
    public async Task CheckTypeCompatibility_StringToObject_IsCompatible()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.String",
            targetType = "System.Object"
        });
        data["compatible"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_IntToString_NotCompatible()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Int32",
            targetType = "System.String"
        });
        data["compatible"]?.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetOutgoingCalls_OnGetHealthCheck_ReturnsCallsArray()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "GetHealthCheckAsync", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_outgoing_calls", new
        {
            filePath = file,
            line = line + 1,
            column = col
        });
        data["calls"].Should().NotBeNull();
    }

    [Fact]
    public async Task FindUnusedCode_DefaultArgs_ReturnsUnusedSymbolsArray()
    {
        var data = await CallAndGetDataAsync("roslyn:find_unused_code", new
        {
            projectName = (string?)null,
            includePrivate = true,
            includeInternal = false,
            symbolKindFilter = (string?)null,
            maxResults = 10
        });
        var unused = data["unusedSymbols"] as JArray;
        unused.Should().NotBeNull("unusedSymbols array must always be present");
        unused!.Count.Should().BeLessOrEqualTo(10, "maxResults must cap returnedCount");
    }

    [Fact]
    public async Task ValidateCode_ValidStandaloneCode_Compiles()
    {
        var data = await CallAndGetDataAsync("roslyn:validate_code", new
        {
            code = "public class Test { public void Foo() { } }",
            standalone = true
        });
        data["compiles"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ValidateCode_BrokenCode_DoesNotCompile()
    {
        var data = await CallAndGetDataAsync("roslyn:validate_code", new
        {
            code = "public class Test { public void Foo() { !!! invalid syntax } }",
            standalone = true
        });
        data["compiles"]?.Value<bool>().Should().BeFalse();
        data["errors"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetComplexityMetrics_OnFile_ReturnsPerMethodMetrics()
    {
        var data = await CallAndGetDataAsync("roslyn:get_complexity_metrics", new
        {
            filePath = Fixture.RoslynServicePath
        });
        data["scope"]?.Value<string>().Should().Be("file");
        var methods = data["methods"] as JArray;
        methods.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FindCircularDependencies_DefaultLevel_ReturnsCyclesField()
    {
        var data = await CallAndGetDataAsync("roslyn:find_circular_dependencies", new
        {
            level = (string?)null
        });
        data["hasCycles"]?.Type.Should().Be(JTokenType.Boolean);
        data["cycles"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetMissingMembers_OnRoslynService_ReturnsEmptyOrAbsentList()
    {
        // RoslynService is a complete type — no missing members expected.
        var data = await CallAndGetDataAsync("roslyn:get_missing_members", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 50,
            column = 10
        });
        var missing = data["missingMembers"] as JArray;
        (missing == null || missing.Count == 0).Should().BeTrue();
    }
}
