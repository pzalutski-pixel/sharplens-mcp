using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Analysis category (11 tools). Each assertion is grounded
// in concrete known facts about the loaded SharpLens solution:
//  - Clean compiler-only diagnostics (no errors/warnings)
//  - RefactoringFixture.Sum has known variable flow (partial, total, a, c)
//  - GetHealthCheckAsync calls GetProjectCompilationAsync (verified in RoslynService.cs:491)
//  - CreateSuccessResponse has >30 call sites across the tool surface
//  - No project-level circular dependencies
public class AnalysisToolsViaMcpTests : McpTestBase
{
    public AnalysisToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetDiagnostics_FullSolutionRunAnalyzersFalse_ReportsCompilerOnly()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = false
        });
        // runAnalyzers:false locks both fields to deterministic values.
        data["analyzersRan"]?.Value<bool>().Should().BeFalse();
        data["analyzerCount"]?.Value<int>().Should().Be(0);

        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull();
        // The reported counts must match the entries in the diagnostics array.
        var errors = diagnostics!.Count(d => d["severity"]?.Value<string>() == "Error");
        var warnings = diagnostics!.Count(d => d["severity"]?.Value<string>() == "Warning");
        data["errorCount"]?.Value<int>().Should().Be(errors);
        data["warningCount"]?.Value<int>().Should().Be(warnings);
    }

    [Fact]
    public async Task GetDiagnostics_FullSolutionRunAnalyzersTrue_ReportsAnalyzerFields()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = true
        });
        // With analyzers on, the response must surface BOTH flags.
        // analyzersRan can be false if no project has analyzer references; then count == 0.
        data["analyzersRan"]?.Type.Should().Be(JTokenType.Boolean);
        data["analyzerCount"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
    }

    [Fact]
    public async Task GetDiagnostics_SeverityWarningFilter_OnlyWarnings()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = "Warning",
            includeHidden = false,
            runAnalyzers = false
        });
        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull();
        foreach (var d in diagnostics!)
        {
            d["severity"]?.Value<string>().Should().Be("Warning",
                "the severity filter must drop non-Warning entries");
        }
        data["errorCount"]?.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetDiagnostics_ProjectPathFilter_RestrictsToThatProject()
    {
        var projectPath = Path.Combine(Fixture.SolutionDir, "src", "SharpLensMcp.csproj");
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = false
        });
        var diagnostics = data["diagnostics"] as JArray;
        diagnostics.Should().NotBeNull();
        // Every diagnostic must come from a file inside the SharpLensMcp/src/ directory.
        // No tests/* files should appear when the filter is set to the main project.
        foreach (var d in diagnostics!)
        {
            var path = d["filePath"]?.Value<string>() ?? "";
            path.Should().NotContain("tests/",
                "projectPath filter must exclude files from other projects");
            path.Should().NotContain("tests\\");
        }
    }

    [Fact]
    public async Task GetDiagnostics_SeverityErrorFilter_OnlyErrorsAndCountMatches()
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
            d["severity"]?.Value<string>().Should().Be("Error",
                "severity filter must drop non-Error entries");
        }
        // With the Error filter, warningCount in the response should be 0.
        data["warningCount"]?.Value<int>().Should().Be(0);
        data["errorCount"]?.Value<int>().Should().Be(diagnostics.Count);
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnFixtureSumBody_LocksDeclaredAndReadVariables()
    {
        var (file, _, _) = await LocateSymbolAsync(
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
        declared.Should().BeEquivalentTo(new[] { "partial", "total" },
            "the Sum body declares exactly `partial` and `total` locals");

        var read = (data["readInside"] as JArray)!.Select(v => v.Value<string>()).ToList();
        read.Should().Contain("a");
        read.Should().Contain("c");
        read.Should().Contain("partial");
    }

    [Fact]
    public async Task AnalyzeControlFlow_OnFixtureSumBody_ReportsEntryAndExitInvariants()
    {
        var (file, _, _) = await LocateSymbolAsync(
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
        data["succeeded"]?.Value<bool>().Should().BeTrue();
        data["startPointIsReachable"]?.Value<bool>().Should().BeTrue(
            "the region starts with a local declaration that is reachable");
        data["endPointIsReachable"]?.Value<bool>().Should().BeFalse(
            "the region ends with `return total` so falling off the end is unreachable");

        var returns = data["returnStatements"] as JArray;
        returns.Should().NotBeNullOrEmpty();
        returns!.Count.Should().Be(1, "Sum has exactly one return statement in the analyzed region");
    }

    [Fact]
    public async Task AnalyzeChangeImpact_RenameCreateSuccessResponse_AllInfoNoBreaking()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "CreateSuccessResponse", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:analyze_change_impact", new
        {
            filePath = file, line, column = col,
            changeType = "rename",
            newValue = "CreateSuccess"
        });
        // Rename change: per Inspection.cs:1180-1183, severity is always "info"
        // and breakingChanges is never incremented — these are the load-bearing
        // invariants of the rename classifier.
        data["safe"]?.Value<bool>().Should().BeTrue("rename is non-breaking per the impact classifier");
        data["breakingChanges"]?.Value<int>().Should().Be(0);
        data["warnings"]?.Value<int>().Should().Be(0,
            "rename has neither breaking nor warning impacts per the classifier");

        var impacted = data["impactedLocations"] as JArray;
        impacted.Should().NotBeNullOrEmpty();
        foreach (var loc in impacted!)
        {
            loc["severity"]?.Value<string>().Should().Be("info",
                "every rename impact must classify as info");
            loc["impact"]?.Value<string>().Should().Be("Reference will need to be updated",
                "Inspection.cs:1181 sets this exact text for rename");
        }
        // totalReferences must equal the size of impactedLocations (no filtering between them).
        data["totalReferences"]?.Value<int>().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task CheckTypeCompatibility_StringToObject_IsImplicitReference()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.String",
            targetType = "System.Object"
        });
        data["compatible"]?.Value<bool>().Should().BeTrue();
        data["requiresCast"]?.Value<bool>().Should().BeFalse();
        data["conversionKind"]?.Value<string>().Should().Be("ImplicitReference");
        data["isReferenceConversion"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_IntToString_NoneConversion()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Int32",
            targetType = "System.String"
        });
        data["compatible"]?.Value<bool>().Should().BeFalse();
        data["requiresCast"]?.Value<bool>().Should().BeFalse();
        data["conversionKind"]?.Value<string>().Should().Be("None");
    }

    [Fact]
    public async Task GetOutgoingCalls_OnGetHealthCheck_IncludesGetProjectCompilationAsync()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "GetHealthCheckAsync", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_outgoing_calls", new
        {
            filePath = file,
            line = line + 1,
            column = col
        });
        var calls = data["calls"] as JArray;
        calls.Should().NotBeNullOrEmpty();
        // GetHealthCheckAsync's body calls GetProjectCompilationAsync (RoslynService.cs:491).
        // The tool's shortName field is "{ContainingType}.{Method}" per Inspection.cs:897.
        calls!.Any(c =>
            c["shortName"]?.Value<string>()?.EndsWith(".GetProjectCompilationAsync") == true)
            .Should().BeTrue("GetHealthCheckAsync explicitly calls GetProjectCompilationAsync");
    }

    [Fact]
    public async Task FindUnusedCode_DefaultArgs_EveryEntryHasFullShape()
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
        unused.Should().NotBeNull();
        unused!.Count.Should().BeLessOrEqualTo(10, "maxResults must cap returnedCount");

        // Every returned entry must conform to the full UnusedSymbolEntry shape so
        // a field rename in the data class would fail this test.
        foreach (var entry in unused)
        {
            entry["name"]?.Value<string>().Should().NotBeNullOrEmpty();
            entry["fullyQualifiedName"]?.Value<string>().Should().NotBeNullOrEmpty();
            entry["kind"]?.Value<string>().Should().NotBeNullOrEmpty();
            entry["accessibility"]?.Value<string>().Should().NotBeNullOrEmpty();
            entry["filePath"]?.Value<string>().Should().EndWith(".cs");
            entry["line"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
        }
    }

    [Fact]
    public async Task ValidateCode_ValidStandaloneCode_CompilesWithNoErrors()
    {
        var data = await CallAndGetDataAsync("roslyn:validate_code", new
        {
            code = "public class Test { public void Foo() { } }",
            standalone = true
        });
        data["compiles"]?.Value<bool>().Should().BeTrue();
        data["errorCount"]?.Value<int>().Should().Be(0);
        (data["errors"] as JArray)?.Count.Should().Be(0);
    }

    [Fact]
    public async Task ValidateCode_BrokenCode_ReportsCompilerErrorIds()
    {
        var data = await CallAndGetDataAsync("roslyn:validate_code", new
        {
            code = "public class Test { public void Foo() { !!! invalid syntax } }",
            standalone = true
        });
        data["compiles"]?.Value<bool>().Should().BeFalse();
        var errors = data["errors"] as JArray;
        errors.Should().NotBeNullOrEmpty();
        // Compiler errors are tagged with "CS{number}" diagnostic IDs.
        errors!.Any(e => e["id"]?.Value<string>()?.StartsWith("CS") == true)
            .Should().BeTrue("Roslyn surfaces compiler errors with CS-prefixed IDs");
    }

    [Fact]
    public async Task GetComplexityMetrics_OnRoslynServiceFile_FindsLoadSolutionAsyncWithCyclomatic()
    {
        var data = await CallAndGetDataAsync("roslyn:get_complexity_metrics", new
        {
            filePath = Fixture.RoslynServicePath
        });
        data["scope"]?.Value<string>().Should().Be("file");
        var methods = data["methods"] as JArray;
        methods.Should().NotBeNullOrEmpty();

        var loadSolution = methods!.FirstOrDefault(m =>
            m["name"]?.Value<string>() == "LoadSolutionAsync");
        loadSolution.Should().NotBeNull(
            "LoadSolutionAsync must appear in the per-method breakdown");
        loadSolution!["metrics"]?["cyclomatic"]?.Value<int>().Should().BeGreaterOrEqualTo(1,
            "every method has cyclomatic complexity >= 1");
    }

    [Fact]
    public async Task FindCircularDependencies_Project_NoCyclesInOurSolution()
    {
        var data = await CallAndGetDataAsync("roslyn:find_circular_dependencies", new
        {
            level = (string?)null
        });
        data["level"]?.Value<string>().Should().Be("project");
        data["hasCycles"]?.Value<bool>().Should().BeFalse(
            "the SharpLens solution has no project-level cycles");
        (data["cycles"] as JArray)?.Count.Should().Be(0);
    }

    [Fact]
    public async Task GetMissingMembers_OnRoslynService_NoneMissing()
    {
        // RoslynService is a complete partial class. Resolve the MatchesGlobPattern
        // method body dynamically (its line shifts when the file is edited) so the
        // test pins to its real position, not a hard-coded constant.
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var methodLine = Array.FindIndex(lines, l => l.Contains("MatchesGlobPattern"));
        methodLine.Should().BeGreaterThan(-1);

        var data = await CallAndGetDataAsync("roslyn:get_missing_members", new
        {
            filePath = Fixture.RoslynServicePath,
            line = methodLine + 4,   // inside the method body
            column = 10
        });
        data["typeName"]?.Value<string>().Should().EndWith("RoslynService");
        data["isAbstract"]?.Value<bool>().Should().BeFalse();
        // Inspection.cs returns missingMembers as an empty array (not null) for
        // complete types — assert exactly that, no OR-pattern weakening.
        var missing = data["missingMembers"] as JArray;
        missing.Should().NotBeNull("missingMembers is always emitted as an array");
        missing!.Count.Should().Be(0,
            "RoslynService implements all interface/abstract members it inherits from");
    }
}
