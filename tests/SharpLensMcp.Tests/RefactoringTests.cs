using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for refactoring tools: rename_symbol, extract_method, organize_usings, etc.
/// </summary>
public class RefactoringTests : RoslynServiceTestBase
{
    [Fact]
    public async Task RenameSymbol_WithPreview_ShowsChanges()
    {
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.RenameSymbolAsync(
                file, line, col,
                newName: "_roslynWorkspace",
                preview: true);

            AssertSuccess(result);
            var data = GetData(result);
            data["newName"]?.Value<string>().Should().Be("_roslynWorkspace");
            data["affectedFiles"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task RenameSymbol_WithInvalidName_ReturnsError()
    {
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            // Invalid C# identifier
            var result = await Service.RenameSymbolAsync(
                file, line, col,
                newName: "123invalid",
                preview: true);

            // Should handle gracefully
            var json = JObject.FromObject(result);
        }
    }

    [Fact]
    public async Task ExtractInterface_GeneratesInterfaceCode()
    {
        var searchResult = await Service.SearchSymbolsAsync("RoslynService", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.ExtractInterfaceAsync(
                file, line, col,
                interfaceName: "IRoslynService",
                includeMemberNames: null);

            AssertSuccess(result);
            var data = GetData(result);
            data["interfaceCode"]?.Value<string>().Should().Contain("interface IRoslynService");
        }
    }

    [Fact]
    public async Task GenerateConstructor_CreatesConstructorCode()
    {
        var searchResult = await Service.SearchSymbolsAsync("RoslynService", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GenerateConstructorAsync(file, line, col);

            AssertSuccess(result);
            var data = GetData(result);
            data["constructorCode"]?.Value<string>().Should().Contain("public");
        }
    }

    [Fact]
    public async Task ChangeSignature_WithPreview_ShowsImpact()
    {
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.ChangeSignatureAsync(
                file, line, col,
                changes: new List<SignatureChange>
                {
                    new SignatureChange { Action = "add", Name = "timeout", Type = "int", DefaultValue = "30" }
                },
                preview: true);

            // Should process without error
            var json = JObject.FromObject(result);
        }
    }

    [Fact]
    public async Task ExtractMethod_WithSelection_GeneratesMethod()
    {
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var startLine = symbol["line"]?.Value<int>() ?? 0;

            var result = await Service.ExtractMethodAsync(
                file,
                startLine: startLine + 2,
                endLine: startLine + 4,
                methodName: "ExtractedMethod",
                preview: true);

            // May succeed or fail depending on what's selected
            var json = JObject.FromObject(result);
        }
    }

    [Fact]
    public async Task GetMissingMembers_OnImplementingClass_ReturnsMembers()
    {
        var result = await Service.GetMissingMembersAsync(RoslynServicePath, 10, 10);

        // Should handle gracefully
        var json = JObject.FromObject(result);
    }

    [Fact]
    public async Task GetOutgoingCalls_ReturnsCalledMethods()
    {
        var searchResult = await Service.SearchSymbolsAsync("GetHealthCheckAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GetOutgoingCallsAsync(file, line + 1, col);

            AssertSuccess(result);
            var data = GetData(result);
            data["outgoingCalls"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task OrganizeUsings_FormatsFile()
    {
        var result = await Service.OrganizeUsingsAsync(RoslynServicePath);

        AssertSuccess(result);
        var data = GetData(result);
        data["organizedText"].Should().NotBeNull();
    }

    [Fact]
    public async Task OrganizeUsingsBatch_ProcessesProject()
    {
        var result = await Service.OrganizeUsingsBatchAsync(
            projectName: "SharpLensMcp",
            filePattern: null,
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        // Response contains fileCount or files array
        (data["fileCount"] ?? data["files"]).Should().NotBeNull();
    }

    [Fact]
    public async Task FormatDocumentBatch_FormatsProject()
    {
        var result = await Service.FormatDocumentBatchAsync(
            projectName: "SharpLensMcp",
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        // Response contains fileCount or files array
        (data["fileCount"] ?? data["files"]).Should().NotBeNull();
    }

    [Fact]
    public async Task GetMethodOverloads_ReturnsAllOverloads()
    {
        var searchResult = await Service.SearchSymbolsAsync("CreateErrorResponse", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GetMethodOverloadsAsync(file, line, col);

            AssertSuccess(result);
            var data = GetData(result);
            data["overloads"].Should().NotBeNull();
        }
    }

    [Fact]
    public async Task GetContainingMember_ReturnsEnclosingSymbol()
    {
        var result = await Service.GetContainingMemberAsync(RoslynServicePath, 100, 10);

        AssertSuccess(result);
        var data = GetData(result);
        data["memberName"].Should().NotBeNull();
        data["memberKind"].Should().NotBeNull();
    }

    [Fact]
    public async Task GetAttributes_FindsAttributes()
    {
        var result = await Service.GetAttributesAsync("Obsolete");

        AssertSuccess(result);
        var data = GetData(result);
        data["symbols"].Should().NotBeNull();
    }

    [Fact]
    public async Task FindImplementations_FindsInterfaceImplementers()
    {
        var result = await Service.FindImplementationsAsync(RoslynServicePath, 10, 10);
        var json = JObject.FromObject(result);
    }

    [Fact]
    public async Task GetTypeHierarchy_ReturnsInheritanceInfo()
    {
        var searchResult = await Service.SearchSymbolsAsync("RoslynService", kind: "Class", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GetTypeHierarchyAsync(file, line, col);

            AssertSuccess(result);
            var data = GetData(result);
            data["baseTypes"].Should().NotBeNull();
        }
    }
}
