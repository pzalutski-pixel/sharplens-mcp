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
        data["kind"].Should().NotBeNull();
        data["kind"]!.Value<string>().Should().Be("NamedType");
        data["name"].Should().NotBeNull();
        data["name"]!.Value<string>().Should().Be("RoslynService");
        data["typeKind"].Should().NotBeNull();
        data["typeKind"]!.Value<string>().Should().Be("Class");
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

        // Two distinct CreateSuccessResponse methods exist (RoslynService.cs and
        // McpServer.cs), so symbols[0] is non-deterministic. Pin the RoslynService
        // one so go_to_definition resolves to a known file.
        var symbol = symbols!.First(s =>
            s["location"]!["filePath"]!.Value<string>()!.EndsWith("RoslynService.cs"));
        var loc = symbol["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var line = loc["line"]!.Value<int>();
        var col = loc["column"]!.Value<int>();

        var result = await Service.GoToDefinitionAsync(file, line, col);
        AssertSuccess(result);
        var data = GetData(result);
        // go_to_definition nests the location under `definition` (Navigation.cs
        // builds data.definition.{filePath,line,...}); the old top-level
        // data["filePath"] read was always null and silently passed.
        data["definition"].Should().NotBeNull("a resolved definition must carry its location");
        var definition = data["definition"]!;
        definition["filePath"].Should().NotBeNull("CreateSuccessResponse is defined in RoslynService.cs");
        definition["filePath"]!.Value<string>().Should().EndWith("RoslynService.cs",
            "CreateSuccessResponse is defined in RoslynService.cs");
        definition["line"].Should().NotBeNull();
        definition["line"]!.Value<int>().Should().Be(line,
            "go_to_definition must resolve to the same declaration line search_symbols reported");
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
            symbol["kind"].Should().NotBeNull();
            symbol["kind"]!.Value<string>().Should().Be("Method");
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
    public async Task FindReferences_OnFixtureAnchor_ReturnsExactlyFiveInvocations()
    {
        // ReferenceCountFixture.Anchor is invoked exactly five times (CallerOne..Five),
        // so the reference total is a fixed, drift-proof 5 rather than an inequality
        // against the live, evolving RoslynService source.
        var searchResult = await Service.SearchSymbolsAsync("Anchor", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty("ReferenceCountTarget.Anchor exists in the loaded solution");

        var symbol = symbols!.First(s => s["containingType"]!.Value<string>()!.EndsWith("ReferenceCountTarget"));
        var loc = symbol["location"]!;
        var result = await Service.FindReferencesAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        var references = data["references"] as JArray;
        references.Should().NotBeNull();
        references!.Count.Should().Be(5, "Anchor is invoked five times in ReferenceCountFixture");
        data["totalReferences"].Should().NotBeNull();
        data["totalReferences"]!.Value<int>().Should().Be(5);
        foreach (var r in references)
        {
            r["kind"]!.Value<string>().Should().Be("invocation",
                "every reference to Anchor is a call");
        }
    }

    [Fact]
    public async Task GetTypeMembers_ReturnsAllMembers()
    {
        // Act
        var result = await Service.GetTypeMembersAsync("RoslynService");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["typeName"].Should().NotBeNull();
        data["typeName"]!.Value<string>().Should().Contain("RoslynService");
        data["totalMembers"].Should().NotBeNull();
        data["totalMembers"]!.Value<int>().Should().BeGreaterThan(50, "RoslynService has many methods");
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
            member["kind"].Should().NotBeNull();
            member["kind"]!.Value<string>().Should().Be("Method");
        }
    }

    [Fact]
    public async Task GetTypeMembers_WithConstructorKind_ReturnsConstructorsOnly()
    {
        // memberKind=Constructor must filter to constructors (named ".ctor"). Before
        // the fix this hit the default arm and returned every member.
        var result = await Service.GetTypeMembersAsync("RoslynService", memberKind: "Constructor");

        AssertSuccess(result);
        var members = (GetData(result)["members"] as JArray)!;
        members.Should().NotBeNullOrEmpty("RoslynService declares a constructor");
        members.Should().OnlyContain(m => m["name"]!.Value<string>() == ".ctor",
            "the Constructor filter must return only constructors");
    }

    [Fact]
    public async Task GetTypeMembers_WithUnknownKind_ReturnsInvalidParameter()
    {
        // An unknown memberKind must error, not silently return every member.
        var result = await Service.GetTypeMembersAsync("RoslynService", memberKind: "Bogus");
        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task GetTypeHierarchy_ReportsTotalInterfaces()
    {
        // interfaces is now capped (like derivedTypes) with the true total reported,
        // instead of an uncapped list with no count.
        var search = await Service.SearchSymbolsAsync("FixtureRectangle", kind: "Class", maxResults: 5);
        var loc = (GetData(search)["results"] as JArray)!
            .First(s => s["name"]!.Value<string>() == "FixtureRectangle")["location"]!;

        var result = await Service.GetTypeHierarchyAsync(
            loc["filePath"]!.Value<string>()!, loc["line"]!.Value<int>(), loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        data["totalInterfaces"].Should().NotBeNull("the interface total must be reported alongside the capped list");
        data["totalInterfaces"]!.Value<int>().Should().Be((data["interfaces"] as JArray)!.Count,
            "with no cap hit, totalInterfaces equals the returned interface count");
    }

    [Fact]
    public async Task GetMethodSignature_ReturnsDetails()
    {
        var result = await Service.GetMethodSignatureAsync("RoslynService", "LoadSolutionAsync");

        AssertSuccess(result);
        var data = GetData(result);
        // The impl emits `name`, not `methodName`. The previous
        // `data["methodName"]?.Value<string>().Should().Be("X")` silently passed because
        // C# null-conditional short-circuits the ENTIRE chain — including .Should().Be(...)
        // — when the prefix is null. Lock the actual field name to defeat that pattern.
        data["name"].Should().NotBeNull("response must include the method name");
        data["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        data["returnType"].Should().NotBeNull();
        data["returnType"]!.Value<string>().Should().Contain("Task<object>");
        var parameters = data["parameters"] as JArray;
        parameters.Should().NotBeNullOrEmpty();
        parameters!.Count.Should().Be(2, "solutionPath plus the cancellationToken every tool method carries");
        parameters[0]["name"].Should().NotBeNull();
        parameters[0]["name"]!.Value<string>().Should().Be("solutionPath");
        parameters[1]["name"].Should().NotBeNull();
        parameters[1]["name"]!.Value<string>().Should().Be("cancellationToken");
    }

    [Fact]
    public async Task GetDerivedTypes_FindsTransitiveSubclasses()
    {
        var result = await Service.GetDerivedTypesAsync("FixtureRectangle");

        AssertSuccess(result);
        var data = GetData(result);
        data["baseType"].Should().NotBeNull();
        data["baseType"]!.Value<string>().Should().EndWith("FixtureRectangle");

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
    public async Task FindCallers_OnFixtureAnchor_FindsExactlyFiveNamedCallers()
    {
        // Anchor is called from exactly five distinct methods, so the caller set is a
        // fixed list rather than a `>= 20` inequality against the live source.
        var searchResult = await Service.SearchSymbolsAsync("Anchor", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols!.First(s => s["containingType"]!.Value<string>()!.EndsWith("ReferenceCountTarget"));
        var loc = symbol["location"]!;
        var result = await Service.FindCallersAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        var callers = data["callers"] as JArray;
        callers.Should().NotBeNull();
        callers!.Count.Should().Be(5, "Anchor is called from CallerOne through CallerFive");
        var names = callers
            .Select(c => c["callingSymbol"]!["name"]!.Value<string>())
            .ToList();
        names.Should().BeEquivalentTo(
            new[] { "CallerOne", "CallerTwo", "CallerThree", "CallerFour", "CallerFive" });
    }

    [Fact]
    public async Task SemanticQuery_FindsAsyncMethods()
    {
        // Constrain to methods: only methods can be async, and the impl emits the
        // isAsync field exclusively for method results — so kinds:["Method"] makes
        // the async-filter invariant verifiable on every returned symbol. (Querying
        // every kind leaks non-method symbols that carry no isAsync field, which the
        // old `symbol["isAsync"]?...` silent-pass hid.)
        var result = await Service.SemanticQueryAsync(
            kinds: new List<string> { "Method" },
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
        results!.Count.Should().BeGreaterThan(5, "Should find async methods");
        // Filter invariant: every returned method must satisfy the isAsync filter.
        foreach (var symbol in results)
        {
            symbol["isAsync"].Should().NotBeNull("every method result carries isAsync");
            symbol["isAsync"]!.Value<bool>().Should().BeTrue(
                "isAsync:true must drop non-async methods");
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
            symbol["accessibility"].Should().NotBeNull();
            symbol["accessibility"]!.Value<string>().Should().Be("Public");
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
        data["kind"].Should().NotBeNull();
        data["kind"]!.Value<string>().Should().Be("Method");
        data["name"].Should().NotBeNull();
        data["name"]!.Value<string>().Should().Be("LoadSolutionAsync");
        data["containingType"].Should().NotBeNull();
        data["containingType"]!.Value<string>().Should().Contain("RoslynService");
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
        // must reflect that.
        var result = await Service.GetMethodSignatureAsync("System.String", "Substring");

        AssertSuccess(result);
        var data = GetData(result);
        // Note: the impl emits `name`, not `methodName`.
        data["name"].Should().NotBeNull();
        data["name"]!.Value<string>().Should().Be("Substring");
        data["overloadCount"].Should().NotBeNull();
        data["overloadCount"]!.Value<int>().Should().Be(2,
            "String.Substring has exactly 2 overloads in the BCL surface");
        // selectedOverload defaults to 0 — the first overload is returned.
        data["selectedOverload"].Should().NotBeNull();
        data["selectedOverload"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public async Task GetMethodSignature_OverloadIndexOutOfRange_ReturnsInvalidParameter()
    {
        // String.Substring has exactly 2 overloads, so index 2 is out of range.
        // It must surface as a structured error, never as a silent fallback to
        // overload 0 — an agent asking for a specific overload would otherwise
        // act on the wrong signature.
        var result = await Service.GetMethodSignatureAsync("System.String", "Substring", overloadIndex: 2);
        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task GetMethodSignature_NegativeOverloadIndex_ReturnsInvalidParameter()
    {
        var result = await Service.GetMethodSignatureAsync("System.String", "Substring", overloadIndex: -1);
        AssertError(result, ErrorCodes.InvalidParameter);
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
        p1["offset"].Should().NotBeNull();
        p1["offset"]!.Value<int>().Should().Be(0);
        p1["hasMore"].Should().NotBeNull();
        p1["hasMore"]!.Value<bool>().Should().BeTrue(
            "the solution has dozens of *Async methods; 5 doesn't exhaust them");

        var nextOffset = p1["pagination"]!["nextOffset"]!.Value<int>();
        nextOffset.Should().Be(5);

        var page2 = await Service.SearchSymbolsAsync("*Async", kind: "Method", maxResults: 5, offset: nextOffset);
        var p2 = GetData(page2);
        p2["offset"].Should().NotBeNull();
        p2["offset"]!.Value<int>().Should().Be(5);

        // Page 2 results must not overlap with page 1.
        var p1Names = (p1["results"] as JArray)!.Select(r => r["fullyQualifiedName"]?.Value<string>()).ToList();
        var p2Names = (p2["results"] as JArray)!.Select(r => r["fullyQualifiedName"]?.Value<string>()).ToList();
        p1Names.Intersect(p2Names).Should().BeEmpty(
            "page 2 must not duplicate page 1 entries");
    }

    [Fact]
    public async Task FindReferences_WithMaxResults_CapsListButTotalReportsTrueCount()
    {
        // ReferenceCountFixture.Anchor has exactly five references; with maxResults=3
        // the list caps at 3 while totalReferences reports the true 5.
        var searchResult = await Service.SearchSymbolsAsync("Anchor", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var loc = symbols!.First(s => s["containingType"]!.Value<string>()!.EndsWith("ReferenceCountTarget"))["location"]!;

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
        data["totalReferences"].Should().NotBeNull();
        data["totalReferences"]!.Value<int>().Should().Be(5,
            "totalReferences must reflect the true count (5), not the capped count (3)");
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
            symbol["accessibility"].Should().NotBeNull();
            symbol["accessibility"]!.Value<string>().Should().Be("Public",
                "accessibility=Public must be satisfied for every returned symbol");
            symbol["isStatic"].Should().NotBeNull();
            symbol["isStatic"]!.Value<bool>().Should().BeTrue(
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

    [Fact]
    public async Task SemanticQuery_PastTheCap_ReportsHonestTotalCountAndTruncated()
    {
        // The solution declares far more than one method. With maxResults=1 the
        // materialized list caps at 1, but the meta must report the true match
        // count and flag truncation — totalCount previously equalled returnedCount
        // by construction, so truncated never fired.
        var result = await Service.SemanticQueryAsync(
            kinds: new List<string> { "Method" },
            isAsync: null,
            namespaceFilter: null,
            accessibility: null,
            isStatic: null,
            type: null,
            returnType: null,
            attributes: null,
            parameterIncludes: null,
            parameterExcludes: null,
            maxResults: 1);

        AssertSuccess(result);
        (GetData(result)["results"] as JArray)!.Count.Should().Be(1,
            "maxResults=1 caps the materialized list at exactly one");

        // meta is a ResponseMetadata class → PascalCase in-process.
        var meta = JObject.FromObject(result)["meta"]!;
        meta["ReturnedCount"].Should().NotBeNull();
        meta["ReturnedCount"]!.Value<int>().Should().Be(1);
        meta["TotalCount"].Should().NotBeNull();
        meta["TotalCount"]!.Value<int>().Should().BeGreaterThan(1,
            "the true match count exceeds the cap of 1, so it must not collapse to returnedCount");
        meta["Truncated"].Should().NotBeNull();
        meta["Truncated"]!.Value<bool>().Should().BeTrue(
            "totalCount exceeding returnedCount must set truncated");
    }
}
