using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Refactoring category (14 tools). Refactoring tools
// that modify disk (rename, change_signature applied, etc.) are covered in
// preview mode here — the apply-to-disk path is exercised directly in
// ChangeSignatureTests with its snapshot/restore fixture pattern.
public class RefactoringToolsViaMcpTests : McpTestBase
{
    public RefactoringToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RenameSymbol_Preview_ShowsChanges()
    {
        var (file, line, col) = await LocateSymbolAsync("_workspace", kind: "Field");
        var data = await CallAndGetDataAsync("roslyn:rename_symbol", new
        {
            filePath = file, line, column = col,
            newName = "_roslynWorkspace",
            preview = true
        });
        data["newName"]?.Value<string>().Should().Be("_roslynWorkspace");
        data["preview"]?.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task RenameSymbol_InvalidIdentifier_ReturnsToolError()
    {
        var (file, line, col) = await LocateSymbolAsync("_workspace", kind: "Field");
        var error = await CallAndGetErrorAsync("roslyn:rename_symbol", new
        {
            filePath = file, line, column = col,
            newName = "123invalid",
            preview = true
        });
        error["code"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ChangeSignature_Preview_OnMethodHolderConcat_ReturnsCallSites()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "Concat", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("ChangeSignatureMethodHolder") == true);
        var data = await CallAndGetDataAsync("roslyn:change_signature", new
        {
            filePath = file, line, column = col,
            changes = new object[]
            {
                new { action = "add", name = "extra", type = "int", defaultValue = "0" }
            },
            preview = true
        });
        data["preview"]?.Value<bool>().Should().BeTrue();
        (data["callSites"] as JArray)!.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExtractMethod_OnFixtureSelection_ReturnsExtractedCode()
    {
        var (file, methodLine, _) = await LocateSymbolAsync(
            "Sum", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);

        var data = await CallAndGetDataAsync("roslyn:extract_method", new
        {
            filePath = file,
            startLine = methodLine + 2,
            endLine = methodLine + 3,
            methodName = "ComputePartial",
            preview = true
        });
        data["extractedCode"]?.Value<string>().Should().Contain("ComputePartial");
        data["appliesEditsAutomatically"]?.Value<bool>().Should().BeFalse(
            "extract_method is generation-only per the 1.5.3 honesty fix");
    }

    [Fact]
    public async Task ExtractInterface_OnFixtureRectangle_GeneratesInterface()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "FixtureRectangle", kind: "Class");
        var data = await CallAndGetDataAsync("roslyn:extract_interface", new
        {
            filePath = file, line, column = col,
            interfaceName = "IFixtureRectangle"
        });
        data["interfaceCode"]?.Value<string>().Should().Contain("interface IFixtureRectangle");
    }

    [Fact]
    public async Task GenerateConstructor_OnRefactoringTarget_ProducesConstructor()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "RefactoringTarget", kind: "Class");
        var data = await CallAndGetDataAsync("roslyn:generate_constructor", new
        {
            filePath = file, line, column = col
        });
        data["constructorCode"]?.Value<string>().Should().Contain("public RefactoringTarget");
    }

    [Fact]
    public async Task OrganizeUsings_OnRoslynServiceCs_ReturnsOrganizedText()
    {
        var data = await CallAndGetDataAsync("roslyn:organize_usings", new
        {
            filePath = Fixture.RoslynServicePath
        });
        data["organizedText"].Should().NotBeNull();
    }

    [Fact]
    public async Task OrganizeUsingsBatch_OnSharpLensMcp_PreviewSucceeds()
    {
        var data = await CallAndGetDataAsync("roslyn:organize_usings_batch", new
        {
            projectName = "SharpLensMcp",
            preview = true
        });
        (data["fileCount"] ?? data["files"]).Should().NotBeNull(
            "batch response must report either a count or a file list");
    }

    [Fact]
    public async Task FormatDocumentBatch_OnSharpLensMcp_PreviewSucceeds()
    {
        var data = await CallAndGetDataAsync("roslyn:format_document_batch", new
        {
            projectName = "SharpLensMcp",
            preview = true
        });
        (data["fileCount"] ?? data["files"]).Should().NotBeNull();
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_OnMethodName_ReturnsActionsArray()
    {
        var (file, line, col) = await LocateSymbolAsync("LoadSolutionAsync", kind: "Method");
        var data = await CallAndGetDataAsync("roslyn:get_code_actions_at_position", new
        {
            filePath = file, line, column = col
        });
        data["actions"].Should().NotBeNull(
            "response must always include the actions array, even if empty");
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_NonExistentTitle_ReturnsToolError()
    {
        var error = await CallAndGetErrorAsync("roslyn:apply_code_action_by_title", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 10, column = 10,
            title = "this action does not exist 12345"
        });
        error["code"]?.Value<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ImplementMissingMembers_OnCompleteType_ReturnsEmptyOrError()
    {
        var inner = await CallToolAsync("roslyn:implement_missing_members", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 10, column = 10,
            preview = true
        });
        // Either success with empty members list, or a structured error.
        (inner["success"]?.Value<bool>() == true || inner["error"] != null)
            .Should().BeTrue("tool must return either structured success or structured error");
    }

    [Fact]
    public async Task EncapsulateField_OnFixtureBareCounter_ReturnsStructuredResponse()
    {
        var (file, line, col) = await LocateSymbolAsync("BareCounter", kind: "Field");
        var inner = await CallToolAsync("roslyn:encapsulate_field", new
        {
            filePath = file, line, column = col, preview = true
        });
        (inner["success"]?.Value<bool>() == true || inner["error"] != null)
            .Should().BeTrue();
    }

    [Fact]
    public async Task InlineVariable_OnFixtureGreetingFor_ReturnsStructuredResponse()
    {
        var (file, methodLine, _) = await LocateSymbolAsync("GreetingFor", kind: "Method");
        var inner = await CallToolAsync("roslyn:inline_variable", new
        {
            filePath = file, line = methodLine + 2, column = 12, preview = true
        });
        (inner["success"]?.Value<bool>() == true || inner["error"] != null)
            .Should().BeTrue();
    }

    [Fact]
    public async Task ExtractVariable_OnFixtureCompute_ReturnsStructuredResponse()
    {
        var (file, methodLine, _) = await LocateSymbolAsync(
            "Compute", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);
        var inner = await CallToolAsync("roslyn:extract_variable", new
        {
            filePath = file,
            line = methodLine + 2, column = 20,
            endLine = methodLine + 2, endColumn = 30,
            preview = true
        });
        (inner["success"]?.Value<bool>() == true || inner["error"] != null)
            .Should().BeTrue();
    }
}
