using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for the Analysis category. Every assertion locks a specific
// field from the impl's documented response shape:
//  - get_diagnostics:           Analysis.cs:49-57
//  - analyze_data_flow:         Compound.cs:97-114
//  - analyze_control_flow:      Compound.cs:232-242
//  - analyze_change_impact:     Inspection.cs:1244-1256
//  - check_type_compatibility:  Validation.cs:247-260
//  - get_outgoing_calls:        Inspection.cs:942-948
//  - find_unused_code:          Inspection.cs:405-421
//  - validate_code:             Validation.cs:126-134
//  - get_complexity_metrics:    CodeGeneration.cs:169-177
//  - find_circular_dependencies:Discovery.cs:256-258
//  - get_missing_members:       Inspection.cs:789-796
//
// Tightening rule for this file: every accessor uses `!.Value<T>()` (NRE on
// missing) rather than `?.Value<T>()` (silent-pass via short-circuit).
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
        data["analyzersRan"]!.Value<bool>().Should().BeFalse();
        data["analyzerCount"]!.Value<int>().Should().Be(0);

        var diagnostics = (data["diagnostics"] as JArray)!;
        // Lock that every entry has a severity field — without this, the count
        // comparison below would silently drop entries with missing severity and
        // still match against the reported count for the wrong reason.
        foreach (var d in diagnostics)
        {
            d["severity"]!.Value<string>().Should().NotBeNullOrEmpty(
                "every diagnostic entry must carry a severity field");
        }
        var errors = diagnostics.Count(d => d["severity"]!.Value<string>() == "Error");
        var warnings = diagnostics.Count(d => d["severity"]!.Value<string>() == "Warning");
        data["errorCount"]!.Value<int>().Should().Be(errors);
        data["warningCount"]!.Value<int>().Should().Be(warnings);
    }

    [Fact]
    public async Task GetDiagnostics_FullSolutionRunAnalyzersTrue_ActuallyRunsAnalyzers()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = true
        });
        // The SharpLens solution loads SharpLensMcp.Tests.TestAnalyzers, so the
        // contract is analyzersRan=true AND analyzerCount > 0. The previous test
        // just checked the field type was Boolean — a smoke pattern that let any
        // value pass.
        data["analyzersRan"]!.Value<bool>().Should().BeTrue(
            "runAnalyzers=true must actually invoke analyzers in this solution");
        data["analyzerCount"]!.Value<int>().Should().BeGreaterThan(0,
            "the SharpLens solution has at least one analyzer reference (TestAnalyzers)");
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
        var diagnostics = (data["diagnostics"] as JArray)!;
        foreach (var d in diagnostics)
        {
            d["severity"]!.Value<string>().Should().Be("Warning",
                "the severity filter must drop non-Warning entries");
        }
        data["errorCount"]!.Value<int>().Should().Be(0);
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
        var diagnostics = (data["diagnostics"] as JArray)!;
        foreach (var d in diagnostics)
        {
            var path = d["filePath"]!.Value<string>()!;
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
        var diagnostics = (data["diagnostics"] as JArray)!;
        foreach (var d in diagnostics)
        {
            d["severity"]!.Value<string>().Should().Be("Error",
                "severity filter must drop non-Error entries");
        }
        data["warningCount"]!.Value<int>().Should().Be(0);
        data["errorCount"]!.Value<int>().Should().Be(diagnostics.Count);
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
        data["succeeded"]!.Value<bool>().Should().BeTrue();

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
        data["succeeded"]!.Value<bool>().Should().BeTrue();
        data["startPointIsReachable"]!.Value<bool>().Should().BeTrue(
            "the region starts with a local declaration that is reachable");
        data["endPointIsReachable"]!.Value<bool>().Should().BeFalse(
            "the region ends with `return total` so falling off the end is unreachable");

        var returns = (data["returnStatements"] as JArray)!;
        returns.Count.Should().Be(1, "Sum has exactly one return statement in the analyzed region");
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
        data["safe"]!.Value<bool>().Should().BeTrue("rename is non-breaking per the impact classifier");
        data["breakingChanges"]!.Value<int>().Should().Be(0);
        data["warnings"]!.Value<int>().Should().Be(0,
            "rename has neither breaking nor warning impacts per the classifier");

        var impacted = (data["impactedLocations"] as JArray)!;
        impacted.Should().NotBeEmpty();
        foreach (var loc in impacted)
        {
            loc["severity"]!.Value<string>().Should().Be("info",
                "every rename impact must classify as info");
            loc["impact"]!.Value<string>().Should().Be("Reference will need to be updated",
                "Inspection.cs:1181 sets this exact text for rename");
        }
        data["totalReferences"]!.Value<int>().Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public async Task AnalyzeChangeImpact_ChangeType_ProducesWarningsNotBreaking()
    {
        var (file, line, col) = await LocateSymbolAsync("CreateSuccessResponse", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RoslynService") == true);
        var data = await CallAndGetDataAsync("roslyn:analyze_change_impact", new
        {
            filePath = file, line, column = col,
            changeType = "changetype",
            newValue = "Task<object>"
        });
        data["breakingChanges"]!.Value<int>().Should().Be(0);
        data["warnings"]!.Value<int>().Should().BeGreaterOrEqualTo(1);
        data["safe"]!.Value<bool>().Should().BeTrue("changetype produces warnings, not breaking changes");
        foreach (var loc in (data["impactedLocations"] as JArray)!)
        {
            loc["severity"]!.Value<string>().Should().Be("warning");
            loc["impact"]!.Value<string>().Should().Be("Usage may be incompatible with new type");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_AddParameter_ProducesBreakingChanges()
    {
        var (file, line, col) = await LocateSymbolAsync("CreateSuccessResponse", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RoslynService") == true);
        var data = await CallAndGetDataAsync("roslyn:analyze_change_impact", new
        {
            filePath = file, line, column = col,
            changeType = "addparameter",
            newValue = "int extra"
        });
        data["safe"]!.Value<bool>().Should().BeFalse();
        data["breakingChanges"]!.Value<int>().Should().BeGreaterOrEqualTo(1);
        foreach (var loc in (data["impactedLocations"] as JArray)!)
        {
            loc["severity"]!.Value<string>().Should().Be("error");
            loc["impact"]!.Value<string>().Should().Be("Call site missing new parameter");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_RemoveParameter_ProducesBreakingChanges()
    {
        var (file, line, col) = await LocateSymbolAsync("CreateSuccessResponse", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RoslynService") == true);
        var data = await CallAndGetDataAsync("roslyn:analyze_change_impact", new
        {
            filePath = file, line, column = col,
            changeType = "removeparameter",
            newValue = "data"
        });
        data["safe"]!.Value<bool>().Should().BeFalse();
        data["breakingChanges"]!.Value<int>().Should().BeGreaterOrEqualTo(1);
        foreach (var loc in (data["impactedLocations"] as JArray)!)
        {
            loc["impact"]!.Value<string>().Should().Be("Call site has extra parameter");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_Delete_ProducesBreakingChanges()
    {
        var (file, line, col) = await LocateSymbolAsync("CreateSuccessResponse", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RoslynService") == true);
        var data = await CallAndGetDataAsync("roslyn:analyze_change_impact", new
        {
            filePath = file, line, column = col,
            changeType = "delete"
        });
        data["safe"]!.Value<bool>().Should().BeFalse();
        foreach (var loc in (data["impactedLocations"] as JArray)!)
        {
            loc["severity"]!.Value<string>().Should().Be("error");
            loc["impact"]!.Value<string>().Should().Be("Reference will be broken");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_ChangeAccessibility_OnPrivateField_ProducesWarnings()
    {
        // Non-public symbol → impact="Accessibility change may affect visibility",
        // severity="warning", warnings++ (Inspection.cs:1214-1219). The _workspace
        // field is private with references confined to RoslynService partials.
        var (file, line, col) = await LocateSymbolAsync("_workspace", kind: "Field");
        var data = await CallAndGetDataAsync("roslyn:analyze_change_impact", new
        {
            filePath = file, line, column = col,
            changeType = "changeaccessibility",
            newValue = "public"
        });
        data["safe"]!.Value<bool>().Should().BeTrue("non-public accessibility change produces warnings only");
        foreach (var loc in (data["impactedLocations"] as JArray)!)
        {
            loc["severity"]!.Value<string>().Should().Be("warning");
            loc["impact"]!.Value<string>().Should().Be("Accessibility change may affect visibility");
        }
    }

    [Fact]
    public async Task AnalyzeChangeImpact_UnknownChangeType_DefaultsToInfo()
    {
        var (file, line, col) = await LocateSymbolAsync("CreateSuccessResponse", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RoslynService") == true);
        var data = await CallAndGetDataAsync("roslyn:analyze_change_impact", new
        {
            filePath = file, line, column = col,
            changeType = "somethingUnknown",
            newValue = "n/a"
        });
        data["safe"]!.Value<bool>().Should().BeTrue();
        foreach (var loc in (data["impactedLocations"] as JArray)!)
        {
            loc["severity"]!.Value<string>().Should().Be("info");
            loc["impact"]!.Value<string>().Should().Be("Unknown impact");
        }
    }

    [Fact]
    public async Task GetCallGraph_MaxNodesReached_FlagsTruncatedByNodes()
    {
        // Set maxNodes very low to force truncation. LoadSolutionAsync is a
        // method with many callees; with maxNodes=2 the BFS halts past the
        // first level. Locks truncatedByNodes (Inspection.cs:1614).
        var (file, line, col) = await LocateSymbolAsync("LoadSolutionAsync", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_call_graph", new
        {
            filePath = file, line, column = col,
            direction = "callees",
            maxDepth = 5,
            maxNodes = 2
        });
        (data["nodes"] as JArray)!.Count.Should().BeLessOrEqualTo(2);
        data["truncatedByNodes"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_StringToObject_IsImplicitReference()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.String",
            targetType = "System.Object"
        });
        data["compatible"]!.Value<bool>().Should().BeTrue();
        data["requiresCast"]!.Value<bool>().Should().BeFalse();
        data["conversionKind"]!.Value<string>().Should().Be("ImplicitReference");
        data["isReferenceConversion"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_IntToString_NoneConversion()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Int32",
            targetType = "System.String"
        });
        data["compatible"]!.Value<bool>().Should().BeFalse();
        data["requiresCast"]!.Value<bool>().Should().BeFalse();
        data["conversionKind"]!.Value<string>().Should().Be("None");
    }

    [Fact]
    public async Task CheckTypeCompatibility_IntToInt_IsIdentity()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Int32",
            targetType = "System.Int32"
        });
        data["compatible"]!.Value<bool>().Should().BeTrue();
        data["requiresCast"]!.Value<bool>().Should().BeFalse();
        data["conversionKind"]!.Value<string>().Should().Be("Identity");
    }

    [Fact]
    public async Task CheckTypeCompatibility_IntToLong_IsImplicitNumeric()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Int32",
            targetType = "System.Int64"
        });
        data["compatible"]!.Value<bool>().Should().BeTrue();
        data["requiresCast"]!.Value<bool>().Should().BeFalse();
        data["conversionKind"]!.Value<string>().Should().Be("ImplicitNumeric");
        data["isNumericConversion"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_IntToObject_IsBoxing()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Int32",
            targetType = "System.Object"
        });
        data["compatible"]!.Value<bool>().Should().BeTrue();
        data["conversionKind"]!.Value<string>().Should().Be("Boxing");
        data["isBoxing"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_ObjectToInt_IsUnboxing()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Object",
            targetType = "System.Int32"
        });
        data["compatible"]!.Value<bool>().Should().BeTrue();
        data["requiresCast"]!.Value<bool>().Should().BeTrue();
        data["conversionKind"]!.Value<string>().Should().Be("Unboxing");
        data["isUnboxing"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_LongToInt_IsExplicitNumeric()
    {
        var data = await CallAndGetDataAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Int64",
            targetType = "System.Int32"
        });
        data["compatible"]!.Value<bool>().Should().BeTrue();
        data["requiresCast"]!.Value<bool>().Should().BeTrue();
        data["conversionKind"]!.Value<string>().Should().Be("ExplicitNumeric");
        data["isNumericConversion"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task CheckTypeCompatibility_UnknownSourceType_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Does.Not.Exist",
            targetType = "System.Object"
        }, codeContains: ErrorCodes.TypeNotFound);
        error["message"]!.Value<string>()!.Should().Contain("Source type");
        error["message"]!.Value<string>()!.Should().Contain("System.Does.Not.Exist");
    }

    [Fact]
    public async Task CheckTypeCompatibility_UnknownTargetType_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync("roslyn:check_type_compatibility", new
        {
            sourceType = "System.Object",
            targetType = "System.Does.Not.Exist"
        }, codeContains: ErrorCodes.TypeNotFound);
        error["message"]!.Value<string>()!.Should().Contain("Target type");
        error["message"]!.Value<string>()!.Should().Contain("System.Does.Not.Exist");
    }

    [Fact]
    public async Task ValidateCode_WithNonExistentContextFile_ReturnsFileNotInSolution()
    {
        var error = await CallAndGetErrorAsync("roslyn:validate_code", new
        {
            code = "var x = 1;",
            contextFilePath = "/path/does/not/exist/Nope.cs",
            standalone = false
        }, codeContains: ErrorCodes.FileNotInSolution);
        error["message"]!.Value<string>()!.Should().Contain("Nope.cs");
    }

    [Fact]
    public async Task ValidateCode_WithValidContextFile_WrapsInContextNamespaceAndCompiles()
    {
        var data = await CallAndGetDataAsync("roslyn:validate_code", new
        {
            code = "var x = 1;",
            contextFilePath = Fixture.RoslynServicePath,
            standalone = false
        });
        data["compiles"]!.Value<bool>().Should().BeTrue();
        data["errorCount"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task ValidateCode_WithNoContextAndStandaloneFalse_WrapsInMinimalClassWithSystemUsings()
    {
        var data = await CallAndGetDataAsync("roslyn:validate_code", new
        {
            code = "Console.WriteLine(\"hi\");",
            standalone = false
        });
        data["compiles"]!.Value<bool>().Should().BeTrue(
            "the no-context wrap injects 'using System' so Console is resolvable");
        data["errorCount"]!.Value<int>().Should().Be(0);
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
        var calls = (data["calls"] as JArray)!;
        calls.Should().NotBeEmpty();
        // GetHealthCheckAsync's body calls GetProjectCompilationAsync.
        // The tool's shortName field is "{ContainingType}.{Method}" per Inspection.cs:897.
        // The predicate `?.` chain returning false on null is safe — `.Any` returns false
        // if no entry matches, and the outer .Should().BeTrue() fails.
        calls.Any(c =>
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
        var unused = (data["unusedSymbols"] as JArray)!;
        unused.Count.Should().BeLessOrEqualTo(10, "maxResults must cap returnedCount");

        // Every returned entry must conform to the full UnusedSymbolEntry shape so
        // a field rename in the data class would fail this test. Includes the
        // containingType and column fields the impl emits (Inspection.cs:410-420)
        // that the previous test ignored.
        foreach (var entry in unused)
        {
            entry["name"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["fullyQualifiedName"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["kind"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["accessibility"]!.Value<string>().Should().NotBeNullOrEmpty();
            entry["containingType"].Should().NotBeNull(
                "every entry must carry containingType (may be null for top-level types)");
            entry["filePath"]!.Value<string>()!.Should().EndWith(".cs");
            entry["line"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
            entry["column"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
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
        data["compiles"]!.Value<bool>().Should().BeTrue();
        data["errorCount"]!.Value<int>().Should().Be(0);
        (data["errors"] as JArray)!.Count.Should().Be(0);
    }

    [Fact]
    public async Task ValidateCode_BrokenCode_ReportsCompilerErrorIds()
    {
        var data = await CallAndGetDataAsync("roslyn:validate_code", new
        {
            code = "public class Test { public void Foo() { !!! invalid syntax } }",
            standalone = true
        });
        data["compiles"]!.Value<bool>().Should().BeFalse();
        var errors = (data["errors"] as JArray)!;
        errors.Should().NotBeEmpty();
        // Compiler errors are tagged with "CS{number}" diagnostic IDs.
        errors.Any(e => e["id"]?.Value<string>()?.StartsWith("CS") == true)
            .Should().BeTrue("Roslyn surfaces compiler errors with CS-prefixed IDs");
    }

    [Fact]
    public async Task GetComplexityMetrics_OnRoslynServiceFile_FindsLoadSolutionAsyncWithCyclomatic()
    {
        var data = await CallAndGetDataAsync("roslyn:get_complexity_metrics", new
        {
            filePath = Fixture.RoslynServicePath
        });
        data["scope"]!.Value<string>().Should().Be("file");
        var methods = (data["methods"] as JArray)!;
        methods.Should().NotBeEmpty();

        var loadSolution = methods.FirstOrDefault(m =>
            m["name"]?.Value<string>() == "LoadSolutionAsync");
        loadSolution.Should().NotBeNull(
            "LoadSolutionAsync must appear in the per-method breakdown");
        loadSolution!["metrics"]!["cyclomatic"]!.Value<int>().Should().BeGreaterOrEqualTo(1,
            "every method has cyclomatic complexity >= 1");
    }

    [Fact]
    public async Task FindCircularDependencies_Project_NoCyclesInOurSolution()
    {
        var data = await CallAndGetDataAsync("roslyn:find_circular_dependencies", new
        {
            level = (string?)null
        });
        data["level"]!.Value<string>().Should().Be("project");
        data["hasCycles"]!.Value<bool>().Should().BeFalse(
            "the SharpLens solution has no project-level cycles");
        (data["cycles"] as JArray)!.Count.Should().Be(0);
    }

    [Fact]
    public async Task FindCircularDependencies_Namespace_NoCyclesInOurSolution()
    {
        var data = await CallAndGetDataAsync("roslyn:find_circular_dependencies", new
        {
            level = "namespace"
        });
        data["level"]!.Value<string>().Should().Be("namespace");
        data["hasCycles"]!.Value<bool>().Should().BeFalse(
            "the SharpLens solution has no namespace cycles");
        (data["cycles"] as JArray)!.Count.Should().Be(0);
        data["namespaceCount"]!.Value<int>().Should().BeGreaterThan(0,
            "the solution has multiple namespaces (SharpLensMcp, SharpLensMcp.Tests, etc.)");
        // graph must always be emitted for shape consistency with the project-level form
        (data["graph"] as JObject).Should().NotBeNull(
            "namespace-level response must include graph (Discovery.cs:316-317)");
    }

    [Fact]
    public async Task FindCircularDependencies_BadLevel_ReturnsInvalidParameter()
    {
        // Discovery.cs:233-240 explicitly rejects levels other than 'project' or
        // 'namespace'. Without this test, a regression that silently treated unknown
        // values as 'namespace' (the prior behavior) would go unnoticed.
        var error = await CallAndGetErrorAsync("roslyn:find_circular_dependencies",
            new { level = "garbage" },
            codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Contain("project",
            "the error message must enumerate the supported levels");
        error["message"]!.Value<string>().Should().Contain("namespace");
    }

    [Fact]
    public async Task GetDiagnostics_SeverityInfoFilter_OnlyInfoEntries()
    {
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = "Info",
            includeHidden = false,
            runAnalyzers = false
        });
        var diagnostics = (data["diagnostics"] as JArray)!;
        // Severity filter contract: every entry that survives the filter must match.
        // A clean compiler-only build may yield zero Info diagnostics — that's
        // acceptable; the per-entry foreach passes vacuously, but the count fields
        // are independently locked.
        foreach (var d in diagnostics)
        {
            d["severity"]!.Value<string>().Should().Be("Info",
                "the severity=Info filter must drop every non-Info entry");
        }
        data["errorCount"]!.Value<int>().Should().Be(0,
            "with severity=Info, errorCount must be 0");
        data["warningCount"]!.Value<int>().Should().Be(0,
            "with severity=Info, warningCount must be 0");
    }

    [Fact]
    public async Task GetDiagnostics_IncludeHiddenTrue_IsSupersetOfIncludeHiddenFalse()
    {
        // includeHidden controls whether Severity=Hidden diagnostics surface. Setting
        // it to true must produce a superset (or equal set) of the includeHidden=false
        // call.
        var withoutHidden = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = false
        });
        var withHidden = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = (string?)null,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = true,
            runAnalyzers = false
        });
        var visible = (withoutHidden["diagnostics"] as JArray)!;
        var all = (withHidden["diagnostics"] as JArray)!;
        all.Count.Should().BeGreaterOrEqualTo(visible.Count,
            "the includeHidden=true result set must contain every includeHidden=false entry plus any hidden ones");

        // Every visible (non-hidden) id+line must appear in the larger set.
        // Lock per-entry id+line presence to defeat null-key collision in the HashSet.
        foreach (var d in visible)
        {
            d["id"]!.Value<string>().Should().NotBeNullOrEmpty();
            d["line"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        }
        foreach (var d in all)
        {
            d["id"]!.Value<string>().Should().NotBeNullOrEmpty();
        }
        var visibleIds = visible
            .Select(d => $"{d["id"]!.Value<string>()}:{d["line"]!.Value<int>()}")
            .ToHashSet();
        var allIds = all
            .Select(d => $"{d["id"]!.Value<string>()}:{d["line"]!.Value<int>()}")
            .ToHashSet();
        visibleIds.IsSubsetOf(allIds).Should().BeTrue(
            "every visible diagnostic id+line must also appear in the includeHidden=true set");
    }

    [Fact]
    public async Task GetDiagnostics_FilePathScope_RestrictsToThatFile()
    {
        // Analysis.cs:90-99: when filePath is set, only that file's diagnostics
        // are returned. Pass RoslynService.cs and assert every diagnostic (if any)
        // has a filePath ending in RoslynService.cs.
        var data = await CallAndGetDataAsync("roslyn:get_diagnostics", new
        {
            filePath = Fixture.RoslynServicePath,
            projectPath = (string?)null,
            severity = (string?)null,
            includeHidden = false,
            runAnalyzers = false
        });
        var diagnostics = (data["diagnostics"] as JArray)!;
        foreach (var d in diagnostics)
        {
            d["filePath"]!.Value<string>()!.Should().EndWith("RoslynService.cs",
                "filePath scope must drop diagnostics from other files");
        }
    }

    [Fact]
    public async Task GetProjectStructure_SummaryOnlyFalse_ReturnsFullDetails()
    {
        // Analysis.cs:876+ (full mode) returns per-project: name, filePath,
        // language, outputPath, targetFramework, documentCount, referenceCount,
        // references[], projectReferences[], documents[]. summaryOnly=true skips
        // most of this.
        var data = await CallAndGetDataAsync("roslyn:get_project_structure", new
        {
            includeReferences = false,
            includeDocuments = false,
            summaryOnly = false
        });
        var projects = (data["projects"] as JArray)!;
        projects.Should().NotBeEmpty();
        foreach (var p in projects)
        {
            // Full-mode fields that are absent from summary mode.
            p["filePath"]!.Value<string>()!.Should().EndWith(".csproj");
            p["language"]!.Value<string>().Should().Be("C#");
            p["referenceCount"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
            p["projectReferences"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task GetProjectStructure_ProjectNamePatternFilter_LimitsResults()
    {
        // Analysis.cs:830-838: regex-style glob filter on project name.
        var data = await CallAndGetDataAsync("roslyn:get_project_structure", new
        {
            projectNamePattern = "SharpLensMcp.Tests*",
            summaryOnly = true
        });
        var projects = (data["projects"] as JArray)!;
        projects.Should().NotBeEmpty();
        foreach (var p in projects)
        {
            p["name"]!.Value<string>()!.Should().StartWith("SharpLensMcp.Tests",
                "the projectNamePattern filter must drop projects not matching the prefix");
        }
        // The main SharpLensMcp project (without .Tests) must NOT appear.
        projects.Select(p => p["name"]!.Value<string>()!)
            .Should().NotContain("SharpLensMcp");
    }

    [Fact]
    public async Task GetMissingMembers_OnRoslynService_NoneMissing()
    {
        // RoslynService is a complete partial class. Resolve the MatchesGlobPattern
        // method body dynamically so the test pins to its real position even when
        // the file is edited.
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var methodLine = Array.FindIndex(lines, l => l.Contains("MatchesGlobPattern"));
        methodLine.Should().BeGreaterThan(-1);

        var data = await CallAndGetDataAsync("roslyn:get_missing_members", new
        {
            filePath = Fixture.RoslynServicePath,
            line = methodLine + 4,   // inside the method body
            column = 10
        });
        data["typeName"]!.Value<string>()!.Should().EndWith("RoslynService");
        data["isAbstract"]!.Value<bool>().Should().BeFalse();
        // Inspection.cs returns missingMembers as an empty array (not null) for
        // complete types.
        var missing = (data["missingMembers"] as JArray)!;
        missing.Count.Should().Be(0,
            "RoslynService implements all interface/abstract members it inherits from");
    }
}
