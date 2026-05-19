using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for the Compound Tools category. Each tool aggregates other
// tools, so assertions pin BOTH the documented response shape AND the
// concrete content the aggregation surfaces.
//
// Response shapes:
//  - get_type_overview:          Compound.cs:308-324
//  - analyze_method:             Compound.cs:496-507
//  - get_file_overview:          Compound.cs:585-595
//  - get_method_source:          Inspection.cs:1385-1402
//  - get_method_source_batch:    Inspection.cs:1486-1494
//  - get_instantiation_options:  Inspection.cs:1062-1074
//  - get_project_health:         Quality.cs:572-613
//
// Tightening rule for this file: every accessor uses `!.Value<T>()` (NRE on
// missing) rather than `?.Value<T>()` (silent-pass via short-circuit).
public class CompoundToolsViaMcpTests : McpTestBase
{
    public CompoundToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetTypeOverview_OnRoslynService_ReturnsClassWithKnownMemberCounts()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_overview", new
        {
            typeName = "RoslynService"
        });
        // Lock the documented response shape (Compound.cs:308-324).
        data["typeName"]!.Value<string>()!.Should().Contain("RoslynService");
        data["simpleName"]!.Value<string>().Should().Be("RoslynService",
            "simpleName is the unqualified name (Compound.cs:312)");
        data["typeKind"]!.Value<string>().Should().Be("Class");
        data["namespace"]!.Value<string>().Should().Be("SharpLensMcp");
        data["isAbstract"]!.Value<bool>().Should().BeFalse();
        data["isSealed"]!.Value<bool>().Should().BeFalse();
        data["isStatic"]!.Value<bool>().Should().BeFalse();
        data["interfaceCount"]!.Value<int>().Should().BeGreaterOrEqualTo(0);

        // memberSummary has all five count fields (Compound.cs:290-297).
        var memberSummary = data["memberSummary"]!;
        memberSummary["methods"]!.Value<int>().Should().BeGreaterOrEqualTo(20,
            "RoslynService has well over 20 ordinary methods across the partials");
        memberSummary["fields"]!.Value<int>().Should().BeGreaterOrEqualTo(1,
            "RoslynService has fields like _workspace, _solution, etc.");
        memberSummary["properties"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        memberSummary["events"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        memberSummary["constructors"]!.Value<int>().Should().BeGreaterOrEqualTo(1,
            "RoslynService must have at least the default constructor");

        // godotAttributes — all four must be false on a non-Godot project (Compound.cs:300-306).
        var godot = data["godotAttributes"]!;
        godot["hasExport"]!.Value<bool>().Should().BeFalse();
        godot["hasSignal"]!.Value<bool>().Should().BeFalse();
        godot["hasTool"]!.Value<bool>().Should().BeFalse();
        godot["hasGlobalClass"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetTypeOverview_OnFourFixtureKinds_DistinguishesRecordClassRecordStructAndPlain()
    {
        // The RecordFixture explicitly declares four control types to exercise
        // GetTypeKindString (RoslynService.cs:59-66). The prior test only
        // checked Person → "Record"; a regression returning "Record" for every
        // call would pass. Each case below pins its expected typeKind so a
        // regression on any branch of GetTypeKindString fails loudly.
        var person = await CallAndGetDataAsync("roslyn:get_type_overview", new { typeName = "Person" });
        person["typeKind"]!.Value<string>().Should().Be("Record",
            "Person is a `record class` — TypeKind.Class + IsRecord=true → 'Record'");

        var point2d = await CallAndGetDataAsync("roslyn:get_type_overview", new { typeName = "Point2D" });
        point2d["typeKind"]!.Value<string>().Should().Be("RecordStruct",
            "Point2D is a `record struct` — TypeKind.Struct + IsRecord=true → 'RecordStruct'");

        var customer = await CallAndGetDataAsync("roslyn:get_type_overview", new { typeName = "PlainCustomer" });
        customer["typeKind"]!.Value<string>().Should().Be("Class",
            "PlainCustomer is a plain class (control case)");

        var point = await CallAndGetDataAsync("roslyn:get_type_overview", new { typeName = "PlainPoint" });
        point["typeKind"]!.Value<string>().Should().Be("Struct",
            "PlainPoint is a plain struct (control case)");
    }

    [Fact]
    public async Task AnalyzeMethod_OnLoadSolutionAsync_ReturnsExpectedSignatureAndCallers()
    {
        var data = await CallAndGetDataAsync("roslyn:analyze_method", new
        {
            typeName = "RoslynService",
            methodName = "LoadSolutionAsync",
            includeCallers = true,
            includeOutgoingCalls = false
        });

        // signature is a nested object (Compound.cs:376-391).
        var signature = data["signature"]!;
        signature["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        signature["returnType"]!.Value<string>()!.Should().Contain("Task<object>",
            "LoadSolutionAsync returns Task<object>");
        // Roslyn's default ToDisplayString omits parameter names — the rendered
        // signature is "...LoadSolutionAsync(string)"; the parameter NAME is in
        // parameters[], the TYPE in the full signature.
        signature["fullSignature"]!.Value<string>()!.Should().Contain("LoadSolutionAsync(string)");
        signature["isAsync"]!.Value<bool>().Should().BeTrue();
        signature["isStatic"]!.Value<bool>().Should().BeFalse();
        signature["accessibility"]!.Value<string>().Should().Be("Public");

        var parameters = (signature["parameters"] as JArray)!;
        parameters.Should().HaveCount(1, "LoadSolutionAsync takes exactly one parameter");
        parameters[0]["name"]!.Value<string>().Should().Be("solutionPath");
        parameters[0]["type"]!.Value<string>().Should().Be("string");
        parameters[0]["isOptional"]!.Value<bool>().Should().BeFalse();

        // includeCallers=true → callers populated; includeOutgoingCalls=false →
        // outgoingCalls must be null (Compound.cs:423, 500).
        var callers = (data["callers"] as JArray)!;
        callers.Should().NotBeEmpty();
        data["totalCallers"]!.Value<int>().Should().BeGreaterOrEqualTo(1);
        data["callersShown"]!.Value<int>().Should().Be(callers.Count,
            "callersShown must match the actual returned count (Compound.cs:502)");
        data["outgoingCalls"]!.Type.Should().Be(JTokenType.Null,
            "includeOutgoingCalls=false → outgoingCalls is null");
        data["overloadCount"]!.Value<int>().Should().BeGreaterOrEqualTo(1);
        data["location"].Should().NotBeNull();

        // Each callers entry has callingMethod, containingType, locations[].
        foreach (var c in callers)
        {
            c["callingMethod"]!.Value<string>().Should().NotBeNullOrEmpty();
            c["locations"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task AnalyzeMethod_WithOutgoingCalls_ListsCalls()
    {
        var data = await CallAndGetDataAsync("roslyn:analyze_method", new
        {
            typeName = "RoslynService",
            methodName = "LoadSolutionAsync",
            includeCallers = false,
            includeOutgoingCalls = true
        });
        // includeCallers=false → callers null (Compound.cs:394, 500).
        data["callers"]!.Type.Should().Be(JTokenType.Null,
            "includeCallers=false → callers is null");
        data["totalCallers"]!.Value<int>().Should().Be(0);

        // outgoingCalls populated. Per Compound.cs:455-462 each entry has
        // method, shortName, returnType, isAsync, isExternal.
        var outgoing = (data["outgoingCalls"] as JArray)!;
        outgoing.Should().NotBeEmpty(
            "LoadSolutionAsync makes several method calls inside its body");
        data["totalOutgoingCalls"]!.Value<int>().Should().BeGreaterOrEqualTo(outgoing.Count);
        foreach (var c in outgoing)
        {
            c["method"]!.Value<string>().Should().NotBeNullOrEmpty();
            c["shortName"]!.Value<string>().Should().NotBeNullOrEmpty();
            c["returnType"]!.Value<string>().Should().NotBeNullOrEmpty();
            c["isAsync"].Should().NotBeNull();
            c["isExternal"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task AnalyzeMethod_TypeNotFound_ReturnsTypeNotFound()
    {
        // Compound.cs:357-364: unknown typeName → TypeNotFound.
        var error = await CallAndGetErrorAsync("roslyn:analyze_method", new
        {
            typeName = "DoesNotExist_12345",
            methodName = "DoesNotMatter"
        }, codeContains: ErrorCodes.TypeNotFound);
        error["message"]!.Value<string>()!.Should().Contain("DoesNotExist_12345",
            "the error message must echo the bad type name for caller correlation");
    }

    [Fact]
    public async Task AnalyzeMethod_MethodNotFoundOnType_ReturnsSymbolNotFound()
    {
        // Compound.cs:367-372: type resolves but method name doesn't exist on it.
        // Distinct branch from TypeNotFound (covered separately).
        var error = await CallAndGetErrorAsync("roslyn:analyze_method", new
        {
            typeName = "RoslynService",
            methodName = "DoesNotExist_12345"
        }, codeContains: ErrorCodes.SymbolNotFound);
        error["message"]!.Value<string>()!.Should().Contain("DoesNotExist_12345");
    }

    [Fact]
    public async Task AnalyzeMethod_EmptyTypeName_ReturnsInvalidParameter()
    {
        // Compound.cs:349-354: whitespace-or-empty typeName/methodName.
        var error = await CallAndGetErrorAsync("roslyn:analyze_method", new
        {
            typeName = "",
            methodName = "LoadSolutionAsync"
        }, codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("typeName");
    }

    [Fact]
    public async Task AnalyzeDataFlow_OnEmptyRegion_ReturnsAnalysisFailed()
    {
        // Compound.cs:62-70: line range that doesn't enclose any statements
        // (e.g., a using-directive line) → AnalysisFailed with the "No
        // statements found" message.
        var error = await CallAndGetErrorAsync("roslyn:analyze_data_flow", new
        {
            filePath = Fixture.RoslynServicePath,
            startLine = 0, // line 0 is a `using` directive
            endLine = 0
        }, codeContains: ErrorCodes.AnalysisFailed);
        error["message"]!.Value<string>()!.Should().Contain("No statements found");
    }

    [Fact]
    public async Task AnalyzeControlFlow_OnEmptyRegion_ReturnsAnalysisFailed()
    {
        // Compound.cs:172-179: same no-statements branch for control flow.
        var error = await CallAndGetErrorAsync("roslyn:analyze_control_flow", new
        {
            filePath = Fixture.RoslynServicePath,
            startLine = 0,
            endLine = 0
        }, codeContains: ErrorCodes.AnalysisFailed);
        error["message"]!.Value<string>()!.Should().Contain("No statements found");
    }

    [Fact]
    public async Task GetFileOverview_NonExistentFile_ReturnsFileNotInSolution()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_file_overview", new
        {
            filePath = "/does/not/exist/Nope.cs"
        }, codeContains: ErrorCodes.FileNotInSolution);
        error["message"]!.Value<string>()!.Should().Contain("Nope.cs");
    }

    [Fact]
    public async Task AnalyzeDataFlow_NonExistentFile_ReturnsFileNotInSolution()
    {
        var error = await CallAndGetErrorAsync("roslyn:analyze_data_flow", new
        {
            filePath = "/does/not/exist/Nope.cs",
            startLine = 0,
            endLine = 1
        }, codeContains: ErrorCodes.FileNotInSolution);
        error["message"]!.Value<string>()!.Should().Contain("Nope.cs");
    }

    [Fact]
    public async Task AnalyzeControlFlow_NonExistentFile_ReturnsFileNotInSolution()
    {
        var error = await CallAndGetErrorAsync("roslyn:analyze_control_flow", new
        {
            filePath = "/does/not/exist/Nope.cs",
            startLine = 0,
            endLine = 1
        }, codeContains: ErrorCodes.FileNotInSolution);
        error["message"]!.Value<string>()!.Should().Contain("Nope.cs");
    }

    [Fact]
    public async Task GetFileOverview_OnRoslynService_ReportsTypeDeclarationsIncludingRoslynService()
    {
        var data = await CallAndGetDataAsync("roslyn:get_file_overview", new
        {
            filePath = Fixture.RoslynServicePath
        });
        // Lock the documented response shape (Compound.cs:585-595).
        data["filePath"]!.Value<string>()!.Should().Contain("RoslynService");
        data["projectName"]!.Value<string>().Should().Be("SharpLensMcp",
            "RoslynService.cs lives in the main SharpLensMcp project");
        data["namespace"]!.Value<string>().Should().Be("SharpLensMcp");
        data["lineCount"]!.Value<int>().Should().BeGreaterThan(400,
            "RoslynService.cs has hundreds of lines after the partial-class split");
        data["usingCount"]!.Value<int>().Should().BeGreaterThan(0,
            "RoslynService.cs has multiple using directives");
        data["diagnosticSummary"].Should().NotBeNull(
            "diagnosticSummary must always be emitted (may be an empty dictionary)");

        var typeDecls = (data["typeDeclarations"] as JArray)!;
        typeDecls.Should().NotBeEmpty();
        var roslynServiceDecl = typeDecls.FirstOrDefault(t =>
            t["name"]?.Value<string>() == "RoslynService");
        roslynServiceDecl.Should().NotBeNull(
            "the file declares the RoslynService partial class");
        // Per-decl shape (Compound.cs:563-569): name, kind, line, memberCount.
        roslynServiceDecl!["kind"]!.Value<string>().Should().Be("Class",
            "kind strips the 'Declaration' suffix; ClassDeclaration → 'Class'");
        roslynServiceDecl["line"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        roslynServiceDecl["memberCount"]!.Value<int>().Should().BeGreaterThan(0,
            "RoslynService partial in this file declares at least one member");
    }

    [Fact]
    public async Task GetMethodSource_ReturnsFullSourceWithSignatureAndBody()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_source", new
        {
            typeName = "RoslynService",
            methodName = "GetHealthCheckAsync"
        });
        // Lock the documented response shape (Inspection.cs:1385-1402).
        data["typeName"]!.Value<string>()!.Should().Contain("RoslynService");
        data["methodName"]!.Value<string>().Should().Be("GetHealthCheckAsync");
        data["signature"]!.Value<string>()!.Should().Contain("GetHealthCheckAsync");
        data["overloadIndex"]!.Value<int>().Should().Be(0,
            "default overloadIndex is 0 when not specified (Inspection.cs:1323)");
        data["totalOverloads"]!.Value<int>().Should().Be(1,
            "GetHealthCheckAsync has exactly one overload");
        data["lineCount"]!.Value<int>().Should().BeGreaterThan(20,
            "GetHealthCheckAsync spans well over 20 lines");

        // location { filePath, startLine, endLine } (Inspection.cs:1393-1398).
        // Note: the impl uses method.Locations.GetLineSpan() — that's the
        // IDENTIFIER token's span (one line for a single-line declaration),
        // NOT the full method body span. So startLine == endLine is normal.
        var location = data["location"]!;
        location["filePath"]!.Value<string>()!.Should().EndWith(".cs");
        location["startLine"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        location["endLine"]!.Value<int>().Should().BeGreaterOrEqualTo(
            location["startLine"]!.Value<int>(),
            "endLine is >= startLine (equal when the identifier fits on one line)");

        var source = data["fullSource"]!.Value<string>()!;
        source.Should().Contain("public async Task<object> GetHealthCheckAsync()",
            "fullSource must include the exact declaration");
        source.Should().Contain("CreateSuccessResponse",
            "the body uses CreateSuccessResponse");

        data["bodySource"]!.Value<string>()!.Should().NotBeNullOrEmpty(
            "bodySource is populated for block-bodied methods (Inspection.cs:1376-1379)");
    }

    [Fact]
    public async Task GetMethodSource_InvalidOverloadIndex_ReturnsInvalidParameter()
    {
        // Inspection.cs:1324-1330: out-of-range overloadIndex → InvalidParameter
        // with a message enumerating the available range.
        var error = await CallAndGetErrorAsync("roslyn:get_method_source", new
        {
            typeName = "RoslynService",
            methodName = "GetHealthCheckAsync",
            overloadIndex = 99
        }, codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("Invalid overloadIndex",
            "the impl's documented error prefix");
        error["message"]!.Value<string>()!.Should().Contain("99");
    }

    [Fact]
    public async Task GetMethodSourceBatch_ReturnsBothSourcesWithFullEnvelopeShape()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_source_batch", new
        {
            methods = new object[]
            {
                new { typeName = "RoslynService", methodName = "LoadSolutionAsync" },
                new { typeName = "RoslynService", methodName = "GetHealthCheckAsync" }
            }
        });
        // Lock the batch-tool metadata shape (Inspection.cs:1486-1494).
        data["totalRequested"]!.Value<int>().Should().Be(2);
        data["successCount"]!.Value<int>().Should().Be(2);
        data["errorCount"]!.Value<int>().Should().Be(0);

        var results = (data["results"] as JArray)!;
        results.Should().HaveCount(2);

        // Each result envelope (Inspection.cs:1466-1472) has typeName, methodName,
        // success=true, data{inner}. Tightening from `results[0]["data"]?["fullSource"]`
        // — that `["data"]?` chain silent-passes if the data field is missing.
        foreach (var r in results)
        {
            r["typeName"]!.Value<string>()!.Should().Contain("RoslynService");
            r["methodName"]!.Value<string>().Should().NotBeNullOrEmpty();
            r["success"]!.Value<bool>().Should().BeTrue();
            r["data"]!["fullSource"]!.Value<string>()!.Should().NotBeNullOrEmpty();
        }

        results[0]["methodName"]!.Value<string>().Should().Be("LoadSolutionAsync");
        results[0]["data"]!["fullSource"]!.Value<string>()!.Should().Contain("LoadSolutionAsync");
        results[1]["methodName"]!.Value<string>().Should().Be("GetHealthCheckAsync");
        results[1]["data"]!["fullSource"]!.Value<string>()!.Should().Contain("GetHealthCheckAsync");
    }

    [Fact]
    public async Task GetInstantiationOptions_OnRoslynService_ListsParameterlessConstructor()
    {
        var data = await CallAndGetDataAsync("roslyn:get_instantiation_options", new
        {
            typeName = "RoslynService"
        });
        // Lock the documented response shape (Inspection.cs:1062-1074).
        data["typeName"]!.Value<string>()!.Should().Contain("RoslynService");
        data["typeKind"]!.Value<string>().Should().Be("Class");
        data["isAbstract"]!.Value<bool>().Should().BeFalse();
        data["isStatic"]!.Value<bool>().Should().BeFalse();
        data["implementsIDisposable"]!.Value<bool>().Should().BeFalse(
            "RoslynService does not implement IDisposable");
        data["hasBuilder"]!.Value<bool>().Should().BeFalse(
            "no RoslynServiceBuilder type exists in the solution");
        // hint is null for a non-disposable, non-abstract, non-interface concrete type.
        data["hint"]!.Type.Should().Be(JTokenType.Null,
            "no hint applies to a non-disposable concrete class (Inspection.cs:1054-1060)");

        var ctors = (data["constructors"] as JArray)!;
        ctors.Should().NotBeEmpty();
        // RoslynService has a single parameterless public constructor.
        var defaultCtor = ctors.FirstOrDefault(c =>
            (c["parameters"] as JArray)?.Count == 0);
        defaultCtor.Should().NotBeNull("RoslynService exposes a parameterless ctor");
        // Per-ctor shape (Inspection.cs:982-993): signature, accessibility, parameters, isObsolete.
        defaultCtor!["signature"]!.Value<string>().Should().NotBeNullOrEmpty();
        defaultCtor["accessibility"]!.Value<string>().Should().Be("Public");
        defaultCtor["isObsolete"]!.Value<bool>().Should().BeFalse();

        // RoslynService has no static factory methods returning RoslynService.
        var factories = (data["factoryMethods"] as JArray)!;
        factories.Count.Should().Be(0,
            "no public static method on RoslynService returns RoslynService");
        // externalFactories is always an array (may be empty).
        (data["externalFactories"] as JArray).Should().NotBeNull();
    }

    [Fact]
    public async Task GetProjectHealth_OnSharpLensMcp_ReportsCleanBuildAcrossAllSections()
    {
        var data = await CallAndGetDataAsync("roslyn:get_project_health", new
        {
            projectName = "SharpLensMcp",
            includeAnalyzers = false,
            topN = 3
        });
        data["projectName"]!.Value<string>().Should().Be("SharpLensMcp");

        // All four aggregate sections must be present and have expected substructure.
        var diag = data["diagnostics"]!;
        diag["errorCount"]!.Value<int>().Should().Be(0,
            "the codebase compiles clean");
        diag["warningCount"]!.Value<int>().Should().Be(0,
            "the codebase has no compiler warnings");
        diag["analyzerCount"]!.Value<int>().Should().Be(0,
            "includeAnalyzers=false → analyzerCount must be 0 (Quality.cs:524)");

        data["unusedCode"]!["count"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        data["coupling"]!["godObjectCandidates"]!.Value<int>().Should().BeGreaterOrEqualTo(0);
        data["coverage"]!["uncoveredPublicSurface"]!.Value<int>().Should().BeGreaterOrEqualTo(0);

        // Summary string must contain all five buckets per Quality.cs:569 format.
        var summary = data["summary"]!.Value<string>()!;
        summary.Should().Contain("0 errors");
        summary.Should().Contain("0 warnings");
        summary.Should().Contain("god-object");
        summary.Should().Contain("uncovered");
        summary.Should().Contain("unused");
    }

    [Fact]
    public async Task GetProjectHealth_UnknownProject_ReturnsInvalidParameterWithEchoedName()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:get_project_health",
            new { projectName = "DoesNotExist_12345" },
            codeContains: ErrorCodes.InvalidParameter);
        // The helper already enforced codeContains=InvalidParameter. The prior
        // `error["code"]?.Value<string>().Should().Be(...)` was redundant AND
        // silent-pass — replaced with a useful message lock.
        error["message"]!.Value<string>()!.Should().Contain("DoesNotExist_12345",
            "the error message must echo the bad project name for caller correlation");
    }
}
