using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for analysis tools: get_diagnostics, analyze_data_flow, analyze_control_flow, etc.
/// </summary>
//
// get_diagnostics filter/severity/includeHidden coverage lives in
// GetDiagnosticsFilterTests.cs — those tests use an AdhocWorkspace seeded with
// known compiler diagnostics + InfoFiresAnalyzer/HiddenFiresAnalyzer/AlwaysFiresAnalyzer
// so each filter branch can be content-locked against deterministic output. The
// previous solution-loaded variants here were tautologies on a clean codebase.
public class AnalysisTests : RoslynServiceTestBase
{
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
        memberSummary["fields"]?.Value<int>().Should().BeGreaterOrEqualTo(5,
            "RoslynService declares at least _workspace, _solution, _solutionLoadedAt, " +
            "_documentCache, _compilationCache");
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
        // Our solution has the deliberately-untested UntestedCodeFrameworkTests fixtures
        // (UntestedCodeFixture, UntestedFrameworkTarget) and a CompoundedTestabilityTarget
        // — at least one unused symbol must appear, or the foreach silently passes.
        unusedSymbols.Should().NotBeNullOrEmpty(
            "the solution intentionally contains fixtures with untested public surface");
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
        var code = "public class Test { public void Foo() { invalidSyntax!!!! } }";
        var result = await Service.ValidateCodeAsync(code, standalone: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["compiles"]?.Value<bool>().Should().BeFalse();
        var errors = data["errors"] as JArray;
        errors.Should().NotBeNullOrEmpty(
            "an obviously malformed expression must surface compiler errors, not an empty list");
        // Every emitted error must carry a real diagnostic ID (CS####) — empty IDs would
        // mean the response shape is broken even though compiles=false.
        foreach (var err in errors!)
        {
            err["id"]?.Value<string>().Should().StartWith("CS",
                "compiler errors are reported with CS-prefixed IDs");
        }
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

    private async Task<(string file, int line, int col)> LocateCreateSuccessResponseAsync()
    {
        var searchResult = await Service.SearchSymbolsAsync("CreateSuccessResponse", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();
        var loc = symbols![0]["location"]!;
        return (loc["filePath"]!.Value<string>()!,
                loc["line"]!.Value<int>(),
                loc["column"]!.Value<int>());
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AddParameter_ReportsBreakingChangesAndErrorSeverity()
    {
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "addParameter");

        AssertSuccess(result);
        var data = GetData(result);
        // Inspection.cs:1192-1197 marks every call site as error / breakingChanges++.
        data["breakingChanges"]?.Value<int>().Should().BeGreaterThan(0,
            "addParameter must report every call site as a breaking change");
        data["safe"]?.Value<bool>().Should().BeFalse(
            "addParameter cannot be safe — call sites lose a required argument");
        data["changeType"]?.Value<string>().Should().Be("addParameter");

        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        foreach (var l in locations!)
        {
            l["severity"]?.Value<string>().Should().Be("error");
            l["impact"]?.Value<string>().Should().Contain("missing new parameter");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_RemoveParameter_ReportsBreakingChangesAndErrorSeverity()
    {
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "removeParameter");

        AssertSuccess(result);
        var data = GetData(result);
        data["breakingChanges"]?.Value<int>().Should().BeGreaterThan(0);
        data["safe"]?.Value<bool>().Should().BeFalse();
        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        foreach (var l in locations!)
        {
            l["severity"]?.Value<string>().Should().Be("error");
            l["impact"]?.Value<string>().Should().Contain("extra parameter");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_ChangeType_ReportsWarningSeverityNotBreaking()
    {
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "changeType");

        AssertSuccess(result);
        var data = GetData(result);
        // Inspection.cs:1185-1190: changeType produces warnings, not breaking changes.
        data["breakingChanges"]?.Value<int>().Should().Be(0);
        data["warnings"]?.Value<int>().Should().BeGreaterThan(0,
            "changeType must increment the warnings counter once per call site");
        data["safe"]?.Value<bool>().Should().BeTrue("warnings don't count toward safe=false");

        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        foreach (var l in locations!)
        {
            l["severity"]?.Value<string>().Should().Be("warning");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_Delete_ReportsBreakingChanges()
    {
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "delete");

        AssertSuccess(result);
        var data = GetData(result);
        data["breakingChanges"]?.Value<int>().Should().BeGreaterThan(0);
        data["safe"]?.Value<bool>().Should().BeFalse();
        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        foreach (var l in locations!)
        {
            l["severity"]?.Value<string>().Should().Be("error");
            l["impact"]?.Value<string>().Should().Contain("Reference will be broken");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_ChangeAccessibility_OnPrivateSymbol_ReportsWarning()
    {
        // CreateSuccessResponse is private — Inspection.cs:1206-1219 takes the non-public
        // branch, emitting warning severity and incrementing warnings (not breakingChanges).
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "changeAccessibility");

        AssertSuccess(result);
        var data = GetData(result);
        data["safe"]?.Value<bool>().Should().BeTrue();
        data["breakingChanges"]?.Value<int>().Should().Be(0);
        data["warnings"]?.Value<int>().Should().BeGreaterThan(0);
        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        foreach (var l in locations!)
        {
            l["severity"]?.Value<string>().Should().Be("warning");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_UnknownChangeType_FallsThroughToInfoSeverity()
    {
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "made_up_change_type_xyz");

        AssertSuccess(result);
        var data = GetData(result);
        // Default branch (Inspection.cs:1228-1231): unknown change → info severity, no counters.
        data["breakingChanges"]?.Value<int>().Should().Be(0);
        data["warnings"]?.Value<int>().Should().Be(0);
        data["safe"]?.Value<bool>().Should().BeTrue();
        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        foreach (var l in locations!)
        {
            l["severity"]?.Value<string>().Should().Be("info");
            l["impact"]?.Value<string>().Should().Be("Unknown impact");
        }
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnSingleStatement_ExercisesEqualFirstLastBranch()
    {
        // When startLine == endLine and ResolveAnalysisStatements returns the same
        // node for both, the impl takes the AnalyzeDataFlow(singleStatement) branch
        // at Compound.cs:75-77. Lock that path against the Sum body's `var partial` line.
        var (file, firstStmt, _) = LocateSumBody(Service);

        var result = await Service.AnalyzeDataFlowAsync(file, startLine: firstStmt, endLine: firstStmt);
        AssertSuccess(result);

        var data = GetData(result);
        data["succeeded"]?.Value<bool>().Should().BeTrue(
            "the single-statement branch must still produce a successful analysis");

        // `var partial = a + b;` declares partial and reads a, b.
        var declared = (data["variablesDeclared"] as JArray)!.Select(v => v.Value<string>()).ToList();
        declared.Should().Contain("partial",
            "the single-statement region must report the declaration it contains");

        var read = (data["readInside"] as JArray)!.Select(v => v.Value<string>()).ToList();
        read.Should().Contain("a");
        read.Should().Contain("b",
            "single-statement analysis must surface BOTH operands of `a + b`");
    }

    [Fact]
    public async Task AnalyzeControlFlow_OnLoopWithBreakAndContinue_ReportsBothExitKinds()
    {
        // ControlFlowFixtureTarget.FilterAndSum has a for-loop containing both `continue`
        // and `break`. Their ExitPoints kinds must surface in the response.
        var searchResult = await Service.SearchSymbolsAsync("FilterAndSum", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty(
            "ControlFlowFixtureTarget.FilterAndSum must be declared in the fixtures project");

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        // file is solution-relative; resolve to absolute against the solution dir for the local read.
        var absoluteFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, file);
        var lines = File.ReadAllLines(absoluteFile);
        // Select the LOOP BODY (the three if/if/+= statements) — NOT the for construct
        // itself. Roslyn classifies break/continue as exit points only when they leave
        // the analyzed region; if the region IS the loop, break/continue stay inside it.
        var firstIfLine = Array.FindIndex(lines, l => l.Contains("if (values[i] < 0)"));
        var totalLine = Array.FindIndex(lines, l => l.Contains("total += values[i];"));
        firstIfLine.Should().BeGreaterThan(-1);
        totalLine.Should().BeGreaterThan(firstIfLine);

        var result = await Service.AnalyzeControlFlowAsync(file, startLine: firstIfLine, endLine: totalLine);
        AssertSuccess(result);
        var data = GetData(result);
        data["succeeded"]?.Value<bool>().Should().BeTrue();

        var exitPoints = data["exitPoints"] as JArray;
        exitPoints.Should().NotBeNullOrEmpty(
            "a region containing break and continue must report exit points");
        var kinds = exitPoints!.Select(e => e["kind"]?.Value<string>()).ToList();
        kinds.Should().Contain("BreakStatement",
            "the loop's break statement must surface as an exit point");
        kinds.Should().Contain("ContinueStatement",
            "the loop's continue statement must surface as an exit point");
    }

    [Fact]
    public async Task CheckTypeCompatibility_ListOfIntToIEnumerableOfInt_IsCompatible()
    {
        var result = await Service.CheckTypeCompatibilityAsync(
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.IEnumerable`1");

        AssertSuccess(result);
        var data = GetData(result);
        data["compatible"]?.Value<bool>().Should().BeTrue(
            "List<T> implements IEnumerable<T>; the generic compatibility must resolve");
    }

    [Fact]
    public async Task CheckTypeCompatibility_OpenGenericDictionaryToString_IsIncompatible()
    {
        // Negative companion to ListOfIntToIEnumerableOfInt: an open generic type
        // (Dictionary<,>) has no conversion to System.String. Locks the "incompatible"
        // branch on a generic source type — the existing Int32→String test covers
        // only non-generic source types.
        var result = await Service.CheckTypeCompatibilityAsync(
            "System.Collections.Generic.Dictionary`2",
            "System.String");

        AssertSuccess(result);
        var data = GetData(result);
        data["compatible"]?.Value<bool>().Should().BeFalse(
            "Dictionary<,> has no conversion to System.String");
    }
}
