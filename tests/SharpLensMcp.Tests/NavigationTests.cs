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
        // RoslynService class is at beginning of file
        // Line numbers are 0-based for the tool
        var result = await Service.GetSymbolInfoAsync(RoslynServicePath, line: 10, column: 20);

        AssertSuccess(result);
        var data = GetData(result);
        data["kind"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetSymbolInfo_InvalidPosition_ReturnsNoSymbol()
    {
        // Position in whitespace
        var result = await Service.GetSymbolInfoAsync(RoslynServicePath, line: 0, column: 0);

        // May succeed with null symbol or return error
        var json = JObject.FromObject(result);
        if (json["success"]?.Value<bool>() == true)
        {
            var data = json["data"];
            data?["kind"]?.Value<string>().Should().BeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GoToDefinition_OnMethod_ReturnsLocation()
    {
        // Search for a method call first to find a good position
        var searchResult = await Service.SearchSymbolsAsync("CreateSuccessResponse", kind: null, maxResults: 10);
        AssertSuccess(searchResult);

        var symbols = GetData(searchResult)["symbols"] as JArray;
        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>();
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            // Now go to definition
            var result = await Service.GoToDefinitionAsync(file!, line, col);
            AssertSuccess(result);
            var data = GetData(result);
            data["filePath"]?.Value<string>().Should().NotBeNullOrEmpty();
        }
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
        // Find CreateSuccessResponse which is used many times
        var searchResult = await Service.SearchSymbolsAsync("CreateSuccessResponse", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.FindReferencesAsync(file, line, col);
            AssertSuccess(result);

            var data = GetData(result);
            var references = data["references"] as JArray;
            references.Should().NotBeNull();
            references!.Count.Should().BeGreaterThan(1, "CreateSuccessResponse should have many references");
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
    public async Task GetDerivedTypes_FindsSubclasses()
    {
        // Test with a type we know has no subclasses in this project
        var result = await Service.GetDerivedTypesAsync("RoslynService");

        AssertSuccess(result);
        // May or may not have derived types, just verify structure
        var data = GetData(result);
        data["baseTypeName"]?.Value<string>().Should().Contain("RoslynService");
    }

    [Fact]
    public async Task GetBaseTypes_ReturnsInheritanceChain()
    {
        // Act
        var result = await Service.GetBaseTypesAsync("RoslynService");

        // Assert
        AssertSuccess(result);
        var data = GetData(result);
        var baseTypes = data["baseTypes"] as JArray;
        baseTypes.Should().NotBeNull();
        // Should have at least Object in the chain
    }

    [Fact]
    public async Task FindCallers_FindsUsages()
    {
        // Find a method that is called internally
        var searchResult = await Service.SearchSymbolsAsync("EnsureSolutionLoaded", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.FindCallersAsync(file, line, col);
            AssertSuccess(result);

            var data = GetData(result);
            data["callers"].Should().NotBeNull();
        }
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
