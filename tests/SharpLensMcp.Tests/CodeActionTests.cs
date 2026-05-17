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
    private async Task<(string file, int startLine, int endLine)> LocateSumExtractableSelectionAsync()
    {
        // RefactoringTarget.Sum body has two `var` statements ("var partial = a + b;"
        // and "var total = partial + c;"). Roslyn's extract-method provider reliably
        // offers an Extract-method refactoring over this span — it's the most
        // deterministic positive position in our fixture set.
        var searchResult = await Service.SearchSymbolsAsync("Sum", kind: "Method", maxResults: 50);
        var symbols = GetData(searchResult)["results"] as JArray;
        var sum = symbols!.First(s =>
            s["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);
        var loc = sum["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var methodLine = loc["line"]!.Value<int>();
        return (file, methodLine + 2, methodLine + 3);
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_OverTwoStatementsInSumBody_OffersExtractMethod()
    {
        var (file, startLine, endLine) = await LocateSumExtractableSelectionAsync();

        var result = await Service.GetCodeActionsAtPositionAsync(
            file,
            line: startLine,
            column: 0,
            endLine: endLine,
            endColumn: 50,
            includeRefactorings: true);

        AssertSuccess(result);
        var data = GetData(result);
        var actions = data["actions"] as JArray;
        actions.Should().NotBeNullOrEmpty(
            "a multi-statement selection inside a method body must offer at least an extract-method refactoring");
        actions!.Any(a => a["title"]?.Value<string>()?.Contains("Extract", StringComparison.OrdinalIgnoreCase) == true)
            .Should().BeTrue("the actions list must include an Extract-method title");
        data["refactoringCount"]?.Value<int>().Should().BeGreaterThan(0,
            "the extract-method offering must be classified as a refactoring");
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_IncludeOnlyFixes_StripsRefactoringActionsFromSumSelection()
    {
        var (file, startLine, endLine) = await LocateSumExtractableSelectionAsync();

        // Baseline: unfiltered must include at least one refactoring or there's nothing to filter.
        var unfiltered = await Service.GetCodeActionsAtPositionAsync(
            file, startLine, 0, endLine, 50);
        var baselineActions = GetData(unfiltered)["actions"] as JArray;
        baselineActions.Should().NotBeNullOrEmpty();
        baselineActions!.Any(a => a["kind"]?.Value<string>() == "refactoring")
            .Should().BeTrue(
                "baseline must include refactorings or this test cannot detect a filtering regression");

        // Filtered: same span with includeRefactorings:false. Every surviving action
        // must be kind="fix"; the strict count drop proves filtering actually fired.
        var filtered = await Service.GetCodeActionsAtPositionAsync(
            file, startLine, 0, endLine, 50,
            includeCodeFixes: true,
            includeRefactorings: false);
        AssertSuccess(filtered);
        var data = GetData(filtered);
        var actions = data["actions"] as JArray;
        actions.Should().NotBeNull("the actions array must be present on success");
        foreach (var action in actions!)
        {
            action["kind"]?.Value<string>().Should().Be("fix",
                "includeRefactorings:false must filter out every action whose kind is not 'fix'");
        }
        actions!.Count.Should().BeLessThan(baselineActions!.Count,
            "filtering refactorings out must produce strictly fewer actions than the unfiltered baseline");
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_WithNonExistentTitle_ReturnsSymbolNotFoundListingAvailableTitles()
    {
        var result = await Service.ApplyCodeActionByTitleAsync(
            RoslynServicePath, line: 10, column: 10,
            title: "This action does not exist 12345");

        AssertError(result, ErrorCodes.SymbolNotFound);
        // Line 10/col 10 sits inside the using directives and surfaces "Sort Usings".
        // The non-matching title triggers CodeActions.cs:192-193 where availableTitles
        // count > 0 — the hint becomes "Available actions: ..." listing what IS offered.
        var json = JObject.FromObject(result);
        json["error"]?["hint"]?.Value<string>().Should().Contain("Available actions:",
            "when titles exist at the position, hint must list them rather than say 'No actions available'");
        json["error"]?["hint"]?.Value<string>().Should().Contain("Sort Usings",
            "Sort Usings is the deterministic refactoring offered in a using directive");
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_OnExtractMethodOfferAtSumSelection_PreviewsChangeWithoutWriting()
    {
        // Discover the extract-method title Roslyn offers for the Sum selection — Roslyn
        // versions vary ("Extract method", "Extract Method", "Extract local function"),
        // so we read it back and apply that exact title rather than guessing.
        var (file, startLine, endLine) = await LocateSumExtractableSelectionAsync();

        var actionsResult = await Service.GetCodeActionsAtPositionAsync(
            file, startLine, 0, endLine, 50);
        var actions = GetData(actionsResult)["actions"] as JArray;
        actions.Should().NotBeNullOrEmpty();
        var extractTitle = actions!
            .Select(a => a["title"]?.Value<string>())
            .First(t => t != null && t.Contains("Extract", StringComparison.OrdinalIgnoreCase));

        // `file` is solution-relative; resolve to absolute for the defensive snapshot.
        var absoluteFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, file);
        var snapshot = File.ReadAllText(absoluteFile);
        try
        {
            var result = await Service.ApplyCodeActionByTitleAsync(
                file, startLine, 0,
                title: extractTitle!,
                endLine: endLine, endColumn: 50,
                preview: true);

            AssertSuccess(result);
            var data = GetData(result);
            data["actionTitle"]?.Value<string>().Should().Be(extractTitle);
            data["preview"]?.Value<bool>().Should().BeTrue();
            data["applied"]?.Value<bool>().Should().BeFalse(
                "preview=true must report applied=false per CodeActions.cs:310");
            var changed = data["changedFiles"] as JArray;
            changed.Should().NotBeNullOrEmpty(
                "extract-method preview must report at least one changed file");
            var newText = changed![0]["newText"]?.Value<string>();
            newText.Should().NotBeNullOrEmpty("preview=true must include the new file text");
        }
        finally
        {
            // Defensive: preview=true must NOT mutate disk. If a regression flipped that
            // contract, this restore prevents corrupting the fixture for downstream tests.
            File.WriteAllText(absoluteFile, snapshot);
        }
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
