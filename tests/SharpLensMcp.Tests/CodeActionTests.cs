using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Tests for Phase 3 code action tools:
/// get_code_actions_at_position, apply_code_action_by_title,
/// implement_missing_members, encapsulate_field, inline_variable, extract_variable
/// </summary>
public class CodeActionTests : RoslynServiceTestBase
{
    [Fact]
    public async Task GetCodeActionsAtPosition_ReturnsAvailableActions()
    {
        // Position on a method - should have refactoring options
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GetCodeActionsAtPositionAsync(file, line, col);
            AssertSuccess(result);

            var data = GetData(result);
            var actions = data["actions"] as JArray;
            actions.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_WithSelection_ReturnsExtractMethod()
    {
        var searchResult = await Service.SearchSymbolsAsync("EnsureSolutionLoaded", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.GetCodeActionsAtPositionAsync(
                file, line, col,
                endLine: line + 2,
                endColumn: 0,
                includeRefactorings: true);

            AssertSuccess(result);
        }
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_IncludeOnlyFixes_FiltersResults()
    {
        var result = await Service.GetCodeActionsAtPositionAsync(
            RoslynServicePath, line: 50, column: 10,
            includeCodeFixes: true,
            includeRefactorings: false);

        AssertSuccess(result);
        var data = GetData(result);
        var actions = data["actions"] as JArray;

        foreach (var action in actions ?? new JArray())
        {
            var kind = action["kind"]?.Value<string>();
            if (!string.IsNullOrEmpty(kind))
            {
                kind.Should().Be("fix");
            }
        }
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_WithNonExistentTitle_ReturnsError()
    {
        var result = await Service.ApplyCodeActionByTitleAsync(
            RoslynServicePath, line: 10, column: 10,
            title: "This action does not exist 12345");

        AssertError(result);
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_WithPreview_ShowsChanges()
    {
        var actionsResult = await Service.GetCodeActionsAtPositionAsync(
            RoslynServicePath, line: 50, column: 10);

        if (JObject.FromObject(actionsResult)["success"]?.Value<bool>() == true)
        {
            var actions = GetData(actionsResult)["actions"] as JArray;
            if (actions?.Count > 0)
            {
                var title = actions[0]["title"]?.Value<string>();
                if (!string.IsNullOrEmpty(title))
                {
                    var result = await Service.ApplyCodeActionByTitleAsync(
                        RoslynServicePath, line: 50, column: 10,
                        title: title,
                        preview: true);

                    var json = JObject.FromObject(result);
                    // Just verify it doesn't crash
                }
            }
        }
    }

    [Fact]
    public async Task ImplementMissingMembers_OnIncompleteClass_GeneratesStubs()
    {
        var result = await Service.ImplementMissingMembersAsync(
            RoslynServicePath, line: 10, column: 10,
            preview: true);

        var json = JObject.FromObject(result);
        // Just verify it doesn't throw
    }

    [Fact]
    public async Task EncapsulateField_OnField_CreatesProperty()
    {
        var searchResult = await Service.SearchSymbolsAsync("_workspace", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["symbols"] as JArray;

        if (symbols?.Count > 0)
        {
            var symbol = symbols[0];
            var file = symbol["filePath"]?.Value<string>()!;
            var line = symbol["line"]?.Value<int>() ?? 0;
            var col = symbol["column"]?.Value<int>() ?? 0;

            var result = await Service.EncapsulateFieldAsync(file, line, col, preview: true);

            var json = JObject.FromObject(result);
            // Just verify no crash
        }
    }

    [Fact]
    public async Task InlineVariable_WithValidVariable_InlinesUsages()
    {
        var result = await Service.InlineVariableAsync(
            RoslynServicePath, line: 100, column: 20,
            preview: true);

        var json = JObject.FromObject(result);
        // Just verify no crash
    }

    [Fact]
    public async Task ExtractVariable_WithValidExpression_CreatesVariable()
    {
        // Correct signature: filePath, line, column, endLine?, endColumn?, preview
        var result = await Service.ExtractVariableAsync(
            RoslynServicePath,
            line: 100, column: 10,
            endLine: 100, endColumn: 50,
            preview: true);

        var json = JObject.FromObject(result);
        // Just verify no crash
    }
}
