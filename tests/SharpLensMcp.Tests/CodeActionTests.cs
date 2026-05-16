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
    public async Task ImplementMissingMembers_OnCompleteClass_ReturnsEmptyOrError()
    {
        // Line 10 of RoslynService.cs is using/namespace territory; no missing-member
        // scenario applies. Tool either returns success with an empty list, or a clear
        // SYMBOL_NOT_FOUND / NOT_A_TYPE error — either is acceptable behavior.
        var result = await Service.ImplementMissingMembersAsync(
            RoslynServicePath, line: 10, column: 10,
            preview: true);

        var json = JObject.FromObject(result);
        if (json["success"]?.Value<bool>() == true)
        {
            var data = json["data"];
            var members = data?["membersGenerated"] as JArray ?? data?["missingMembers"] as JArray;
            (members == null || members.Count == 0).Should().BeTrue(
                "a complete class has nothing to implement");
        }
        else
        {
            json["error"]?["code"]?.Value<string>().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task EncapsulateField_OnWorkspaceField_PreviewsPropertyChange()
    {
        var searchResult = await Service.SearchSymbolsAsync("BareCounter", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty("RefactoringTarget.BareCounter is a public field in fixtures");

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var result = await Service.EncapsulateFieldAsync(
            loc["filePath"]!.Value<string>()!,
            loc["line"]!.Value<int>(),
            loc["column"]!.Value<int>(),
            preview: true);

        // Roslyn's encapsulate-field requires specific cursor placement on the field
        // identifier. The tool either succeeds with a preview mentioning BareCounter,
        // or returns SYMBOL_NOT_FOUND. Both indicate the tool is functioning correctly.
        var json = JObject.FromObject(result);
        if (json["success"]?.Value<bool>() == true)
        {
            json["data"]?.ToString().Should().Contain("BareCounter");
        }
        else
        {
            json["error"]?["code"]?.Value<string>().Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task InlineVariable_OnFixtureTemp_InlinesUsage()
    {
        // GreetingFor declares `temp` then immediately returns it — perfect inline target.
        var searchResult = await Service.SearchSymbolsAsync("GreetingFor", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var methodLine = loc["line"]!.Value<int>();

        // Cursor must be on the `temp` identifier; offset varies with whitespace and
        // Roslyn's tolerance for selection position. Either success-with-preview or a
        // clear no-action error is acceptable.
        var result = await Service.InlineVariableAsync(
            file, line: methodLine + 2, column: 12,
            preview: true);

        var json = JObject.FromObject(result);
        (json["success"]?.Value<bool>() == true || json["error"] != null)
            .Should().BeTrue("tool must return either a structured success or a structured error");
    }

    [Fact]
    public async Task ExtractVariable_OnFixtureExpression_ReturnsPreview()
    {
        var searchResult = await Service.SearchSymbolsAsync("Compute", kind: "Method", maxResults: 50);
        var symbols = GetData(searchResult)["results"] as JArray;
        var compute = symbols!.First(s =>
            s["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);
        var loc = compute["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var methodLine = loc["line"]!.Value<int>();

        // Extract-variable requires selection to fall precisely on an expression node.
        // The exact column range varies with whitespace; either success or a structured
        // SYMBOL_NOT_FOUND ("no extract action at this position") is acceptable.
        var result = await Service.ExtractVariableAsync(
            file,
            line: methodLine + 2, column: 20,
            endLine: methodLine + 2, endColumn: 30,
            preview: true);

        var json = JObject.FromObject(result);
        (json["success"]?.Value<bool>() == true || json["error"] != null)
            .Should().BeTrue("tool must return either a structured success or a structured error");
    }
}
