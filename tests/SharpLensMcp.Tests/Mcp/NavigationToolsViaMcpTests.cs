using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Navigation & Discovery category (19 tools).
// Each assertion is grounded in specific known facts about the loaded
// SharpLens solution + the engineered fixtures.
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
        data["kind"]?.Value<string>().Should().Be("NamedType");
        data["name"]?.Value<string>().Should().Be("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        data["namespace"]?.Value<string>().Should().Be("SharpLensMcp",
            "the partial class is in the SharpLensMcp namespace");
    }

    [Fact]
    public async Task GetSymbolInfo_AtFileTop_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:get_symbol_info",
            new { filePath = Fixture.RoslynServicePath, line = 0, column = 0 },
            codeContains: ErrorCodes.SymbolNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task GoToDefinition_OnCreateSuccessResponseCall_LocatesDeclaration()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "CreateSuccessResponse", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:go_to_definition", new
        {
            filePath = file, line, column = col
        });
        data["filePath"]?.Value<string>().Should().EndWith("RoslynService.cs");
        data["line"]?.Value<int>().Should().BeGreaterThan(0,
            "the declaration is well past the file header");

        var symbol = data["symbol"]!;
        symbol["name"]?.Value<string>().Should().Be("CreateSuccessResponse");
        symbol["kind"]?.Value<string>().Should().Be("Method");
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
        data["symbolName"]?.Value<string>().Should().Be("CreateSuccessResponse");
        data["symbolKind"]?.Value<string>().Should().Be("Method");

        var refs = data["references"] as JArray;
        refs.Should().NotBeNullOrEmpty();
        data["totalReferences"]?.Value<int>().Should().BeGreaterOrEqualTo(2);

        // Every reference must conform to the documented shape.
        foreach (var r in refs!)
        {
            r["filePath"]?.Value<string>().Should().EndWith(".cs");
            r["line"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
            r["column"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
            r["kind"]?.Value<string>().Should().NotBeNullOrEmpty();
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
        var refs = data["references"] as JArray;
        refs.Should().NotBeNullOrEmpty(
            "the fixture has at least one write site on TrackedField");
        refs!.All(r => r["kind"]?.Value<string>() == "write")
            .Should().BeTrue("the kind filter must drop every non-write reference");
        data["kindFilter"]?.Value<string>().Should().Be("write");
        data["totalReferencesAfterFilter"]?.Value<int>().Should().BeLessThan(totalUnfiltered,
            "the unfiltered total has reads/invocations/typeof in addition to writes");
        data["totalReferences"]?.Value<int>().Should().Be(totalUnfiltered);
    }

    [Fact]
    public async Task FindReferences_KindFilterCast_OnTrackedTarget_ReturnsAtLeastOneCast()
    {
        var (file, line, col) = await LocateSymbolAsync("TrackedTarget", kind: "Class");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "cast"
        });
        var refs = data["references"] as JArray;
        refs.Should().NotBeNullOrEmpty(
            "ReferenceKindsFixture has `(TrackedTarget)boxed` — at least one cast ref");
        refs!.All(r => r["kind"]?.Value<string>() == "cast").Should().BeTrue();
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
        var impls = data["implementations"] as JArray;
        impls.Should().NotBeNullOrEmpty();
        // find_implementations returns fully-qualified names per Navigation.cs:461.
        var names = impls!.Select(i => i["name"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".FixtureCircle"));
        names.Should().Contain(n => n.EndsWith(".FixtureRectangle"));
        names.Should().Contain(n => n.EndsWith(".FixtureSquare"),
            "FixtureSquare derives from FixtureRectangle, so it transitively implements IShapeFixture");

        foreach (var i in impls!)
        {
            i["kind"]?.Value<string>().Should().Be("Class");
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
        var callers = data["callers"] as JArray;
        callers.Should().NotBeNullOrEmpty();
        // EnsureSolutionLoaded is invoked from virtually every public tool method
        // EXCEPT LoadSolutionAsync (which is the one that loads the solution).
        callers!.Count.Should().BeGreaterOrEqualTo(20,
            "almost every tool method calls EnsureSolutionLoaded at the top");

        var callerNames = callers
            .Select(c => c["callingSymbol"]?["name"]?.Value<string>() ?? "")
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
        var nodes = data["nodes"] as JArray;
        nodes.Should().NotBeNullOrEmpty();
        var names = nodes!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainA()"));
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().Contain(n => n.EndsWith(".ChainC()"),
            "depth:2 from ChainA must reach the leaf ChainC");

        // root must be ChainA.
        data["root"]!["fullName"]?.Value<string>().Should().EndWith(".ChainA()");
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
        var names = (data["nodes"] as JArray)!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().NotContain(n => n.EndsWith(".ChainC()"),
            "depth:1 must stop at ChainB");
        data["truncatedByDepth"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task GetCallGraph_DepthOutOfRange_ReturnsInvalidParameter()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "ChainA", kind: "Method");
        var error = await CallAndGetErrorAsync(
            "roslyn:get_call_graph",
            new { filePath = file, line, column = col, maxDepth = 100 },
            codeContains: ErrorCodes.InvalidParameter);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
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
        var baseTypes = data["baseTypes"] as JArray;
        baseTypes.Should().NotBeNullOrEmpty();
        baseTypes!.Any(b => b["name"]?.Value<string>()?.EndsWith("FixtureRectangle") == true)
            .Should().BeTrue("FixtureSquare extends FixtureRectangle");

        // Transitive interface via FixtureRectangle → IShapeFixture.
        var interfaces = data["interfaces"] as JArray;
        interfaces.Should().NotBeNull();
        interfaces!.Any(i => i["name"]?.Value<string>()?.EndsWith("IShapeFixture") == true)
            .Should().BeTrue("FixtureSquare transitively implements IShapeFixture");
    }

    [Fact]
    public async Task SearchSymbols_OnRoslynService_FindsClassEntry()
    {
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "RoslynService", maxResults = 10
        });
        var results = data["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
        var classEntry = results!.FirstOrDefault(r =>
            r["name"]?.Value<string>() == "RoslynService"
            && r["kind"]?.Value<string>() == "NamedType");
        classEntry.Should().NotBeNull("the class itself must appear among results");
        // RoslynService is a partial class spread across many files; Roslyn returns
        // ONE partial as the canonical declaration. Any of them is acceptable as
        // long as the filename contains "RoslynService" and is a .cs file.
        var path = classEntry!["location"]?["filePath"]?.Value<string>()!;
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
        page1["offset"]?.Value<int>().Should().Be(0);
        page1["hasMore"]?.Value<bool>().Should().BeTrue(
            "the solution has dozens of *Async methods; 5 doesn't exhaust them");
        var nextOffset = page1["pagination"]!["nextOffset"]!.Value<int>();
        nextOffset.Should().Be(5);

        // Page 2 must report offset==nextOffset and return a different page of results.
        var page2 = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "*Async", kind = "Method", maxResults = 5, offset = nextOffset
        });
        page2["offset"]?.Value<int>().Should().Be(5);
        var page1Names = (page1["results"] as JArray)!.Select(r => r["fullyQualifiedName"]?.Value<string>()).ToList();
        var page2Names = (page2["results"] as JArray)!.Select(r => r["fullyQualifiedName"]?.Value<string>()).ToList();
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
        var names = (data["nodes"] as JArray)!.Select(n => n["fullName"]?.Value<string>() ?? "").ToList();
        names.Should().Contain(n => n.EndsWith(".ChainB()"));
        names.Should().Contain(n => n.EndsWith(".ChainA()"),
            "direction=both must include ChainA (the caller of ChainB)");
        names.Should().Contain(n => n.EndsWith(".ChainC()"),
            "direction=both must include ChainC (the callee of ChainB)");
        data["direction"]?.Value<string>().Should().Be("both");
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
        filtered["assembly"]?.Value<string>().Should().Be(actualAssembly);
        filtered["typeName"]?.Value<string>().Should().Be("string");
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
        error["code"]?.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task GetExternalTypeInfo_IncludeInheritedFalse_ExcludesBaseTypeMembers()
    {
        // FixtureSquare extends FixtureRectangle (which has Width, Height, Area).
        // FixtureSquare's own members: Side. With includeInherited=true we get all four;
        // with includeInherited=false we get only Side.
        // (System.String / List<T> can't be used because their immediate base IS
        // System.Object which the impl excludes from the walk per ExternalApi.cs:78.)
        var included = await CallAndGetDataAsync("roslyn:get_external_type_info", new
        {
            typeName = "SharpLensMcp.Tests.Fixtures.FixtureSquare",
            includeInherited = true,
            maxMembers = 50
        });
        var includedNames = (included["members"] as JArray)!
            .Select(m => m["name"]?.Value<string>())
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
            .Select(m => m["name"]?.Value<string>())
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
        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().BeGreaterThan(10);
        foreach (var r in results)
        {
            r["kind"]?.Value<string>().Should().Be("Method");
            r["name"]?.Value<string>().Should().EndWith("Async",
                "the *Async glob must filter to names ending in Async");
        }
    }

    [Fact]
    public async Task SemanticQuery_AsyncFilter_EveryResultIsAsync()
    {
        var data = await CallAndGetDataAsync("roslyn:semantic_query", new
        {
            isAsync = true,
            maxResults = 100
        });
        var results = data["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
        results!.Count.Should().BeGreaterThan(5,
            "the solution has many async methods");
        // Filter invariant: every returned symbol must have isAsync=true.
        foreach (var r in results)
        {
            r["isAsync"]?.Value<bool>().Should().BeTrue(
                "every result must have isAsync=true after filtering by isAsync:true");
        }
    }

    [Fact]
    public async Task GetTypeMembers_OnRoslynService_IncludesLoadSolutionAndWorkspace()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members", new
        {
            typeName = "RoslynService"
        });
        data["totalMembers"]?.Value<int>().Should().BeGreaterThan(50);

        var members = (data["members"] as JArray)!
            .Select(m => new { name = m["name"]?.Value<string>(), kind = m["kind"]?.Value<string>() })
            .ToList();
        members.Should().Contain(m => m.name == "LoadSolutionAsync" && m.kind == "Method");
        members.Should().Contain(m => m.name == "_workspace" && m.kind == "Field");
    }

    [Fact]
    public async Task GetTypeMembersBatch_ReturnsTwoNamedResults()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members_batch", new
        {
            typeNames = new[] { "RoslynService", "McpServer" }
        });
        var results = data["results"] as JArray;
        results!.Count.Should().Be(2);
        data["successCount"]?.Value<int>().Should().Be(2);
        var typeNames = results.Select(r => r["typeName"]?.Value<string>()).ToList();
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
        data["name"]?.Value<string>().Should().Be("LoadSolutionAsync");
        data["returnType"]?.Value<string>().Should().Contain("Task<object>");
        data["isAsync"]?.Value<bool>().Should().BeTrue();
        data["accessibility"]?.Value<string>().Should().Be("Public");

        var parameters = data["parameters"] as JArray;
        parameters!.Count.Should().Be(1);
        parameters[0]["name"]?.Value<string>().Should().Be("solutionPath");
        parameters[0]["type"]?.Value<string>().Should().Be("string");
    }

    [Fact]
    public async Task GetDerivedTypes_OnFixtureRectangle_ReturnsExactlyFixtureSquare()
    {
        var data = await CallAndGetDataAsync("roslyn:get_derived_types", new
        {
            baseTypeName = "FixtureRectangle",
            includeTransitive = true
        });
        var derived = data["derivedTypes"] as JArray;
        derived.Should().NotBeNullOrEmpty();
        derived!.Count.Should().Be(1, "FixtureSquare is the only derived class in the fixture");
        derived[0]["name"]?.Value<string>().Should().Be("FixtureSquare");
        data["totalDerived"]?.Value<int>().Should().Be(1);
    }

    [Fact]
    public async Task GetBaseTypes_OnFixtureSquare_ReturnsRectangleAsImmediateBase()
    {
        var data = await CallAndGetDataAsync("roslyn:get_base_types", new
        {
            typeName = "FixtureSquare"
        });
        data["typeName"]?.Value<string>().Should().EndWith("FixtureSquare");
        var baseTypes = data["baseTypes"] as JArray;
        baseTypes.Should().NotBeNullOrEmpty();
        baseTypes![0]["name"]?.Value<string>().Should().Be("FixtureRectangle",
            "the immediate base of FixtureSquare is FixtureRectangle");

        var interfaces = data["interfaces"] as JArray;
        interfaces.Should().NotBeNullOrEmpty();
        interfaces!.Any(i => i["name"]?.Value<string>() == "IShapeFixture")
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetAttributes_FactAttribute_FindsManyXunitFactMembers()
    {
        var data = await CallAndGetDataAsync("roslyn:get_attributes", new
        {
            attributeName = "Fact"
        });
        var symbols = data["symbols"] as JArray;
        symbols.Should().NotBeNullOrEmpty();
        data["totalFound"]?.Value<int>().Should().BeGreaterThan(100,
            "the test project has well over 100 [Fact]-decorated methods");

        // Every match's attribute.name must be FactAttribute (not some other "Fact"-named attr).
        foreach (var s in symbols!)
        {
            s["attribute"]?["name"]?.Value<string>().Should().Be("FactAttribute");
        }
    }

    [Fact]
    public async Task GetContainingMember_AtMatchesGlobPatternBody_ReturnsThatMethodName()
    {
        // Find MatchesGlobPattern's body dynamically — the line number shifts with edits.
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var methodLine = Array.FindIndex(lines, l => l.Contains("MatchesGlobPattern"));
        methodLine.Should().BeGreaterThan(-1);
        // Cursor in the body, a few lines below the signature.
        var bodyLine = methodLine + 4;

        var data = await CallAndGetDataAsync("roslyn:get_containing_member", new
        {
            filePath = Fixture.RoslynServicePath, line = bodyLine, column = 10
        });
        data["memberName"]?.Value<string>().Should().Be("MatchesGlobPattern");
        data["memberKind"]?.Value<string>().Should().Be("Method");
        data["containingType"]?.Value<string>().Should().Contain("RoslynService");
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
        data["methodName"]?.Value<string>().Should().Be("CreateErrorResponse");
        var overloads = data["overloads"] as JArray;
        overloads.Should().NotBeNullOrEmpty();
        foreach (var o in overloads!)
        {
            // Every overload's signature must include CreateErrorResponse identifier.
            o["signature"]?.Value<string>().Should().Contain("CreateErrorResponse");
            o["returnType"]?.Value<string>().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task FindAttributeUsages_FactAttribute_EveryEntryIsFactDecoratedMethod()
    {
        var data = await CallAndGetDataAsync("roslyn:find_attribute_usages", new
        {
            attributeName = "Fact",
            maxResults = 200
        });
        var usages = data["usages"] as JArray;
        usages.Should().NotBeNullOrEmpty();
        data["totalCount"]?.Value<int>().Should().BeGreaterThan(100,
            "the test project has well over 100 [Fact]-decorated methods");

        foreach (var u in usages!)
        {
            u["attributeName"]?.Value<string>().Should().Be("FactAttribute");
            u["symbolKind"]?.Value<string>().Should().Be("Method",
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
        data["typeName"]?.Value<string>().Should().Be("string");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        data["assembly"]?.Value<string>().Should().NotBeNullOrEmpty(
            "System.String must report its containing assembly");

        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty();
        var names = members!.Select(m => m["name"]?.Value<string>()).ToList();
        names.Should().Contain("Length");
        names.Should().Contain("Substring");
        names.Should().Contain("Trim");

        // At least one well-known member must have an XML doc summary.
        members!.Any(m => !string.IsNullOrEmpty(m["xmlDoc"]?.Value<string>()))
            .Should().BeTrue("System.String members ship with XML docs in the runtime reference assemblies");
    }

    [Fact]
    public async Task GetExternalTypeInfo_UnknownType_ReturnsExactTypeNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:get_external_type_info",
            new { typeName = "System.Garbage.NoSuchType" },
            codeContains: ErrorCodes.TypeNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.TypeNotFound);
    }
}
