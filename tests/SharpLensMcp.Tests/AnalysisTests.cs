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
        data["succeeded"].Should().NotBeNull();
        data["succeeded"]!.Value<bool>().Should().BeTrue();

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
        data["startPointIsReachable"].Should().NotBeNull();
        data["startPointIsReachable"]!.Value<bool>().Should().BeTrue();
        data["endPointIsReachable"].Should().NotBeNull();
        data["endPointIsReachable"]!.Value<bool>().Should().BeFalse(
            "the region ends with a return so the end-point isn't reachable");
    }

    [Fact]
    public async Task GetFileOverview_OnRoslynService_IncludesRoslynServiceTypeDeclaration()
    {
        var result = await Service.GetFileOverviewAsync(RoslynServicePath);

        AssertSuccess(result);
        var data = GetData(result);
        data["filePath"].Should().NotBeNull();
        data["filePath"]!.Value<string>().Should().Contain("RoslynService");
        // Lock lineCount to the file's actual line count — exact and drift-proof —
        // rather than a `> 400` inequality against the live, growing source file.
        data["lineCount"].Should().NotBeNull();
        data["lineCount"]!.Value<int>().Should().Be(
            File.ReadAllText(RoslynServicePath).Count(c => c == '\n') + 1,
            "lineCount must equal the file's line count (newlines + 1, Roslyn's convention)");

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
        data["typeName"].Should().NotBeNull();
        data["typeName"]!.Value<string>().Should().Contain("RoslynService");
        data["typeKind"].Should().NotBeNull();
        data["typeKind"]!.Value<string>().Should().Be("Class");
        var memberSummary = data["memberSummary"]!;
        memberSummary["methods"].Should().NotBeNull();
        memberSummary["methods"]!.Value<int>().Should().BeGreaterOrEqualTo(20,
            "RoslynService has dozens of methods across partials");
        memberSummary["fields"].Should().NotBeNull();
        memberSummary["fields"]!.Value<int>().Should().BeGreaterOrEqualTo(5,
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
        signature["name"].Should().NotBeNull();
        signature["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        signature["returnType"].Should().NotBeNull();
        signature["returnType"]!.Value<string>().Should().Contain("Task<object>");
        signature["isAsync"].Should().NotBeNull();
        signature["isAsync"]!.Value<bool>().Should().BeTrue();

        var parameters = signature["parameters"] as JArray;
        parameters!.Count.Should().Be(2, "solutionPath plus the cancellationToken every tool method carries");
        parameters[0]["name"].Should().NotBeNull();
        parameters[0]["name"]!.Value<string>().Should().Be("solutionPath");
        parameters[1]["name"].Should().NotBeNull();
        parameters[1]["name"]!.Value<string>().Should().Be("cancellationToken");
    }

    [Fact]
    public async Task AnalyzeMethod_OverloadedName_WithoutIndex_ReturnsAmbiguousMatch()
    {
        // NameAddressingTarget declares Describe() and Describe(int) — analyzing by
        // name alone must not silently pick one; it surfaces the ambiguity.
        var result = await Service.AnalyzeMethodAsync("NameAddressingTarget", "Describe");

        AssertError(result, ErrorCodes.AmbiguousMatch);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("2 overloads",
            "the ambiguity error must report how many overloads exist");
    }

    [Fact]
    public async Task AnalyzeMethod_OverloadIndex_SelectsThatSpecificOverload()
    {
        var zero = await Service.AnalyzeMethodAsync("NameAddressingTarget", "Describe", overloadIndex: 0);
        AssertSuccess(zero);
        var one = await Service.AnalyzeMethodAsync("NameAddressingTarget", "Describe", overloadIndex: 1);
        AssertSuccess(one);

        var zeroParams = (GetData(zero)["signature"]!["parameters"] as JArray)!.Count;
        var oneParams = (GetData(one)["signature"]!["parameters"] as JArray)!.Count;
        new[] { zeroParams, oneParams }.Should().BeEquivalentTo(new[] { 0, 1 },
            "the two indices select the no-arg and the single-parameter Describe overload");
    }

    [Fact]
    public async Task AnalyzeMethod_OverloadIndexOutOfRange_ReturnsInvalidParameter()
    {
        var result = await Service.AnalyzeMethodAsync("NameAddressingTarget", "Describe", overloadIndex: 5);

        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task GetMethodSource_OnHealthCheck_ReturnsFullSourceWithSignature()
    {
        var result = await Service.GetMethodSourceAsync("RoslynService", "GetHealthCheckAsync");

        AssertSuccess(result);
        var data = GetData(result);
        // The response field is `fullSource`, not `source`.
        // The prior assertion read `data["source"]` which silently passed via null-conditional.
        var source = data["fullSource"]!.Value<string>()!;
        source.Should().Contain("public async Task<object> GetHealthCheckAsync(",
            "fullSource must include the declaration");
        source.Should().Contain("bool includeDiagnostics = false",
            "the includeDiagnostics parameter gates the diagnostics sweep");
        source.Should().Contain("CreateSuccessResponse",
            "the body uses CreateSuccessResponse for the health_check envelope");
        // lineCount is the method declaration's own line span — consistent with the
        // reported span and free of the leading-trivia lines fullSource carries.
        var startLine = data["span"]!["startLine"]!.Value<int>();
        var endLine = data["span"]!["endLine"]!.Value<int>();
        data["location"]!["line"]!.Value<int>().Should().Be(startLine,
            "the name of GetHealthCheckAsync is on the declaration's first line");
        endLine.Should().BeGreaterThan(startLine, "GetHealthCheckAsync spans multiple lines");
        data["lineCount"].Should().NotBeNull();
        data["lineCount"]!.Value<int>().Should().Be(
            endLine - startLine + 1,
            "lineCount must equal the declaration's line span (endLine - startLine + 1)");
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
            entry["name"].Should().NotBeNull();
            entry["name"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["fullyQualifiedName"].Should().NotBeNull();
            entry["fullyQualifiedName"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["kind"].Should().NotBeNull();
            entry["kind"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["accessibility"].Should().NotBeNull();
            entry["accessibility"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["filePath"].Should().NotBeNull();
            entry["filePath"]!.Value<string>().Should().EndWith(".cs");
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
        data["compiles"].Should().NotBeNull();
        data["compiles"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task ValidateCode_WithInvalidCode_ReturnsErrors()
    {
        var code = "public class Test { public void Foo() { invalidSyntax!!!! } }";
        var result = await Service.ValidateCodeAsync(code, standalone: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["compiles"].Should().NotBeNull();
        data["compiles"]!.Value<bool>().Should().BeFalse();
        var errors = data["errors"] as JArray;
        errors.Should().NotBeNullOrEmpty(
            "an obviously malformed expression must surface compiler errors, not an empty list");
        // Every emitted error must carry a real diagnostic ID (CS####) — empty IDs would
        // mean the response shape is broken even though compiles=false.
        foreach (var err in errors!)
        {
            err["id"].Should().NotBeNull();
            err["id"]!.Value<string>().Should().StartWith("CS",
                "compiler errors are reported with CS-prefixed IDs");
        }
    }

    [Fact]
    public async Task ValidateCode_NoContextWrap_ErrorLinesMapToTheSnippet()
    {
        // The error sits on the SECOND line of the snippet (0-based line 1).
        // The reported line must be relative to the snippet, not to the
        // ValidationClass wrapper the tool builds around it.
        var code = "var ok = 1;\nint bad = \"text\";";
        var result = await Service.ValidateCodeAsync(code);

        AssertSuccess(result);
        var data = GetData(result);
        data["compiles"]!.Value<bool>().Should().BeFalse();
        var errors = (data["errors"] as JArray)!;
        var cs0029 = errors.Single(e => e["id"]!.Value<string>() == "CS0029");
        cs0029["line"]!.Value<int>().Should().Be(1,
            "the string-to-int assignment is the snippet's second line");
    }

    [Fact]
    public async Task ValidateCode_WithContextFile_ErrorLinesMapToTheSnippet()
    {
        // Context mode wraps the snippet under the context file's usings and
        // namespace — several scaffold lines whose count depends on the file.
        // A one-line snippet's error must still report line 0.
        var result = await Service.ValidateCodeAsync(
            "var x = undefinedSymbol123;",
            contextFilePath: RoslynServicePath);

        AssertSuccess(result);
        var data = GetData(result);
        data["compiles"]!.Value<bool>().Should().BeFalse();
        var errors = (data["errors"] as JArray)!;
        var cs0103 = errors.Single(e => e["id"]!.Value<string>() == "CS0103");
        cs0103["line"]!.Value<int>().Should().Be(0,
            "the snippet is a single line, so its error is on snippet line 0");
    }

    [Fact]
    public async Task ValidateCode_Standalone_LinesAreReportedUnshifted()
    {
        // Standalone code IS the validated file — no wrapper, no offset.
        var code = "public class T\n{\n    public void M()\n    {\n        int bad = \"text\";\n    }\n}";
        var result = await Service.ValidateCodeAsync(code, standalone: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["compiles"]!.Value<bool>().Should().BeFalse();
        var errors = (data["errors"] as JArray)!;
        var cs0029 = errors.Single(e => e["id"]!.Value<string>() == "CS0029");
        cs0029["line"]!.Value<int>().Should().Be(4,
            "the bad assignment sits on line 4 of the standalone file");
    }

    [Fact]
    public async Task ValidateCode_SnippetErrorSurfacingInWrapperSuffix_ClampsToSnippetRange()
    {
        // An unclosed brace errors at the wrapper's closing scaffold, BELOW the
        // snippet. The reported line must clamp into the snippet's own range
        // instead of pointing past its end.
        var result = await Service.ValidateCodeAsync("if (true) {");

        AssertSuccess(result);
        var data = GetData(result);
        data["compiles"]!.Value<bool>().Should().BeFalse();
        var errors = (data["errors"] as JArray)!;
        errors.Should().NotBeEmpty("an unclosed brace cannot compile");
        foreach (var error in errors)
        {
            error["line"]!.Value<int>().Should().Be(0,
                "every reported line must stay within the one-line snippet");
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
        data["compatible"].Should().NotBeNull();
        data["compatible"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_WithIncompatibleTypes_ReturnsFalse()
    {
        // Act - int is not compatible with string directly (need fully qualified names)
        var result = await Service.CheckTypeCompatibilityAsync("System.Int32", "System.String");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["compatible"].Should().NotBeNull();
        data["compatible"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetInstantiationOptions_OnRoslynService_HasParameterlessConstructor()
    {
        var result = await Service.GetInstantiationOptionsAsync("RoslynService");

        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"].Should().NotBeNull();
        data["typeName"]!.Value<string>().Should().Contain("RoslynService");
        data["typeKind"].Should().NotBeNull();
        data["typeKind"]!.Value<string>().Should().Be("Class");
        data["implementsIDisposable"].Should().NotBeNull();
        data["implementsIDisposable"]!.Value<bool>().Should().BeFalse();
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
        // Rename is always non-breaking.
        data["safe"].Should().NotBeNull();
        data["safe"]!.Value<bool>().Should().BeTrue();
        data["breakingChanges"].Should().NotBeNull();
        data["breakingChanges"]!.Value<int>().Should().Be(0);

        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        foreach (var l in locations!)
        {
            l["severity"].Should().NotBeNull();
            l["severity"]!.Value<string>().Should().Be("info");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AddParameterWithDefault_IsNonBreaking()
    {
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        // A new parameter WITH a default value keeps existing call sites valid.
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "addParameter", newValue: "int flags = 0");

        AssertSuccess(result);
        var data = GetData(result);
        data["breakingChanges"]!.Value<int>().Should().Be(0,
            "adding a parameter with a default value is source-compatible");
        data["safe"]!.Value<bool>().Should().BeTrue();
        // The response is honest about being a categorized inventory, not a proof.
        data["note"]!.Value<string>().Should().Contain("feasibility");
        foreach (var l in (data["impactedLocations"] as JArray)!)
        {
            l["severity"]!.Value<string>().Should().Be("info");
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
        data["safe"].Should().NotBeNull();
        data["safe"]!.Value<bool>().Should().BeFalse(
            "addParameter cannot be safe — call sites lose a required argument");
        data["changeType"].Should().NotBeNull();
        data["changeType"]!.Value<string>().Should().Be("addParameter");

        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        // Every call site is a breaking change, so breakingChanges equals the number of
        // impacted locations — an exact, drift-proof consistency lock rather than `> 0`.
        data["breakingChanges"].Should().NotBeNull();
        data["breakingChanges"]!.Value<int>().Should().Be(locations!.Count,
            "addParameter marks every impacted call site as a breaking change");
        foreach (var l in locations)
        {
            l["severity"].Should().NotBeNull();
            l["severity"]!.Value<string>().Should().Be("error");
            l["impact"].Should().NotBeNull();
            l["impact"]!.Value<string>().Should().Contain("missing new parameter");
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
        data["safe"].Should().NotBeNull();
        data["safe"]!.Value<bool>().Should().BeFalse();
        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        data["breakingChanges"].Should().NotBeNull();
        data["breakingChanges"]!.Value<int>().Should().Be(locations!.Count,
            "removeParameter marks every impacted call site as a breaking change");
        foreach (var l in locations)
        {
            l["severity"].Should().NotBeNull();
            l["severity"]!.Value<string>().Should().Be("error");
            l["impact"].Should().NotBeNull();
            l["impact"]!.Value<string>().Should().Contain("extra parameter");
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
        // changeType produces warnings, not breaking changes.
        data["breakingChanges"].Should().NotBeNull();
        data["breakingChanges"]!.Value<int>().Should().Be(0);
        data["safe"].Should().NotBeNull();
        data["safe"]!.Value<bool>().Should().BeTrue("warnings don't count toward safe=false");

        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        // changeType warns once per call site, so warnings equals the number of impacted
        // locations — an exact, drift-proof consistency lock rather than `> 0`.
        data["warnings"].Should().NotBeNull();
        data["warnings"]!.Value<int>().Should().Be(locations!.Count,
            "changeType increments the warnings counter once per impacted call site");
        foreach (var l in locations)
        {
            l["severity"].Should().NotBeNull();
            l["severity"]!.Value<string>().Should().Be("warning");
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
        data["safe"].Should().NotBeNull();
        data["safe"]!.Value<bool>().Should().BeFalse();
        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        data["breakingChanges"].Should().NotBeNull();
        data["breakingChanges"]!.Value<int>().Should().Be(locations!.Count,
            "delete marks every impacted reference as a breaking change");
        foreach (var l in locations)
        {
            l["severity"].Should().NotBeNull();
            l["severity"]!.Value<string>().Should().Be("error");
            l["impact"].Should().NotBeNull();
            l["impact"]!.Value<string>().Should().Contain("Reference will be broken");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_ChangeAccessibility_OnPrivateSymbol_ReportsWarning()
    {
        // CreateSuccessResponse is private — the impl takes the non-public
        // branch, emitting warning severity and incrementing warnings (not breakingChanges).
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "changeAccessibility");

        AssertSuccess(result);
        var data = GetData(result);
        data["safe"].Should().NotBeNull();
        data["safe"]!.Value<bool>().Should().BeTrue();
        data["breakingChanges"].Should().NotBeNull();
        data["breakingChanges"]!.Value<int>().Should().Be(0);
        var locations = data["impactedLocations"] as JArray;
        locations.Should().NotBeNullOrEmpty();
        data["warnings"].Should().NotBeNull();
        data["warnings"]!.Value<int>().Should().Be(locations!.Count,
            "changeAccessibility on a private symbol warns once per impacted reference");
        foreach (var l in locations)
        {
            l["severity"].Should().NotBeNull();
            l["severity"]!.Value<string>().Should().Be("warning");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_UnknownChangeType_IsInvalidParameter()
    {
        // An unknown change type is a caller error, never a "safe to proceed" verdict.
        var (file, line, col) = await LocateCreateSuccessResponseAsync();
        var result = await Service.AnalyzeChangeImpactAsync(
            file, line, col, changeType: "made_up_change_type_xyz");

        AssertError(result, ErrorCodes.InvalidParameter);
        var error = JObject.FromObject(result)["error"]!;
        error["Context"]!["parameter"]!.Value<string>().Should().Be("changeType");
        error["Context"]!["allowedValues"]!.Values<string>().Should().Equal(
            "rename", "changeType", "addParameter", "removeParameter", "changeAccessibility", "delete");
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnSingleStatement_ExercisesEqualFirstLastBranch()
    {
        // When startLine == endLine and ResolveAnalysisStatements returns the same
        // node for both, the impl takes the AnalyzeDataFlow(singleStatement) branch
        // Lock that path against the Sum body's `var partial` line.
        var (file, firstStmt, _) = LocateSumBody(Service);

        var result = await Service.AnalyzeDataFlowAsync(file, startLine: firstStmt, endLine: firstStmt);
        AssertSuccess(result);

        var data = GetData(result);
        data["succeeded"].Should().NotBeNull();
        data["succeeded"]!.Value<bool>().Should().BeTrue(
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
        data["succeeded"].Should().NotBeNull();
        data["succeeded"]!.Value<bool>().Should().BeTrue();

        var exitPoints = data["exitPoints"] as JArray;
        exitPoints.Should().NotBeNullOrEmpty(
            "a region containing break and continue must report exit points");
        var kinds = exitPoints!.Select(e => e["kind"]?.Value<string>()).ToList();
        kinds.Should().Contain("BreakStatement",
            "the loop's break statement must surface as an exit point");
        kinds.Should().Contain("ContinueStatement",
            "the loop's continue statement must surface as an exit point");
    }

    private static (string file, string[] lines) LocateControlFlowFixture(RoslynService service)
    {
        var solution = service.GetSolutionForTesting()!;
        var fixtureDoc = solution.Projects
            .SelectMany(p => p.Documents)
            .First(d => d.FilePath != null && d.FilePath.EndsWith("ControlFlowFixture.cs"));
        return (fixtureDoc.FilePath!, File.ReadAllLines(fixtureDoc.FilePath!));
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnSubRangeOfBlock_AnalyzesOnlySelectedStatements()
    {
        // Select only the first two statements of Sum, leaving `return total;`
        // OUTSIDE the region. Resolving the selection through the enclosing block's
        // first/last statements would silently widen the analysis to the whole body
        // and report nothing flowing out.
        var (file, _, _) = LocateSumBody(Service);
        var lines = File.ReadAllLines(file);
        var first = Array.FindIndex(lines, l => l.Contains("var partial"));
        var second = Array.FindIndex(lines, l => l.Contains("var total"));
        first.Should().BeGreaterThan(-1);
        second.Should().BeGreaterThan(first);

        var result = await Service.AnalyzeDataFlowAsync(file, startLine: first, endLine: second);
        AssertSuccess(result);
        var data = GetData(result);
        data["succeeded"]!.Value<bool>().Should().BeTrue();

        var declared = (data["variablesDeclared"] as JArray)!.Select(v => v.Value<string>()).ToList();
        declared.Should().BeEquivalentTo(new[] { "partial", "total" });

        var flowsOut = (data["dataFlowsOut"] as JArray)!.Select(v => v.Value<string>()).ToList();
        flowsOut.Should().Contain("total",
            "`total` is read by the return statement below the selection — it only flows " +
            "out when the analysis stops at the selected lines instead of the whole block");
        flowsOut.Should().NotContain("partial",
            "`partial` is last read inside the selection, so it must not flow out");
    }

    [Fact]
    public async Task AnalyzeControlFlow_OnSubRangeOfBlock_EndPointIsReachable()
    {
        // Same sub-range as the data-flow clamp test: the region excludes the
        // method's return, so execution falls through the end of the region and no
        // return statement belongs to it. Whole-block analysis reported the opposite.
        var (file, _, _) = LocateSumBody(Service);
        var lines = File.ReadAllLines(file);
        var first = Array.FindIndex(lines, l => l.Contains("var partial"));
        var second = Array.FindIndex(lines, l => l.Contains("var total"));

        var result = await Service.AnalyzeControlFlowAsync(file, startLine: first, endLine: second);
        AssertSuccess(result);
        var data = GetData(result);
        data["succeeded"]!.Value<bool>().Should().BeTrue();
        data["endPointIsReachable"]!.Value<bool>().Should().BeTrue(
            "the selected region ends before the return, so its end point is reachable");
        (data["returnStatements"] as JArray)!.Should().BeEmpty(
            "the return statement lies outside the selected region");
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnMiddleLineOfMultiLineStatement_AnalyzesThatStatementOnly()
    {
        // CombineAcrossLines declares `combined` with an initializer spanning three
        // lines. Selecting only the middle line resolves to a node INSIDE the
        // declaration statement; the analysis must clamp to that one statement
        // instead of climbing to the whole method body.
        var (file, lines) = LocateControlFlowFixture(Service);
        var middle = Array.FindIndex(lines, l => l.Contains("x * 2 +"));
        middle.Should().BeGreaterThan(-1);

        var result = await Service.AnalyzeDataFlowAsync(file, startLine: middle, endLine: middle);
        AssertSuccess(result);
        var data = GetData(result);
        data["succeeded"]!.Value<bool>().Should().BeTrue();

        var declared = (data["variablesDeclared"] as JArray)!.Select(v => v.Value<string>()).ToList();
        declared.Should().BeEquivalentTo(new[] { "combined" },
            "the selected line belongs to the `combined` declaration and nothing else");

        var flowsOut = (data["dataFlowsOut"] as JArray)!.Select(v => v.Value<string>()).ToList();
        flowsOut.Should().Contain("combined",
            "`combined` is read by the return below the analyzed statement; whole-block " +
            "analysis would have included the return and reported nothing flowing out");
    }

    [Fact]
    public async Task AnalyzeControlFlow_OnMiddleLineOfMultiLineStatement_ExcludesTheReturnBelow()
    {
        var (file, lines) = LocateControlFlowFixture(Service);
        var middle = Array.FindIndex(lines, l => l.Contains("x * 2 +"));
        middle.Should().BeGreaterThan(-1);

        var result = await Service.AnalyzeControlFlowAsync(file, startLine: middle, endLine: middle);
        AssertSuccess(result);
        var data = GetData(result);
        data["succeeded"]!.Value<bool>().Should().BeTrue();
        data["endPointIsReachable"]!.Value<bool>().Should().BeTrue(
            "the analyzed declaration statement falls through to the return below it");
        (data["returnStatements"] as JArray)!.Should().BeEmpty(
            "`return combined;` lies outside the analyzed statement");
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnSubRangeOfSwitchSection_ClampsToSelectedStatements()
    {
        // DescribeCode's case 1 section holds three statements; its statement list
        // lives on the SwitchSectionSyntax, not on a block. Selecting the two
        // declarations must analyze exactly those, so both locals flow out to the
        // section's return below the selection.
        var (file, lines) = LocateControlFlowFixture(Service);
        var first = Array.FindIndex(lines, l => l.Contains("var label"));
        var second = Array.FindIndex(lines, l => l.Contains("var suffix"));
        first.Should().BeGreaterThan(-1);
        second.Should().BeGreaterThan(first);

        var result = await Service.AnalyzeDataFlowAsync(file, startLine: first, endLine: second);
        AssertSuccess(result);
        var data = GetData(result);
        data["succeeded"]!.Value<bool>().Should().BeTrue();

        var declared = (data["variablesDeclared"] as JArray)!.Select(v => v.Value<string>()).ToList();
        declared.Should().BeEquivalentTo(new[] { "label", "suffix" });

        var flowsOut = (data["dataFlowsOut"] as JArray)!.Select(v => v.Value<string>()).ToList();
        flowsOut.Should().Contain("label",
            "`label` is read by the section's return below the selection");
        flowsOut.Should().Contain("suffix",
            "`suffix` is read by the section's return below the selection");
    }

    [Fact]
    public async Task CheckTypeCompatibility_ListOfIntToIEnumerableOfInt_IsCompatible()
    {
        var result = await Service.CheckTypeCompatibilityAsync(
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.IEnumerable`1");

        AssertSuccess(result);
        var data = GetData(result);
        data["compatible"].Should().NotBeNull();
        data["compatible"]!.Value<bool>().Should().BeTrue(
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
        data["compatible"].Should().NotBeNull();
        data["compatible"]!.Value<bool>().Should().BeFalse(
            "Dictionary<,> has no conversion to System.String");
    }
}
