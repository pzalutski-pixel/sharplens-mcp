using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for the Navigation & Discovery category. Every assertion is
// grounded in specific known facts about the loaded SharpLens solution + the
// engineered fixtures (InterfaceHierarchyFixture, ReferenceKindsFixture,
// CallGraphFixture, RecordFixture).
//
// Tightening rule for this file: every accessor uses `!.Value<T>()`. Silent-
// pass chains and the `?.Value<T>() ?? ""` defensive-fallback pattern have
// been replaced with NRE-on-missing access.
public class NavigationToolsViaMcpTests : McpTestBase
{
    public NavigationToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetSymbolInfo_OnRoslynServiceClass_ReturnsExpectedMetadata()
    {
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var classLine = Array.FindIndex(lines, l => l.Contains("class RoslynService"));
        classLine.Should().BeGreaterThan(-1);

        var data = await CallAndGetDataAsync("roslyn:get_symbol_info", new
        {
            filePath = Fixture.RoslynServicePath, line = classLine, column = 20
        });
        data["kind"]!.Value<string>().Should().Be("NamedType");
        data["name"]!.Value<string>().Should().Be("RoslynService");
        data["typeKind"]!.Value<string>().Should().Be("Class");
        // Impl emits the Dictionary key "containingNamespace" (RoslynService.cs:930);
        // the prior `data["namespace"]?.Value<string>().Should().Be(...)` was a silent-
        // pass against a non-existent field.
        data["containingNamespace"]!.Value<string>().Should().Be("SharpLensMcp",
            "the partial class is in the SharpLensMcp namespace");
        // fullyQualifiedName and location must be emitted for every symbol result.
        data["fullyQualifiedName"]!.Value<string>().Should().NotBeNullOrEmpty();
        data["location"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetSymbolInfo_AtFileTop_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:get_symbol_info",
            new { filePath = Fixture.RoslynServicePath, line = 0, column = 0 },
            codeContains: ErrorCodes.SymbolNotFound);
        // Helper already enforced codeContains; lock message contains the position
        // detail instead of the redundant silent-pass code re-check.
        error["message"]!.Value<string>().Should().NotBeNullOrEmpty(
            "the impl must surface a SymbolNotFound message");
    }

