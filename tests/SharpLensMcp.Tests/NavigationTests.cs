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
        // Act - search for methods only
        var result = await Service.SearchSymbolsAsync("Get*", kind: "Method", maxResults: 50);

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNull();

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
        // Act
        var result = await Service.GetTypeMembersAsync("RoslynService", memberKind: "Method");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var members = data["members"] as JArray;
        members.Should().NotBeNull();

        foreach (var member in members!)
        {
            member["kind"]?.Value<string>().Should().Be("Method");
        }
    }

    [Fact]
    public async Task GetMethodSignature_ReturnsDetails()
    {
        // Act
        var result = await Service.GetMethodSignatureAsync("RoslynService", "LoadSolutionAsync");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        data["methodName"]?.Value<string>().Should().Be("LoadSolutionAsync");
        data["returnType"]?.Value<string>().Should().Contain("Task");
        data["parameters"].Should().NotBeNull();
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
        // Act - correct signature: kinds, isAsync, namespaceFilter, accessibility, isStatic, type, returnType, attributes, parameterIncludes, parameterExcludes, maxResults
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

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNull();
        results!.Count.Should().BeGreaterThan(5, "Should find async symbols");
    }

    [Fact]
    public async Task SemanticQuery_FiltersByAccessibility()
    {
        // Act
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

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var results = data["results"] as JArray;
        results.Should().NotBeNull();

        foreach (var symbol in results!)
        {
            symbol["accessibility"]?.Value<string>().Should().Be("Public");
        }
    }
}
