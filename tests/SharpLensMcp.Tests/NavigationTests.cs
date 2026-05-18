using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for navigation tools: get_symbol_info, go_to_definition, find_references, etc.
/// </summary>
public class NavigationTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetSymbolInfo_OnClassName_ReturnsTypeInfo()
    {
        var lines = File.ReadAllLines(RoslynServicePath);
        var classLine = Array.FindIndex(lines, l => l.Contains("class RoslynService"));
        classLine.Should().BeGreaterThan(0, "the partial class declaration must exist in RoslynService.cs");

        var result = await Service.GetSymbolInfoAsync(RoslynServicePath, line: classLine, column: 20);

        AssertSuccess(result);
        var data = GetData(result);
        data["kind"]?.Value<string>().Should().Be("NamedType");
        data["name"]?.Value<string>().Should().Be("RoslynService");
        data["typeKind"]?.Value<string>().Should().Be("Class");
    }

    [Fact]
    public async Task GetSymbolInfo_InvalidPosition_ReturnsSymbolNotFound()
    {
        // Position (0, 0) — beginning of file, no symbol there.
        var result = await Service.GetSymbolInfoAsync(RoslynServicePath, line: 0, column: 0);
        AssertError(result, ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task GoToDefinition_OnMethod_ReturnsLocation()
    {
        var searchResult = await Service.SearchSymbolsAsync("CreateSuccessResponse", kind: null, maxResults: 10);
        AssertSuccess(searchResult);

        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty("CreateSuccessResponse exists in the loaded solution");

        var symbol = symbols![0];
        var loc = symbol["location"];
        loc.Should().NotBeNull();
        var file = loc!["filePath"]!.Value<string>()!;
        var line = loc["line"]!.Value<int>();
        var col = loc["column"]!.Value<int>();

        var result = await Service.GoToDefinitionAsync(file, line, col);
        AssertSuccess(result);
        var data = GetData(result);
        data["filePath"]?.Value<string>().Should().EndWith("RoslynService.cs",
            "CreateSuccessResponse is defined in RoslynService.cs");
        data["line"]?.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SearchSymbols_FindsTypes()
    {
        // Act
        var result = await Service.SearchSymbolsAsync("RoslynService", kind: null, maxResults: 10);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().BeGreaterOrEqualTo(1);

        // Should find RoslynService class or fields containing it
        var hasMatch = results.Any(r =>
            r["name"]?.Value<string>()?.Contains("RoslynService") == true ||
            r["fullyQualifiedName"]?.Value<string>()?.Contains("RoslynService") == true);
        hasMatch.Should().BeTrue("Search should find symbols containing 'RoslynService'");
    }

    [Fact]
    public async Task SearchSymbols_WithKindFilter_FiltersResults()
    {
        var result = await Service.SearchSymbolsAsync("Get*", kind: "Method", maxResults: 50);

        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNullOrEmpty(
            "the solution has many Get*-named methods; an empty result would silently bypass the foreach below");

        foreach (var symbol in results!)
        {
            symbol["kind"]?.Value<string>().Should().Be("Method");
        }
    }

    [Fact]
    public async Task SearchSymbols_WithWildcard_FindsMultiple()
    {
        // Act
        var result = await Service.SearchSymbolsAsync("*Async", kind: "Method", maxResults: 100);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().BeGreaterThan(10, "Should find many async methods");
    }

    [Fact]
    public async Task FindReferences_OnPublicMethod_FindsUsages()
    {
        var searchResult = await Service.SearchSymbolsAsync("CreateSuccessResponse", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty("CreateSuccessResponse exists in the loaded solution");

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.FindReferencesAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        var references = data["references"] as JArray;
        references.Should().NotBeNullOrEmpty();
        references!.Count.Should().BeGreaterThan(1,
            "CreateSuccessResponse is called from multiple tool methods");
        data["totalReferences"]?.Value<int>().Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task GetTypeMembers_ReturnsAllMembers()
    {
        // Act
        var result = await Service.GetTypeMembersAsync("RoslynService");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"]?.Value<string>().Should().Contain("RoslynService");
        data["totalMembers"]?.Value<int>().Should().BeGreaterThan(50, "RoslynService has many methods");
    }

    [Fact]
    public async Task GetTypeMembers_WithKindFilter_FiltersResults()
    {
        var result = await Service.GetTypeMembersAsync("RoslynService", memberKind: "Method");

        AssertSuccess(result);
        var data = GetData(result);
        var members = data["members"] as JArray;
        members.Should().NotBeNullOrEmpty(
            "RoslynService has many methods; an empty members array would silently pass the foreach below");

        foreach (var member in members!)
        {
            member["kind"]?.Value<string>().Should().Be("Method");
        }
    }

    [Fact]
    public async Task GetMethodSignature_ReturnsDetails()
    {
        var result = await Service.GetMethodSignatureAsync("RoslynService", "LoadSolutionAsync");

        AssertSuccess(result);
        var data = GetData(result);
        // The impl emits `name` (TypeDiscovery.cs:346), not `methodName`. The previous
        // `data["methodName"]?.Value<string>().Should().Be("X")` silently passed because
        // C# null-conditional short-circuits the ENTIRE chain — including .Should().Be(...)
        // — when the prefix is null. Lock the actual field name to defeat that pattern.
        data["name"].Should().NotBeNull("response must include the method name");
        data["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        data["returnType"]?.Value<string>().Should().Contain("Task<object>");
        var parameters = data["parameters"] as JArray;
        parameters.Should().NotBeNullOrEmpty();
        parameters!.Count.Should().Be(1);
        parameters[0]["name"]?.Value<string>().Should().Be("solutionPath");
    }

    [Fact]
    public async Task GetDerivedTypes_FindsTransitiveSubclasses()
    {
        var result = await Service.GetDerivedTypesAsync("FixtureRectangle");

        AssertSuccess(result);
        var data = GetData(result);
        data["baseType"]?.Value<string>().Should().EndWith("FixtureRectangle");

        var derived = data["derivedTypes"] as JArray;
        derived.Should().NotBeNullOrEmpty("FixtureSquare derives from FixtureRectangle");
        derived!.Any(d => d["name"]?.Value<string>()?.EndsWith("FixtureSquare") == true)
            .Should().BeTrue("FixtureSquare must appear in transitive derived types");
    }

    [Fact]
    public async Task GetBaseTypes_ReturnsInheritanceChain()
    {
        // FixtureSquare : FixtureRectangle : object. Chain stops before object.
        var result = await Service.GetBaseTypesAsync("FixtureSquare");

        AssertSuccess(result);
        var data = GetData(result);
        var baseTypes = data["baseTypes"] as JArray;
        baseTypes.Should().NotBeNullOrEmpty();
        baseTypes!.Any(b => b["name"]?.Value<string>()?.EndsWith("FixtureRectangle") == true)
            .Should().BeTrue("FixtureSquare's immediate base must be FixtureRectangle");
    }

    [Fact]
    public async Task FindCallers_OnEnsureSolutionLoaded_FindsManySpecificCallers()
    {
        var searchResult = await Service.SearchSymbolsAsync("EnsureSolutionLoaded", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.FindCallersAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        var callers = data["callers"] as JArray;
        callers.Should().NotBeNullOrEmpty();
        callers!.Count.Should().BeGreaterOrEqualTo(20,
            "EnsureSolutionLoaded is called from dozens of tool methods");
        var names = callers
            .Select(c => c["callingSymbol"]?["name"]?.Value<string>())
            .ToList();
        names.Should().Contain("SearchSymbolsAsync");
        names.Should().Contain("GetDiagnosticsAsync");
    }

    [Fact]
    public async Task SemanticQuery_FindsAsyncMethods()
    {
        var result = await Service.SemanticQueryAsync(
            kinds: null,
            isAsync: true,
            namespaceFilter: null,
            accessibility: null,
            isStatic: null,
            type: null,
            returnType: null,
            attributes: null,
            parameterIncludes: null,
            parameterExcludes: null,
            maxResults: 50);

        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNullOrEmpty();
        results!.Count.Should().BeGreaterThan(5, "Should find async symbols");
        // Filter invariant: every returned symbol must satisfy the isAsync filter.
        foreach (var symbol in results)
        {
            symbol["isAsync"]?.Value<bool>().Should().BeTrue(
                "isAsync:true must drop non-async symbols");
        }
    }

    [Fact]
    public async Task SemanticQuery_FiltersByAccessibility()
    {
        var result = await Service.SemanticQueryAsync(
            kinds: new List<string> { "Method" },
            isAsync: null,
            namespaceFilter: null,
            accessibility: "Public",
            isStatic: null,
            type: null,
            returnType: null,
            attributes: null,
            parameterIncludes: null,
            parameterExcludes: null,
            maxResults: 50);

        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNullOrEmpty(
            "the solution has many public methods; an empty result would silently pass the foreach");

        foreach (var symbol in results!)
        {
            symbol["accessibility"]?.Value<string>().Should().Be("Public");
        }
    }

    [Fact]
    public async Task GetSymbolInfo_OnMethodName_ReturnsMethodSymbol()
    {
        // Locate LoadSolutionAsync via search, then point at its name.
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();
        var loc = symbols![0]["location"]!;

        var result = await Service.GetSymbolInfoAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());

        AssertSuccess(result);
        var data = GetData(result);
        // Kind for methods is "Method", not "NamedType".
        data["kind"]?.Value<string>().Should().Be("Method");
        data["name"]?.Value<string>().Should().Be("LoadSolutionAsync");
        data["containingType"]?.Value<string>().Should().Contain("RoslynService");
    }

    [Fact]
    public async Task GetTypeMembers_OnNonExistentType_ReturnsTypeNotFound()
    {
        var result = await Service.GetTypeMembersAsync("DoesNotExist_XYZ_12345");
        AssertError(result, ErrorCodes.TypeNotFound);
    }

    [Fact]
    public async Task GetMethodSignature_OnNonExistentMethod_ReturnsSymbolNotFound()
    {
        var result = await Service.GetMethodSignatureAsync("RoslynService", "DoesNotExistMethod_XYZ");
        AssertError(result, ErrorCodes.SymbolNotFound);
    }

    [Fact]
    public async Task GetMethodSignature_OnOverloadedMethod_ReportsOverloadCount()
    {
        // System.String.Substring has two overloads: (int startIndex) and
        // (int startIndex, int length). The response's overloadCount field
        // (TypeDiscovery.cs:359) must reflect that.
        var result = await Service.GetMethodSignatureAsync("System.String", "Substring");

        AssertSuccess(result);
        var data = GetData(result);
        // Note: the impl emits `name`, not `methodName` (TypeDiscovery.cs:346).
        data["name"]?.Value<string>().Should().Be("Substring");
        data["overloadCount"]?.Value<int>().Should().Be(2,
            "String.Substring has exactly 2 overloads in the BCL surface");
        // selectedOverload defaults to 0 — the first overload is returned.
        data["selectedOverload"]?.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetDerivedTypes_OnSealedType_ReturnsEmpty()
    {
        // FixtureSquare doesn't derive any further (no FixtureSquareSubclass).
        var result = await Service.GetDerivedTypesAsync("FixtureSquare");

        AssertSuccess(result);
        var data = GetData(result);
        var derived = data["derivedTypes"] as JArray;
        derived.Should().NotBeNull();
        derived!.Count.Should().Be(0,
            "FixtureSquare has no further-derived subclasses in the solution");
    }

    [Fact]
    public async Task SearchSymbols_WithOffset_ReturnsPage2NonOverlapping()
    {
        var page1 = await Service.SearchSymbolsAsync("*Async", kind: "Method", maxResults: 5, offset: 0);
        var p1 = GetData(page1);
        p1["offset"]?.Value<int>().Should().Be(0);
        p1["hasMore"]?.Value<bool>().Should().BeTrue(
            "the solution has dozens of *Async methods; 5 doesn't exhaust them");

        var nextOffset = p1["pagination"]!["nextOffset"]!.Value<int>();
        nextOffset.Should().Be(5);

        var page2 = await Service.SearchSymbolsAsync("*Async", kind: "Method", maxResults: 5, offset: nextOffset);
        var p2 = GetData(page2);
        p2["offset"]?.Value<int>().Should().Be(5);

        // Page 2 results must not overlap with page 1.
        var p1Names = (p1["results"] as JArray)!.Select(r => r["fullyQualifiedName"]?.Value<string>()).ToList();
        var p2Names = (p2["results"] as JArray)!.Select(r => r["fullyQualifiedName"]?.Value<string>()).ToList();
        p1Names.Intersect(p2Names).Should().BeEmpty(
            "page 2 must not duplicate page 1 entries");
    }

    [Fact]
    public async Task FindReferences_WithMaxResults_CapsListButTotalReportsTrueCount()
    {
        // EnsureSolutionLoaded has 20+ callers (asserted in
        // FindCallers_OnEnsureSolutionLoaded_FindsManySpecificCallers above).
        // With maxResults=3, references list caps at 3, totalReferences stays accurate.
        var searchResult = await Service.SearchSymbolsAsync("EnsureSolutionLoaded", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var loc = symbols![0]["location"]!;

        var result = await Service.FindReferencesAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            maxResults: 3);

        AssertSuccess(result);
        var data = GetData(result);
        var references = data["references"] as JArray;
        references!.Count.Should().Be(3,
            "maxResults=3 must fill the cap exactly when more references exist");
        data["totalReferences"]?.Value<int>().Should().BeGreaterThan(3,
            "totalReferences must reflect the true count, not the capped count");
    }

    [Fact]
    public async Task SemanticQuery_WithCombinedFiltersIsStaticTrueAndPublic_AllResultsMatchBoth()
    {
        // Combined filter test: isStatic=true AND accessibility=Public AND kind=Method.
        // Every returned symbol must satisfy ALL three constraints. The
        // semantic_query API combines filters with AND, never OR.
        var result = await Service.SemanticQueryAsync(
            kinds: new List<string> { "Method" },
            isAsync: null,
            namespaceFilter: null,
            accessibility: "Public",
            isStatic: true,
            type: null,
            returnType: null,
            attributes: null,
            parameterIncludes: null,
            parameterExcludes: null,
            maxResults: 50);

        AssertSuccess(result);
        var results = (GetData(result)["results"] as JArray)!;
        results.Should().NotBeNullOrEmpty(
            "the solution has public static methods (e.g., MatchesGlobPattern is private static — but ErrorCodes constants and others provide public statics)");

        foreach (var symbol in results)
        {
            symbol["accessibility"]?.Value<string>().Should().Be("Public",
                "accessibility=Public must be satisfied for every returned symbol");
            symbol["isStatic"]?.Value<bool>().Should().BeTrue(
                "isStatic=true must be satisfied for every returned symbol");
        }
    }

    [Fact]
    public async Task SemanticQuery_WithParameterIncludesFilter_OnlyMatchesMethodsWithMatchingParam()
    {
        // parameterIncludes filter: every returned method must have a parameter
        // whose type contains the search string.
        var result = await Service.SemanticQueryAsync(
            kinds: new List<string> { "Method" },
            isAsync: null,
            namespaceFilter: null,
            accessibility: null,
            isStatic: null,
            type: null,
            returnType: null,
            attributes: null,
            parameterIncludes: new List<string> { "string" },
            parameterExcludes: null,
            maxResults: 50);

        AssertSuccess(result);
        var results = (GetData(result)["results"] as JArray)!;
        results.Should().NotBeNullOrEmpty(
            "many methods take string parameters (filePath, typeName, etc.)");

        // Every returned method must have at least one string-typed parameter.
        foreach (var symbol in results)
        {
            var parameters = symbol["parameters"] as JArray;
            parameters.Should().NotBeNullOrEmpty(
                "a method that matched parameterIncludes='string' must have parameters");
            parameters!.Any(p => p["type"]?.Value<string>()?.Contains("string", StringComparison.OrdinalIgnoreCase) == true)
                .Should().BeTrue(
                    "every returned method must have at least one string-typed parameter");
        }
    }
}