    [Fact]
    public async Task GoToDefinition_OnCreateSuccessResponseCall_LocatesDeclaration()
    {
        // Both McpServer.cs (line 1979) and RoslynService.cs (line 72) declare a
        // CreateSuccessResponse method — filter LocateSymbolAsync to the RoslynService
        // overload so go_to_definition lands deterministically in RoslynService.cs.
        var (file, line, col) = await LocateSymbolAsync(
            "CreateSuccessResponse", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RoslynService") == true);
        var data = await CallAndGetDataAsync("roslyn:go_to_definition", new
        {
            filePath = file, line, column = col
        });
        // The impl nests filePath/line under data["definition"] (Navigation.cs:337-339);
        // the prior `data["filePath"]?.Value<string>()...` silent-passed against a
        // non-existent top-level field.
        var definition = data["definition"]!;
        definition["filePath"]!.Value<string>()!.Should().EndWith("RoslynService.cs");
        definition["line"]!.Value<int>().Should().BeGreaterThan(0,
            "the declaration is well past the file header");

        var symbol = data["symbol"]!;
        symbol["name"]!.Value<string>().Should().Be("CreateSuccessResponse");
        symbol["kind"]!.Value<string>().Should().Be("Method");
    }

    [Fact]
    public async Task FindReferences_OnCreateSuccessResponse_LocksTotalAndRefShape()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "CreateSuccessResponse", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col
        });
        data["symbolName"]!.Value<string>().Should().Be("CreateSuccessResponse");
        data["symbolKind"]!.Value<string>().Should().Be("Method");

        var refs = (data["references"] as JArray)!;
        refs.Should().NotBeEmpty();
        data["totalReferences"]!.Value<int>().Should().BeGreaterOrEqualTo(2);

        // Every reference must conform to the documented shape — filePath is a .cs
        // file and kind is one of the documented classifier values.
        var validKinds = new[] { "read", "write", "invocation", "cast", "typeof", "nameof", "attribute" };
        foreach (var r in refs)
        {
            r["filePath"]!.Value<string>()!.Should().EndWith(".cs");
            r["kind"]!.Value<string>().Should().BeOneOf(validKinds);
        }
    }

    [Fact]
    public async Task FindReferences_KindFilterWrite_OnTrackedField_OnlyReturnsWrites()
    {
        var (file, line, col) = await LocateSymbolAsync("TrackedField", kind: "Field");

        // Unfiltered for the comparison.
        var unfiltered = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col
        });
        var totalUnfiltered = unfiltered["totalReferences"]!.Value<int>();

        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "write"
        });
        var refs = (data["references"] as JArray)!;
        refs.Should().NotBeEmpty(
            "the fixture has at least one write site on TrackedField");
        refs.All(r => r["kind"]!.Value<string>() == "write")
            .Should().BeTrue("the kind filter must drop every non-write reference");
        data["kindFilter"]!.Value<string>().Should().Be("write");
        data["totalReferencesAfterFilter"]!.Value<int>().Should().BeLessThan(totalUnfiltered,
            "the unfiltered total has reads/invocations/typeof in addition to writes");
        data["totalReferences"]!.Value<int>().Should().Be(totalUnfiltered);
    }

    [Fact]
    public async Task FindReferences_KindFilterCast_OnTrackedTarget_ReturnsAtLeastOneCast()
    {
        var (file, line, col) = await LocateSymbolAsync("TrackedTarget", kind: "Class");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "cast"
        });
        var refs = (data["references"] as JArray)!;
        refs.Should().NotBeEmpty(
            "ReferenceKindsFixture has `(TrackedTarget)boxed` — at least one cast ref");
        refs.All(r => r["kind"]!.Value<string>() == "cast").Should().BeTrue();
    }

    [Fact]
    public async Task FindReferences_KindFilterInvocation_OnTrackedField_OnlyReturnsInvocations()
    {
        var (file, line, col) = await LocateSymbolAsync("TrackedField", kind: "Field");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "invocation"
        });
        var refs = (data["references"] as JArray)!;
        refs.Should().NotBeEmpty(
            "TrackedField is invoked as `TrackedField()` in the fixture");
        refs.All(r => r["kind"]!.Value<string>() == "invocation").Should().BeTrue();
    }

    [Fact]
    public async Task FindReferences_KindFilterTypeof_OnTrackedTarget_OnlyReturnsTypeofRefs()
    {
        var (file, line, col) = await LocateSymbolAsync("TrackedTarget", kind: "Class");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "typeof"
        });
        var refs = (data["references"] as JArray)!;
        refs.Should().NotBeEmpty(
            "the fixture has `typeof(TrackedTarget)`");
        refs.All(r => r["kind"]!.Value<string>() == "typeof").Should().BeTrue();
    }

    [Fact]
    public async Task FindReferences_KindFilterNameof_OnTrackedField_OnlyReturnsNameofRefs()
    {
        var (file, line, col) = await LocateSymbolAsync("TrackedField", kind: "Field");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "nameof"
        });
        var refs = (data["references"] as JArray)!;
        refs.Should().NotBeEmpty(
            "the fixture has `nameof(TrackedField)` as an attribute argument");
        refs.All(r => r["kind"]!.Value<string>() == "nameof").Should().BeTrue();
    }

    [Fact]
    public async Task FindReferences_KindFilterAttribute_OnTrackedMarker_OnlyReturnsAttributeRefs()
    {
        var (file, line, col) = await LocateSymbolAsync("TrackedMarker", kind: "Field");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "attribute"
        });
        var refs = (data["references"] as JArray)!;
        refs.Should().NotBeEmpty(
            "TrackedMarker is used as an attribute argument with no wrapper");
        refs.All(r => r["kind"]!.Value<string>() == "attribute").Should().BeTrue();
    }

    [Fact]
    public async Task FindReferences_KindFilterRead_OnTrackedField_OnlyReturnsReadRefs()
    {
        var (file, line, col) = await LocateSymbolAsync("TrackedField", kind: "Field");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "read"
        });
        var refs = (data["references"] as JArray)!;
        refs.Should().NotBeEmpty(
            "TrackedField is read as `var read = TrackedField;`");
        refs.All(r => r["kind"]!.Value<string>() == "read").Should().BeTrue();
    }

    [Fact]
    public async Task GoToDefinition_OnMetadataSymbol_ReturnsSymbolNotFound()
    {
        // GoToDefinition on a BCL type's usage: the symbol resolves but its
        // definition is in metadata, not source (Navigation.cs:314-322).
        // Position cursor on a `using System;` line — actually no, that's the
        // using directive. Need to find a usage of a BCL type in source.
        // The line `using System;` at the top of RoslynService.cs has a reference
        // to the System namespace (no source location for namespace?). Instead use
        // a Type usage like `Console.Error` — first find the line.
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        // Look for a Console.Error reference. RoslynService.cs:281 uses Console.Error.WriteLine.
        var consoleLine = Array.FindIndex(lines, l =>
            l.Contains("Console.Error.WriteLine"));
        consoleLine.Should().BeGreaterThan(-1, "RoslynService.cs uses Console.Error");
        var consoleCol = lines[consoleLine].IndexOf("Console", StringComparison.Ordinal);

        var error = await CallAndGetErrorAsync("roslyn:go_to_definition", new
        {
            filePath = Fixture.RoslynServicePath,
            line = consoleLine,
            column = consoleCol
        }, codeContains: ErrorCodes.SymbolNotFound);
        error["hint"]!.Value<string>()!.Should().Contain("metadata",
            "the metadata-only branch hint at Navigation.cs:319 mentions metadata");
    }

    [Fact]
    public async Task FindImplementations_OnIShapeFixture_ReturnsAllThreeWithFqn()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "IShapeFixture", kind: "Interface");
        var data = await CallAndGetDataAsync("roslyn:find_implementations", new
        {
            filePath = file, line, column = col
        });
        // Lock the response shape (Navigation.cs:467-473).
        data["baseType"]!.Value<string>()!.Should().EndWith("IShapeFixture");
        data["totalImplementations"]!.Value<int>().Should().BeGreaterOrEqualTo(3,
            "InterfaceHierarchyFixture declares Circle, Rectangle, AND transitively Square");

        var impls = (data["implementations"] as JArray)!;
        impls.Should().NotBeEmpty();
        // find_implementations returns fully-qualified names per Navigation.cs:460.
        var names = impls.Select(i => i["name"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.EndsWith(".FixtureCircle"));
        names.Should().Contain(n => n.EndsWith(".FixtureRectangle"));
        names.Should().Contain(n => n.EndsWith(".FixtureSquare"),
            "FixtureSquare derives from FixtureRectangle, so it transitively implements IShapeFixture");

        foreach (var i in impls)
        {
            i["kind"]!.Value<string>().Should().Be("Class");
            i["locations"].Should().NotBeNull(
                "each implementation must carry source locations");
        }
    }

    [Fact]
    public async Task FindCallers_OnEnsureSolutionLoaded_HasManyCallersIncludingKnownOnes()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "EnsureSolutionLoaded", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:find_callers", new
        {
            filePath = file, line, column = col
        });
        var callers = (data["callers"] as JArray)!;
        callers.Should().NotBeEmpty();
        // EnsureSolutionLoaded is invoked from virtually every public tool method
        // EXCEPT LoadSolutionAsync (which is the one that loads the solution).
        callers.Count.Should().BeGreaterOrEqualTo(20,
            "almost every tool method calls EnsureSolutionLoaded at the top");

        // Per-caller shape (Inspection.cs:350-366): callingSymbol {name, kind,
        // containingType, signature}, location {filePath, line, column, lineText}.
        foreach (var c in callers)
        {
            var callingSymbol = c["callingSymbol"]!;
            callingSymbol["name"]!.Value<string>().Should().NotBeNullOrEmpty();
            callingSymbol["kind"]!.Value<string>().Should().NotBeNullOrEmpty();
            callingSymbol["signature"]!.Value<string>().Should().NotBeNullOrEmpty();
            c["location"]!["filePath"]!.Value<string>()!.Should().EndWith(".cs");
        }

        var callerNames = callers
            .Select(c => c["callingSymbol"]!["name"]!.Value<string>()!)
            .ToList();
        callerNames.Should().Contain("SearchSymbolsAsync");
        callerNames.Should().Contain("GetDiagnosticsAsync");
        callerNames.Should().Contain("RenameSymbolAsync");
        callerNames.Should().NotContain("LoadSolutionAsync",
            "LoadSolutionAsync is the loader — it must NOT call EnsureSolutionLoaded itself");
    }

    [Fact]
    public async Task GetCallGraph_CalleesDepth2_OnFixtureChainA_ReachesChainC()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "ChainA", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_call_graph", new
        {
            filePath = file, line, column = col,
            direction = "callees", maxDepth = 2, maxNodes = 50
        });
        var nodes = (data["nodes"] as JArray)!;
        nodes.Should().NotBeEmpty();
        var names = nodes.Select(n => n["fullName"]!.Value<string>()!).ToList();
        names.Should().Contain(n => n.EndsWith(".ChainA()"));
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().Contain(n => n.EndsWith(".ChainC()"),
            "depth:2 from ChainA must reach the leaf ChainC");

        // root must be ChainA.
        data["root"]!["fullName"]!.Value<string>()!.Should().EndWith(".ChainA()");
        // edges array must be emitted by the impl (always present in call graphs).
        (data["edges"] as JArray).Should().NotBeNull(
            "call graph response must always carry an edges array");
    }

    [Fact]
    public async Task GetCallGraph_CalleesDepth1_OnChainA_StopsAtChainBAndFlagsTruncation()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "ChainA", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_call_graph", new
        {
            filePath = file, line, column = col,
            direction = "callees", maxDepth = 1, maxNodes = 50
        });
        var names = (data["nodes"] as JArray)!
            .Select(n => n["fullName"]!.Value<string>()!)
            .ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().NotContain(n => n.EndsWith(".ChainC()"),
            "depth:1 must stop at ChainB");
        data["truncatedByDepth"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task GetCallGraph_DepthOutOfRange_ReturnsInvalidParameterWithRangeInMessage()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "ChainA", kind: "Method");
        var error = await CallAndGetErrorAsync(
            "roslyn:get_call_graph",
            new { filePath = file, line, column = col, maxDepth = 100 },
            codeContains: ErrorCodes.InvalidParameter);
        // Lock the message wording (Inspection.cs:1523) instead of the redundant
        // silent-pass error.code re-check.
        error["message"]!.Value<string>()!.Should().Contain("maxDepth must be between 1 and 10",
            "the impl enumerates the allowed range in the error message");
    }

    [Fact]
    public async Task GetCallGraph_BadDirection_ReturnsInvalidParameterWithAllowedValues()
    {
        // Inspection.cs:1529-1535 explicitly rejects directions other than
        // 'callees', 'callers', or 'both'. Locks the branch — without this, a
        // regression treating unknown values as 'callees' (the prior default
        // behavior) would slip past.
        var (file, line, col) = await LocateSymbolAsync(
            "ChainA", kind: "Method");
        var error = await CallAndGetErrorAsync(
            "roslyn:get_call_graph",
            new { filePath = file, line, column = col, direction = "sideways" },
            codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("direction must be",
            "the impl enumerates the supported direction values");
        error["message"]!.Value<string>()!.Should().Contain("sideways",
            "the error must echo the bad direction value for caller correlation");
    }

    [Fact]
    public async Task GetTypeHierarchy_OnFixtureSquare_ReportsRectangleParentAndInterface()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "FixtureSquare", kind: "Class");
        var data = await CallAndGetDataAsync("roslyn:get_type_hierarchy", new
        {
            filePath = file, line, column = col
        });
        data["typeName"]!.Value<string>()!.Should().EndWith("FixtureSquare");

        var baseTypes = (data["baseTypes"] as JArray)!;
        baseTypes.Should().NotBeEmpty();
        baseTypes.Any(b => b["name"]!.Value<string>()!.EndsWith("FixtureRectangle"))
            .Should().BeTrue("FixtureSquare extends FixtureRectangle");

        // Transitive interface via FixtureRectangle → IShapeFixture.
        var interfaces = (data["interfaces"] as JArray)!;
        interfaces.Any(i => i["name"]!.Value<string>()!.EndsWith("IShapeFixture"))
            .Should().BeTrue("FixtureSquare transitively implements IShapeFixture");
    }

    [Fact]
    public async Task SearchSymbols_OnRoslynService_FindsClassEntry()
    {
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "RoslynService", maxResults = 10
        });
        var results = (data["results"] as JArray)!;
        results.Should().NotBeEmpty();
        var classEntry = results.FirstOrDefault(r =>
            r["name"]?.Value<string>() == "RoslynService"
            && r["kind"]?.Value<string>() == "NamedType");
        classEntry.Should().NotBeNull("the class itself must appear among results");
        // RoslynService is a partial class; Roslyn returns one partial as the
        // canonical declaration. Any of them is acceptable as long as the
        // filename starts with "RoslynService" and is a .cs file.
        var path = classEntry!["location"]!["filePath"]!.Value<string>()!;
        path.Should().EndWith(".cs");
        Path.GetFileName(path).Should().StartWith("RoslynService",
            "the declaration must live in one of the RoslynService.*.cs partials");
    }

    [Fact]
    public async Task SearchSymbols_Pagination_Page2AdvancesOffset()
    {
        // Page 1 with small cap forces hasMore=true and nextOffset=5.
        var page1 = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "*Async", kind = "Method", maxResults = 5, offset = 0
        });
        page1["offset"]!.Value<int>().Should().Be(0);
        page1["hasMore"]!.Value<bool>().Should().BeTrue(
            "the solution has dozens of *Async methods; 5 doesn't exhaust them");
        var nextOffset = page1["pagination"]!["nextOffset"]!.Value<int>();
        nextOffset.Should().Be(5);

        // Page 2 must report offset==nextOffset and return a different page.
        var page2 = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "*Async", kind = "Method", maxResults = 5, offset = nextOffset
        });
        page2["offset"]!.Value<int>().Should().Be(5);
        var page1Names = (page1["results"] as JArray)!
            .Select(r => r["fullyQualifiedName"]!.Value<string>()!).ToList();
        var page2Names = (page2["results"] as JArray)!
            .Select(r => r["fullyQualifiedName"]!.Value<string>()!).ToList();
        page1Names.Intersect(page2Names).Should().BeEmpty(
            "page 2 must not overlap with page 1");
    }

    [Fact]
    public async Task GetCallGraph_DirectionBoth_OnChainB_HasCallerAndCallee()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "ChainB", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_call_graph", new
        {
            filePath = file, line, column = col,
            direction = "both", maxDepth = 2, maxNodes = 50
        });
        var names = (data["nodes"] as JArray)!
            .Select(n => n["fullName"]!.Value<string>()!)
            .ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().Contain(n => n.EndsWith(".ChainA()"),
            "direction=both must include ChainA (the caller of ChainB)");
        names.Should().Contain(n => n.EndsWith(".ChainC()"),
            "direction=both must include ChainC (the callee of ChainB)");
        data["direction"]!.Value<string>().Should().Be("both");
    }

    [Fact]
    public async Task GetExternalTypeInfo_WithMatchingAssemblyName_ResolvesType()
    {
        // Discover the actual containing assembly of System.String via a no-filter
        // call (it varies across runtimes: System.Private.CoreLib vs System.Runtime
        // vs others). Then call with that exact name and assert the assembly matches.
        var discovered = await CallAndGetDataAsync("roslyn:get_external_type_info", new
        {
            typeName = "System.String"
        });
        var actualAssembly = discovered["assembly"]!.Value<string>()!;
        actualAssembly.Should().NotBeNullOrEmpty();

        var filtered = await CallAndGetDataAsync("roslyn:get_external_type_info", new
        {
            typeName = "System.String",
            assemblyName = actualAssembly
        });
        filtered["assembly"]!.Value<string>().Should().Be(actualAssembly);
        filtered["typeName"]!.Value<string>().Should().Be("string");
    }

    [Fact]
    public async Task GetExternalTypeInfo_WrongAssemblyName_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:get_external_type_info",
            new
            {
                typeName = "System.String",
                assemblyName = "Some.Wrong.Assembly.That.Does.Not.Exist"
            },
            codeContains: ErrorCodes.TypeNotFound);
        // Helper already enforced codeContains; lock the assembly-specific hint.
        error["hint"]!.Value<string>()!.Should().Contain("Some.Wrong.Assembly.That.Does.Not.Exist",
            "the assembly-specific hint must echo back the bad assembly name");
    }

    [Fact]
    public async Task GetExternalTypeInfo_IncludeInheritedFalse_ExcludesBaseTypeMembers()
    {
        // FixtureSquare extends FixtureRectangle (which has Width, Height, Area).
        // FixtureSquare's own members: Side. With includeInherited=true we get all four;
        // with includeInherited=false we get only Side.
        // (System.String / List<T> can't be used because their immediate base IS
        // System.Object which the impl excludes from the walk per ExternalApi.cs.)
        var included = await CallAndGetDataAsync("roslyn:get_external_type_info", new
        {
            typeName = "SharpLensMcp.Tests.Fixtures.FixtureSquare",
            includeInherited = true,
            maxMembers = 50
        });
        var includedNames = (included["members"] as JArray)!
            .Select(m => m["name"]!.Value<string>()!)
            .ToList();
        includedNames.Should().Contain("Width", "Width is inherited from FixtureRectangle");
        includedNames.Should().Contain("Side", "Side is FixtureSquare's own member");

        var excluded = await CallAndGetDataAsync("roslyn:get_external_type_info", new
        {
            typeName = "SharpLensMcp.Tests.Fixtures.FixtureSquare",
            includeInherited = false,
            maxMembers = 50
        });
        var excludedNames = (excluded["members"] as JArray)!
            .Select(m => m["name"]!.Value<string>()!)
            .ToList();
        excludedNames.Should().Contain("Side",
            "FixtureSquare's own member must still appear");
        excludedNames.Should().NotContain("Width",
            "includeInherited=false must drop FixtureRectangle's inherited members");
    }

    [Fact]
    public async Task SearchSymbols_GlobPatternAsync_LocksEveryResultIsAsyncMethod()
    {
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "*Async", kind = "Method", maxResults = 100
        });
        var results = (data["results"] as JArray)!;
        results.Count.Should().BeGreaterThan(10);
        foreach (var r in results)
        {
            r["kind"]!.Value<string>().Should().Be("Method");
            r["name"]!.Value<string>()!.Should().EndWith("Async",
                "the *Async glob must filter to names ending in Async");
        }
    }

    [Fact]
    public async Task SemanticQuery_AsyncFilter_EveryMethodResultIsAsync()
    {
        var data = await CallAndGetDataAsync("roslyn:semantic_query", new
        {
            isAsync = true,
            maxResults = 100
        });
        var results = (data["results"] as JArray)!;
        results.Should().NotBeEmpty();
        results.Count.Should().BeGreaterThan(5,
            "the solution has many async methods");

        // The impl's isAsync filter applies ONLY to method symbols (RoslynService.cs:1083);
        // non-method results pass through unfiltered and don't carry an `isAsync` field.
        // Per-method invariant: every method result must have isAsync=true. Sanity
        // check: at least some results must be methods (otherwise the filter is a no-op).
        var methodResults = results
            .Where(r => r["kind"]!.Value<string>() == "Method")
            .ToList();
        methodResults.Should().NotBeEmpty(
            "the result set must include at least one async method (filter would be vacuous otherwise)");
        foreach (var r in methodResults)
        {
            r["isAsync"]!.Value<bool>().Should().BeTrue(
                "every method result must have isAsync=true after filtering by isAsync:true (RoslynService.cs:1083)");
        }
    }

    [Fact]
    public async Task GetTypeMembers_OnRoslynService_IncludesLoadSolutionAndWorkspace()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members", new
        {
            typeName = "RoslynService"
        });
        data["totalMembers"]!.Value<int>().Should().BeGreaterThan(50);

        var members = (data["members"] as JArray)!
            .Select(m => new
            {
                name = m["name"]!.Value<string>()!,
                kind = m["kind"]!.Value<string>()!
            })
            .ToList();
        members.Should().Contain(m => m.name == "LoadSolutionAsync" && m.kind == "Method");
        members.Should().Contain(m => m.name == "_workspace" && m.kind == "Field");
    }

    [Fact]
    public async Task GetTypeMembersBatch_ReturnsTwoNamedResultsWithBatchMetadata()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members_batch", new
        {
            typeNames = new[] { "RoslynService", "McpServer" }
        });
        // Lock the batch-tool metadata shape (TypeDiscovery.cs:169-171).
        data["totalRequested"]!.Value<int>().Should().Be(2);
        data["successCount"]!.Value<int>().Should().Be(2);
        data["errorCount"]!.Value<int>().Should().Be(0);

        var results = (data["results"] as JArray)!;
        results.Count.Should().Be(2);
        var typeNames = results.Select(r => r["typeName"]!.Value<string>()!).ToList();
        typeNames.Should().Contain("RoslynService");
        typeNames.Should().Contain("McpServer");
    }

    [Fact]
    public async Task GetMethodSignature_OnLoadSolutionAsync_LocksParametersAndReturnType()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_signature", new
        {
            typeName = "RoslynService",
            methodName = "LoadSolutionAsync"
        });
        data["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        data["returnType"]!.Value<string>()!.Should().Contain("Task<object>");
        data["isAsync"]!.Value<bool>().Should().BeTrue();
        data["accessibility"]!.Value<string>().Should().Be("Public");
        data["containingType"]!.Value<string>()!.Should().Contain("RoslynService");

        var parameters = (data["parameters"] as JArray)!;
        parameters.Count.Should().Be(1);
        parameters[0]["name"]!.Value<string>().Should().Be("solutionPath");
        parameters[0]["type"]!.Value<string>().Should().Be("string");
        parameters[0]["isOptional"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task GetDerivedTypes_OnFixtureRectangle_ReturnsExactlyFixtureSquare()
    {
        var data = await CallAndGetDataAsync("roslyn:get_derived_types", new
        {
            baseTypeName = "FixtureRectangle",
            includeTransitive = true
        });
        var derived = (data["derivedTypes"] as JArray)!;
        derived.Count.Should().Be(1, "FixtureSquare is the only derived class in the fixture");
        derived[0]["name"]!.Value<string>().Should().Be("FixtureSquare");
        data["totalDerived"]!.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task GetBaseTypes_OnFixtureSquare_ReturnsRectangleAsImmediateBase()
    {
        var data = await CallAndGetDataAsync("roslyn:get_base_types", new
        {
            typeName = "FixtureSquare"
        });
        data["typeName"]!.Value<string>()!.Should().EndWith("FixtureSquare");
        var baseTypes = (data["baseTypes"] as JArray)!;
        baseTypes.Should().NotBeEmpty();
        baseTypes[0]["name"]!.Value<string>().Should().Be("FixtureRectangle",
            "the immediate base of FixtureSquare is FixtureRectangle");

        var interfaces = (data["interfaces"] as JArray)!;
        interfaces.Should().NotBeEmpty();
        interfaces.Any(i => i["name"]!.Value<string>() == "IShapeFixture")
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetAttributes_FactAttribute_FindsManyXunitFactMembers()
    {
        var data = await CallAndGetDataAsync("roslyn:get_attributes", new
        {
            attributeName = "Fact"
        });
        var symbols = (data["symbols"] as JArray)!;
        symbols.Should().NotBeEmpty();
        data["totalFound"]!.Value<int>().Should().BeGreaterThan(100,
            "the test project has well over 100 [Fact]-decorated methods");

        // Every match's attribute.name must be FactAttribute (not some other
        // "Fact"-named attr). The prior `s["attribute"]?["name"]?.Value<string>()
        // .Should().Be("FactAttribute")` chain had TWO `?.` operators that
        // silent-pass if attribute or name field is missing — tightened.
        foreach (var s in symbols)
        {
            s["attribute"]!["name"]!.Value<string>().Should().Be("FactAttribute");
        }
    }

    [Fact]
    public async Task GetContainingMember_AtMatchesGlobPatternBody_ReturnsThatMethodName()
    {
        // Resolve a position deterministically inside MatchesGlobPattern's body
        // by locating the literal `var regexPattern = "^"` initialization.
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var bodyLine = Array.FindIndex(lines, l => l.Contains("var regexPattern = \"^\""));
        bodyLine.Should().BeGreaterThan(-1,
            "MatchesGlobPattern's body contains the regexPattern initialization");

        var data = await CallAndGetDataAsync("roslyn:get_containing_member", new
        {
            filePath = Fixture.RoslynServicePath, line = bodyLine, column = 10
        });
        data["memberName"]!.Value<string>().Should().Be("MatchesGlobPattern");
        data["memberKind"]!.Value<string>().Should().Be("Method");
        data["containingType"]!.Value<string>()!.Should().Contain("RoslynService");
    }

    [Fact]
    public async Task GetMethodOverloads_OnCreateErrorResponse_AllOverloadsShareName()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "CreateErrorResponse", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_method_overloads", new
        {
            filePath = file, line, column = col
        });
        data["methodName"]!.Value<string>().Should().Be("CreateErrorResponse");
        var overloads = (data["overloads"] as JArray)!;
        overloads.Should().NotBeEmpty();
        foreach (var o in overloads)
        {
            // Every overload's signature must include the CreateErrorResponse identifier.
            o["signature"]!.Value<string>()!.Should().Contain("CreateErrorResponse");
            o["returnType"]!.Value<string>().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task FindAttributeUsages_FactAttribute_EveryEntryIsFactDecoratedMethod()
    {
        // totalCount lives in the meta envelope, not on data — so use CallToolAsync
        // and pull it from inner["meta"]. The prior `data["totalCount"]?.Value<int>()`
        // silent-passed against a non-existent data-level field.
        var inner = await CallToolAsync("roslyn:find_attribute_usages", new
        {
            attributeName = "Fact",
            maxResults = 200
        });
        inner["success"]!.Value<bool>().Should().BeTrue();
        var data = inner["data"]!;
        var meta = inner["meta"]!;

        var usages = (data["usages"] as JArray)!;
        usages.Should().NotBeEmpty();
        meta["totalCount"]!.Value<int>().Should().BeGreaterThan(100,
            "the test project has well over 100 [Fact]-decorated methods");
        // Lock the documented data shape (Discovery.cs:66): attributeFilter, usages[].
        data["attributeFilter"]!.Value<string>().Should().Be("Fact");

        foreach (var u in usages)
        {
            u["attributeName"]!.Value<string>().Should().Be("FactAttribute");
            // Test methods or types — the impl tags type-level decorations as
            // "Type" (Discovery.cs:36) and member-level as the member's Kind
            // (Discovery.cs:54). For [Fact] in xUnit, decorations are on methods.
            u["symbolKind"]!.Value<string>().Should().Be("Method",
                "[Fact] is applied to test methods");
        }
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnSystemString_ReturnsCoreMembersWithDocs()
    {
        var data = await CallAndGetDataAsync("roslyn:get_external_type_info", new
        {
            typeName = "System.String"
        });
        data["typeName"]!.Value<string>().Should().Be("string");
        data["typeKind"]!.Value<string>().Should().Be("Class");
        data["assembly"]!.Value<string>().Should().NotBeNullOrEmpty(
            "System.String must report its containing assembly");

        var members = (data["members"] as JArray)!;
        members.Should().NotBeEmpty();
        var names = members.Select(m => m["name"]!.Value<string>()!).ToList();
        names.Should().Contain("Length");
        names.Should().Contain("Substring");
        names.Should().Contain("Trim");

        // At least one well-known member must have an XML doc summary.
        members.Any(m => !string.IsNullOrEmpty(m["xmlDoc"]?.Value<string>()))
            .Should().BeTrue("System.String members ship with XML docs in the runtime reference assemblies");
    }

    [Fact]
    public async Task GetTypeMembers_EmptyTypeName_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_type_members", new
        {
            typeName = ""
        }, codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("typeName");
    }

    [Fact]
    public async Task GetTypeMembers_UnknownType_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_type_members", new
        {
            typeName = "DoesNotExist_12345"
        }, codeContains: ErrorCodes.TypeNotFound);
        error["message"]!.Value<string>()!.Should().Contain("DoesNotExist_12345");
    }

    [Fact]
    public async Task GetTypeMembers_IncludeInheritedTrue_OnFixtureSquare_IncludesRectangleMembers()
    {
        // FixtureSquare adds Side; FixtureRectangle (base) adds Width/Height/Area.
        var data = await CallAndGetDataAsync("roslyn:get_type_members", new
        {
            typeName = "FixtureSquare",
            includeInherited = true
        });
        var names = (data["members"] as JArray)!
            .Select(m => m["name"]!.Value<string>()!)
            .ToList();
        names.Should().Contain("Side");
        names.Should().Contain("Width", "inherited from FixtureRectangle");
        names.Should().Contain("Height");
    }

    [Fact]
    public async Task GetTypeMembers_MemberKindMethod_FiltersToMethodsOnly()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members", new
        {
            typeName = "RoslynService",
            memberKind = "Method"
        });
        var members = (data["members"] as JArray)!;
        members.Should().NotBeEmpty();
        foreach (var m in members)
        {
            m["kind"]!.Value<string>().Should().Be("Method");
        }
    }

    [Fact]
    public async Task GetMethodSignature_UnknownType_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_method_signature", new
        {
            typeName = "DoesNotExist_12345",
            methodName = "DoesNotMatter"
        }, codeContains: ErrorCodes.TypeNotFound);
        error["message"]!.Value<string>()!.Should().Contain("DoesNotExist_12345");
    }

    [Fact]
    public async Task GetMethodSignature_UnknownMethod_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_method_signature", new
        {
            typeName = "RoslynService",
            methodName = "DoesNotExist_12345"
        }, codeContains: ErrorCodes.SymbolNotFound);
        error["message"]!.Value<string>()!.Should().Contain("DoesNotExist_12345");
    }

    [Fact]
    public async Task GetMethodSignature_OverloadIndex_SelectsSpecificOverload()
    {
        // CreateErrorResponse on RoslynService has one overload; CreateSuccessResponse
        // has one too. Locking overloadCount lets a regression that mis-counts overloads
        // fail. Using overloadIndex=0 should be identical to default.
        var data = await CallAndGetDataAsync("roslyn:get_method_signature", new
        {
            typeName = "RoslynService",
            methodName = "CreateErrorResponse",
            overloadIndex = 0
        });
        data["overloadCount"]!.Value<int>().Should().BeGreaterOrEqualTo(1);
        data["selectedOverload"]!.Value<int>().Should().Be(0);
        data["name"]!.Value<string>().Should().Be("CreateErrorResponse");
    }

    [Fact]
    public async Task GetAttributes_EmptyName_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_attributes", new
        {
            attributeName = ""
        }, codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("attributeName");
    }

    [Fact]
    public async Task GetAttributes_ScopeProjectFilter_LimitsToOneProject()
    {
        // scope="project:SharpLensMcp" must drop [Fact] hits from SharpLensMcp.Tests.
        // The main project has zero [Fact] decorations, so totalFound must be 0.
        var data = await CallAndGetDataAsync("roslyn:get_attributes", new
        {
            attributeName = "Fact",
            scope = "project:SharpLensMcp"
        });
        data["totalFound"]!.Value<int>().Should().Be(0,
            "the main SharpLensMcp project has no [Fact] decorations");
    }

    [Fact]
    public async Task GetDerivedTypes_EmptyBaseTypeName_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_derived_types", new
        {
            baseTypeName = ""
        }, codeContains: ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task GetDerivedTypes_UnknownBase_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_derived_types", new
        {
            baseTypeName = "DoesNotExist_12345"
        }, codeContains: ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task GetDerivedTypes_IncludeTransitiveFalse_OnRectangle_OnlyDirectDerivations()
    {
        // FixtureRectangle has FixtureSquare as direct subclass. With
        // includeTransitive=false (Navigation.cs:538), the result still includes
        // direct subclasses. SymbolFinder.FindDerivedClassesAsync(transitive: false)
        // returns only immediate subclasses, which for FixtureRectangle is just
        // FixtureSquare anyway.
        var data = await CallAndGetDataAsync("roslyn:get_derived_types", new
        {
            baseTypeName = "FixtureRectangle",
            includeTransitive = false
        });
        data["includeTransitive"]!.Value<bool>().Should().BeFalse();
        var derived = (data["derivedTypes"] as JArray)!;
        derived.Count.Should().Be(1);
        derived[0]["name"]!.Value<string>().Should().Be("FixtureSquare");
    }

    [Fact]
    public async Task GetBaseTypes_EmptyTypeName_ReturnsInvalidParameter()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_base_types", new
        {
            typeName = ""
        }, codeContains: ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task GetBaseTypes_UnknownType_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_base_types", new
        {
            typeName = "DoesNotExist_12345"
        }, codeContains: ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task GetExternalTypeInfo_UnknownType_ReturnsExactTypeNotFoundWithFqnHint()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:get_external_type_info",
            new { typeName = "System.Garbage.NoSuchType" },
            codeContains: ErrorCodes.TypeNotFound);
        // Helper already enforced codeContains; lock the FQN hint instead of
        // the redundant silent-pass error.code re-check.
        error["hint"]!.Value<string>()!.Should().Contain("fully-qualified name",
            "the no-assembly-filter branch points the caller at the FQN+backtick-arity convention");
    }
}
