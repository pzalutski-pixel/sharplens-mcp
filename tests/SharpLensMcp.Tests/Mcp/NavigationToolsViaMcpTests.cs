using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Navigation & Discovery category (19 tools).
public class NavigationToolsViaMcpTests : McpTestBase
{
    public NavigationToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task GetSymbolInfo_OnRoslynServiceClass_ReturnsNamedType()
    {
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var classLine = Array.FindIndex(lines, l => l.Contains("class RoslynService"));
        var data = await CallAndGetDataAsync("roslyn:get_symbol_info", new
        {
            filePath = Fixture.RoslynServicePath, line = classLine, column = 20
        });
        data["kind"]?.Value<string>().Should().Be("NamedType");
        data["name"]?.Value<string>().Should().Be("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");
    }

    [Fact]
    public async Task GetSymbolInfo_AtFileTop_ReturnsToolError()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_symbol_info", new
        {
            filePath = Fixture.RoslynServicePath, line = 0, column = 0
        });
        error["code"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GoToDefinition_OnCreateSuccessResponseCall_LocatesRoslynServiceFile()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "CreateSuccessResponse", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:go_to_definition", new
        {
            filePath = file, line, column = col
        });
        data["filePath"]?.Value<string>().Should().EndWith("RoslynService.cs");
    }

    [Fact]
    public async Task FindReferences_OnCreateSuccessResponse_FindsManyCallers()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "CreateSuccessResponse", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col
        });
        var refs = data["references"] as JArray;
        refs.Should().NotBeNullOrEmpty();
        data["totalReferences"]?.Value<int>().Should().BeGreaterThan(1,
            "CreateSuccessResponse is called from many tool methods");
    }

    [Fact]
    public async Task FindReferences_WithKindFilterWrite_ReturnsOnlyWrites()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "TrackedField", kind: "Field");
        var data = await CallAndGetDataAsync("roslyn:find_references", new
        {
            filePath = file, line, column = col, kind = "write"
        });
        var refs = data["references"] as JArray;
        refs.Should().NotBeNullOrEmpty();
        refs!.All(r => r["kind"]?.Value<string>() == "write")
            .Should().BeTrue("kind filter must drop every non-write reference");
        data["totalReferencesAfterFilter"].Should().NotBeNull();
    }

    [Fact]
    public async Task FindImplementations_OnIShapeFixture_FindsImplementers()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "IShapeFixture", kind: "Interface");
        var data = await CallAndGetDataAsync("roslyn:find_implementations", new
        {
            filePath = file, line, column = col
        });
        var impls = data["implementations"] as JArray;
        impls.Should().NotBeNullOrEmpty();
        var names = impls!.Select(i => i["name"]?.Value<string>()).ToList();
        names.Should().Contain(n => n!.EndsWith("FixtureCircle"));
    }

    [Fact]
    public async Task FindCallers_OnEnsureSolutionLoaded_ReturnsManyCallers()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "EnsureSolutionLoaded", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:find_callers", new
        {
            filePath = file, line, column = col
        });
        var callers = data["callers"] as JArray;
        callers.Should().NotBeNullOrEmpty(
            "EnsureSolutionLoaded is called from many tool methods");
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
        names.Should().Contain(n => n.EndsWith(".ChainC()"),
            "depth:2 from ChainA must reach the leaf ChainC");
    }

    [Fact]
    public async Task GetCallGraph_DepthOutOfRange_ReturnsToolError()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "ChainA", kind: "Method");
        var error = await CallAndGetErrorAsync("roslyn:get_call_graph", new
        {
            filePath = file, line, column = col, maxDepth = 100
        });
        error["code"]?.Value<string>().Should().Contain("INVALID");
    }

    [Fact]
    public async Task GetTypeHierarchy_OnFixtureSquare_IncludesFixtureRectangle()
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
            .Should().BeTrue();
    }

    [Fact]
    public async Task SearchSymbols_FindsRoslynService()
    {
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "RoslynService", maxResults = 10
        });
        var results = data["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
        results!.Any(r => r["name"]?.Value<string>() == "RoslynService").Should().BeTrue();
    }

    [Fact]
    public async Task SearchSymbols_GlobPattern_FindsMatches()
    {
        var data = await CallAndGetDataAsync("roslyn:search_symbols", new
        {
            query = "*Async", kind = "Method", maxResults = 100
        });
        var results = data["results"] as JArray;
        results!.Count.Should().BeGreaterThan(10, "many async methods exist in the solution");
    }

    [Fact]
    public async Task SemanticQuery_AsyncFilter_ReturnsAsyncMethods()
    {
        var data = await CallAndGetDataAsync("roslyn:semantic_query", new
        {
            isAsync = true,
            maxResults = 50
        });
        var results = data["results"] as JArray;
        results!.Count.Should().BeGreaterThan(5);
    }

    [Fact]
    public async Task GetTypeMembers_OnRoslynService_ReturnsLargeMemberSet()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members", new
        {
            typeName = "RoslynService"
        });
        data["totalMembers"]?.Value<int>().Should().BeGreaterThan(50);
    }

    [Fact]
    public async Task GetTypeMembersBatch_ReturnsBothRequested()
    {
        var data = await CallAndGetDataAsync("roslyn:get_type_members_batch", new
        {
            typeNames = new[] { "RoslynService", "McpServer" }
        });
        var results = data["results"] as JArray;
        results!.Count.Should().Be(2);
    }

    [Fact]
    public async Task GetMethodSignature_ReturnsParametersAndReturnType()
    {
        var data = await CallAndGetDataAsync("roslyn:get_method_signature", new
        {
            typeName = "RoslynService",
            methodName = "LoadSolutionAsync"
        });
        data["name"]?.Value<string>().Should().Be("LoadSolutionAsync");
        data["returnType"]?.Value<string>().Should().Contain("Task");
        data["parameters"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetDerivedTypes_OnFixtureRectangle_FindsFixtureSquare()
    {
        var data = await CallAndGetDataAsync("roslyn:get_derived_types", new
        {
            baseTypeName = "FixtureRectangle",
            includeTransitive = true
        });
        var derived = data["derivedTypes"] as JArray;
        derived.Should().NotBeNullOrEmpty();
        derived!.Any(d => d["name"]?.Value<string>()?.EndsWith("FixtureSquare") == true)
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetBaseTypes_OnFixtureSquare_ReturnsRectangleAncestor()
    {
        var data = await CallAndGetDataAsync("roslyn:get_base_types", new
        {
            typeName = "FixtureSquare"
        });
        var baseTypes = data["baseTypes"] as JArray;
        baseTypes.Should().NotBeNullOrEmpty();
        baseTypes!.Any(b => b["name"]?.Value<string>()?.EndsWith("FixtureRectangle") == true)
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetAttributes_FactAttribute_FindsXunitTests()
    {
        var data = await CallAndGetDataAsync("roslyn:get_attributes", new
        {
            attributeName = "Fact"
        });
        var symbols = data["symbols"] as JArray;
        symbols.Should().NotBeNullOrEmpty(
            "the test project has many [Fact]-decorated methods");
    }

    [Fact]
    public async Task GetContainingMember_AtKnownMethodBody_ReturnsMethodName()
    {
        // Line 50 of RoslynService.cs is inside the MatchesGlobPattern method body.
        var data = await CallAndGetDataAsync("roslyn:get_containing_member", new
        {
            filePath = Fixture.RoslynServicePath, line = 50, column = 10
        });
        data["memberName"]?.Value<string>().Should().Be("MatchesGlobPattern");
    }

    [Fact]
    public async Task GetMethodOverloads_OnCreateErrorResponse_FindsOverloads()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "CreateErrorResponse", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_method_overloads", new
        {
            filePath = file, line, column = col
        });
        var overloads = data["overloads"] as JArray;
        overloads.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task FindAttributeUsages_FactAttribute_FindsMembers()
    {
        var data = await CallAndGetDataAsync("roslyn:find_attribute_usages", new
        {
            attributeName = "Fact",
            maxResults = 50
        });
        data["usages"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetExternalTypeInfo_OnSystemString_ReturnsCoreMembers()
    {
        var data = await CallAndGetDataAsync("roslyn:get_external_type_info", new
        {
            typeName = "System.String"
        });
        data["typeName"]?.Value<string>().Should().Be("string");
        data["typeKind"]?.Value<string>().Should().Be("Class");
        var members = data["members"] as JArray;
        var names = members!.Select(m => m["name"]?.Value<string>()).ToList();
        names.Should().Contain("Length");
        names.Should().Contain("Substring");
    }

    [Fact]
    public async Task GetExternalTypeInfo_UnknownType_ReturnsTypeNotFound()
    {
        var error = await CallAndGetErrorAsync("roslyn:get_external_type_info", new
        {
            typeName = "System.Garbage.NoSuchType"
        }, codeContains: "TYPE_NOT_FOUND");
        error["code"]?.Value<string>().Should().Contain("TYPE_NOT_FOUND");
    }
}
