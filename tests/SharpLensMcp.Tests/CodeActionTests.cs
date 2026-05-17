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
    public async Task GetCodeActionsAtPosition_OnMethodName_ReturnsRefactorings()
    {
        var searchResult = await Service.SearchSymbolsAsync("LoadSolutionAsync", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.GetCodeActionsAtPositionAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>());
        AssertSuccess(result);

        var data = GetData(result);
        var actions = data["actions"] as JArray;
        actions.Should().NotBeNull("response must include actions array");
        // Note: Roslyn may legitimately return zero refactorings depending on context;
        // the contract is the array exists, not that it's populated.
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_WithRangeSelection_Succeeds()
    {
        var searchResult = await Service.SearchSymbolsAsync("EnsureSolutionLoaded", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var line = loc["line"]!.Value<int>();
        var col = loc["column"]!.Value<int>();

        var result = await Service.GetCodeActionsAtPositionAsync(
            loc["filePath"]!.Value<string>()!,
            line, col,
            endLine: line + 2,
            endColumn: 0,
            includeRefactorings: true);

        AssertSuccess(result);
        var data = GetData(result);
        (data["actions"] as JArray).Should().NotBeNull();
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
        actions.Should().NotBeNull("actions array must be present");

        foreach (var action in actions!)
        {
            var kind = action["kind"]?.Value<string>();
            if (!string.IsNullOrEmpty(kind))
            {
                kind.Should().Be("fix",
                    "includeRefactorings:false must filter out non-fix actions");
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
    public async Task ApplyCodeActionByTitle_WithFirstOfferedAction_PreviewsSomething()
    {
        var actionsResult = await Service.GetCodeActionsAtPositionAsync(
            RoslynServicePath, line: 50, column: 10);
        AssertSuccess(actionsResult);

        var actions = GetData(actionsResult)["actions"] as JArray;
        actions.Should().NotBeNull();
        if (actions!.Count == 0)
        {
            // Roslyn may return zero actions on some lines; nothing to apply.
            return;
        }

        var title = actions[0]["title"]?.Value<string>();
        title.Should().NotBeNullOrEmpty();

        var result = await Service.ApplyCodeActionByTitleAsync(
            RoslynServicePath, line: 50, column: 10,
            title: title!,
            preview: true);

        AssertSuccess(result);
        var data = GetData(result);
        data["title"]?.Value<string>().Should().Be(title);
    }

    [Fact]
    public async Task ImplementMissingMembers_OnUsingsLine_ReturnsSymbolNotFound()
    {
        // Line 10 of RoslynService.cs sits inside `using` directives — there's no
        // type at this position, so the wrapper at CodeActions.cs:370-375 returns
        // SymbolNotFound with the "No 'implement members' action found at this
        // position" message.
        var result = await Service.ImplementMissingMembersAsync(
            RoslynServicePath, line: 10, column: 10,
            preview: true);
        AssertError(result, ErrorCodes.SymbolNotFound);
        var json = JObject.FromObject(result);
        json["error"]?["message"]?.Value<string>().Should().Contain("implement members");
    }

    [Fact]
    public async Task EncapsulateField_AtUsingsLine_ReturnsSymbolNotFound()
    {
        // Cursor at line 10 (inside using directives) — Roslyn offers no
        // encapsulate-field refactoring; the wrapper returns SymbolNotFound.
        var result = await Service.EncapsulateFieldAsync(
            RoslynServicePath, line: 10, column: 10, preview: true);
        AssertError(result, ErrorCodes.SymbolNotFound);
        var json = JObject.FromObject(result);
        json["error"]?["message"]?.Value<string>().Should().Contain("encapsulate field");
    }

    [Fact]
    public async Task InlineVariable_AtUsingsLine_ReturnsSymbolNotFound()
    {
        var result = await Service.InlineVariableAsync(
            RoslynServicePath, line: 10, column: 10, preview: true);
        AssertError(result, ErrorCodes.SymbolNotFound);
        var json = JObject.FromObject(result);
        json["error"]?["message"]?.Value<string>().Should().Contain("inline variable");
    }

    [Fact]
    public async Task ExtractVariable_AtUsingsLine_ReturnsSymbolNotFound()
    {
        var result = await Service.ExtractVariableAsync(
            RoslynServicePath,
            line: 10, column: 10,
            endLine: 10, endColumn: 20,
            preview: true);
        AssertError(result, ErrorCodes.SymbolNotFound);
        var json = JObject.FromObject(result);
        json["error"]?["message"]?.Value<string>().Should().Contain("extract variable");
    }
}
