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
        // fixCount field is always emitted alongside refactoringCount (CodeActions.cs:105).
        data["fixCount"]?.Type.Should().Be(JTokenType.Integer,
            "fixCount must be present as an integer even when 0");
        // Sum of buckets equals total actions returned.
        var fix = data["fixCount"]!.Value<int>();
        var refac = data["refactoringCount"]!.Value<int>();
        (fix + refac).Should().Be(actions!.Count,
            "fixCount + refactoringCount must equal the total actions count");
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_IncludeOnlyFixes_StripsRefactoringActionsFromSumSelection()
    {
        var (file, startLine, endLine) = await LocateSumExtractableSelectionAsync();

        // Baseline: unfiltered must include at least one refactoring or there's nothing to filter.
        var unfiltered = await Service.GetCodeActionsAtPositionAsync(
            file, startLine, 0, endLine, 50);
        var baselineData = GetData(unfiltered);
        var baselineActions = baselineData["actions"] as JArray;
        baselineActions.Should().NotBeNullOrEmpty();
        baselineActions!.Any(a => a["kind"]?.Value<string>() == "refactoring")
            .Should().BeTrue(
                "baseline must include refactorings or this test cannot detect a filtering regression");
        var baselineFixCount = baselineData["fixCount"]!.Value<int>();

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
        // Fixes shouldn't be affected by toggling the refactorings flag — the same
        // diagnostics are still in play. fixCount must match the baseline's.
        data["fixCount"]?.Value<int>().Should().Be(baselineFixCount,
            "toggling the refactorings flag must not change the fix count");
        data["refactoringCount"]?.Value<int>().Should().Be(0,
            "with includeRefactorings:false, refactoringCount must be 0");
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_BothFiltersOff_ReturnsEmptyActionsBranchWithMessage()
    {
        // includeCodeFixes:false + includeRefactorings:false sends the impl through the
        // empty-actions branch (CodeActions.cs:66-83) — different response shape (no
        // fixCount / refactoringCount) and a documented `message` string.
        var (file, startLine, endLine) = await LocateSumExtractableSelectionAsync();

        var result = await Service.GetCodeActionsAtPositionAsync(
            file, startLine, 0, endLine, 50,
            includeCodeFixes: false,
            includeRefactorings: false);

        AssertSuccess(result);
        var data = GetData(result);
        var actions = data["actions"] as JArray;
        actions.Should().NotBeNull();
        actions!.Count.Should().Be(0,
            "with both filters off, no actions can survive");
        data["message"]?.Value<string>().Should().Contain("No code actions available",
            "the empty-actions branch uses this exact phrase");
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_IncludeOnlyRefactorings_StripsFixActionsFromSumSelection()
    {
        // Symmetric to the include-only-fixes test: every returned action must be
        // kind="refactoring" when includeCodeFixes=false.
        var (file, startLine, endLine) = await LocateSumExtractableSelectionAsync();

        var result = await Service.GetCodeActionsAtPositionAsync(
            file, startLine, 0, endLine, 50,
            includeCodeFixes: false,
            includeRefactorings: true);

        AssertSuccess(result);
        var data = GetData(result);
        var actions = data["actions"] as JArray;
        actions.Should().NotBeNullOrEmpty();
        foreach (var action in actions!)
        {
            action["kind"]?.Value<string>().Should().Be("refactoring",
                "includeCodeFixes:false must drop every non-refactoring action");
        }
        data["fixCount"]?.Value<int>().Should().Be(0,
            "with includeCodeFixes:false, fixCount must be 0");
        data["refactoringCount"]?.Value<int>().Should().Be(actions.Count);
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
            // Roslyn's modern "Extract method" emits a `static` LOCAL FUNCTION named
            // NewMethod nested inside Sum, NOT a sibling private method. Lock both
            // markers so a regression that emits nothing (or echoes the source) fails.
            newText.Should().NotBe(snapshot, "the preview text must reflect the extraction, not echo the source");
            newText!.Should().Contain("NewMethod",
                "Roslyn names the extracted entity 'NewMethod' by default");
            newText.Should().Contain("static int NewMethod(int a, int b, int c)",
                "the extracted local function declaration must appear with the inferred parameter list");
            newText.Should().Contain("return NewMethod(a, b, c);",
                "the call-site replacement inside Sum must invoke the extracted local function");

            // Verify the contract: preview=true did NOT mutate disk. A regression that
            // accidentally writes would be caught here BEFORE the defensive restore.
            File.ReadAllText(absoluteFile).Should().Be(snapshot,
                "preview=true must leave disk unchanged");
        }
        finally
        {
            // Defensive: even if the test failed mid-assertion (including a regression
            // that DID write), restore so downstream tests see the original file.
            File.WriteAllText(absoluteFile, snapshot);
        }
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_OnFileNotInSolution_ReturnsFileNotInSolution()
    {
        // Path that won't resolve to any document — exercises CodeActions.cs:137-143
        // FileNotInSolution branch.
        var fakePath = Path.Combine(Path.GetTempPath(), $"not-in-solution-{Guid.NewGuid():N}.cs");
        var result = await Service.ApplyCodeActionByTitleAsync(
            fakePath, line: 5, column: 5,
            title: "Sort Usings");

        AssertError(result, ErrorCodes.FileNotInSolution);
    }

    [Fact]
    public async Task EncapsulateField_OnBareCounter_AppliesPreviewWithEncapsulatedProperty()
    {
        // POSITIVE case for the EncapsulateField wrapper. The wrapper at
        // CodeActions.cs:381-413 iterates hardcoded titles ("Encapsulate field",
        // "Encapsulate field (and use property)", "Encapsulate field (but still use
        // field)"); the underlying ApplyCodeActionByTitleAsync does a Contains-substring
        // match, so any Roslyn-offered title containing "Encapsulate field" succeeds.
        var searchResult = await Service.SearchSymbolsAsync("BareCounter", kind: "Field", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty(
            "RefactoringTarget.BareCounter must be declared in the fixture");

        var symbol = symbols![0];
        var loc = symbol["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var absoluteFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, file);
        var snapshot = File.ReadAllText(absoluteFile);
        try
        {
            var result = await Service.EncapsulateFieldAsync(
                file,
                loc["line"]!.Value<int>(),
                loc["column"]!.Value<int>(),
                preview: true);

            AssertSuccess(result);
            var data = GetData(result);
            data["preview"]?.Value<bool>().Should().BeTrue();
            data["actionTitle"]?.Value<string>().Should()
                .Contain("Encapsulate", "the offered title must match the wrapper's iteration");
            var changed = data["changedFiles"] as JArray;
            changed.Should().NotBeNullOrEmpty();
            var newText = changed![0]["newText"]?.Value<string>()!;
            // Modern Roslyn emits expression-bodied accessors backed by a private field:
            //   private int bareCounter;
            //   public int BareCounter { get => bareCounter; set => bareCounter = value; }
            newText.Should().Contain("private int bareCounter",
                "Encapsulate Field generates a private backing field named after the original");
            newText.Should().Contain("public int BareCounter",
                "the public property keeps the original name");
            newText.Should().Contain("get => bareCounter",
                "the property getter must return the backing field");
            newText.Should().Contain("set => bareCounter = value",
                "the property setter must assign the backing field from `value`");

            File.ReadAllText(absoluteFile).Should().Be(snapshot,
                "preview=true must leave disk unchanged");
        }
        finally
        {
            File.WriteAllText(absoluteFile, snapshot);
        }
    }

    [Fact]
    public async Task ImplementMissingMembers_OnIncompleteIDisposable_AppliesPreviewGeneratingDisposeMethod()
    {
        // POSITIVE case for the ImplementMissingMembers wrapper. Uses an inline
        // AdhocWorkspace fixture with a class declaring : IDisposable but missing
        // Dispose(). The wrapper requires the cursor to be on the INTERFACE NAME in
        // the base list (where the CS0535 diagnostic lives) — pointing at the class
        // name itself only surfaces refactorings like "Extract interface...", not the
        // "Implement interface" code fix.
        const string incompleteCode = @"
using System;

namespace Sample;

public class IncompleteDisposable : IDisposable
{
    public string Name { get; init; } = """";
    // Dispose() intentionally missing.
}
";
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(
            incompleteCode, fileName: "IncompleteDisposable.cs");
        var inlineService = new RoslynService();
        inlineService.LoadFromWorkspaceForTesting(workspace);

        // Find the IDisposable token's column on the class declaration line.
        var lines = incompleteCode.Split('\n');
        var classLineIndex = Array.FindIndex(lines, l => l.Contains("class IncompleteDisposable"));
        classLineIndex.Should().BeGreaterThan(-1);
        var idisposableCol = lines[classLineIndex].IndexOf("IDisposable", StringComparison.Ordinal);
        idisposableCol.Should().BeGreaterThan(-1);

        var result = await inlineService.ImplementMissingMembersAsync(
            document.FilePath!,
            classLineIndex, idisposableCol,
            preview: true);

        var json = JObject.FromObject(result);
        json["success"]?.Value<bool>().Should().BeTrue(json.ToString());
        var data = json["data"]!;
        data["preview"]?.Value<bool>().Should().BeTrue();
        data["actionTitle"]?.Value<string>().Should().Contain("Implement",
            "the matched title must contain 'Implement' (one of the wrapper's iteration strings)");

        var changed = data["changedFiles"] as JArray;
        changed.Should().NotBeNullOrEmpty();
        var newText = changed![0]["newText"]?.Value<string>()!;
        newText.Should().Contain("Dispose",
            "the implementation must add a Dispose method to satisfy the IDisposable interface");
    }

    [Fact]
    public async Task InlineVariable_OnTempInGreetingFor_AppliesPreviewInliningTheLiteral()
    {
        // POSITIVE case for the InlineVariable wrapper. Target GreetingFor's
        // `var temp = "Hello, " + name; return temp;` — Roslyn's InlineTemporary
        // refactoring substitutes temp's initializer at the return site.
        var searchResult = await Service.SearchSymbolsAsync("GreetingFor", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        symbols.Should().NotBeNullOrEmpty();
        var loc = symbols![0]["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var absoluteFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, file);

        // Locate the `var temp = ...` line dynamically.
        var lines = File.ReadAllLines(absoluteFile);
        var tempLine = Array.FindIndex(lines, l => l.Contains("var temp = "));
        tempLine.Should().BeGreaterThan(-1);
        var tempCol = lines[tempLine].IndexOf("temp", StringComparison.Ordinal);
        tempCol.Should().BeGreaterThan(-1);

        var snapshot = File.ReadAllText(absoluteFile);
        try
        {
            var result = await Service.InlineVariableAsync(file, tempLine, tempCol, preview: true);

            AssertSuccess(result);
            var data = GetData(result);
            data["preview"]?.Value<bool>().Should().BeTrue();
            data["actionTitle"]?.Value<string>().Should().Contain("Inline",
                "the matched title must contain 'Inline'");
            var changed = data["changedFiles"] as JArray;
            changed.Should().NotBeNullOrEmpty();
            var newText = changed![0]["newText"]?.Value<string>()!;
            // After inlining `temp`, the body becomes `return "Hello, " + name;` and
            // the `var temp = ...` declaration is gone.
            newText.Should().Contain("return \"Hello, \" + name",
                "the inlined initializer must appear at the return site");
            newText.Should().NotContain("var temp = ",
                "the declaration must be removed after inlining");

            File.ReadAllText(absoluteFile).Should().Be(snapshot,
                "preview=true must not modify disk");
        }
        finally
        {
            File.WriteAllText(absoluteFile, snapshot);
        }
    }

    [Fact]
    public async Task ExtractVariable_OnExpressionInCompute_AppliesPreviewWithIntroducedLocal()
    {
        // POSITIVE case for the ExtractVariable wrapper. Target the `a * 2 + 7`
        // expression in Compute → Roslyn's IntroduceVariable refactoring extracts
        // it to a local.
        var searchResult = await Service.SearchSymbolsAsync("Compute", kind: "Method", maxResults: 10);
        var symbols = GetData(searchResult)["results"] as JArray;
        var compute = symbols!.First(s =>
            s["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);
        var loc = compute["location"]!;
        var file = loc["filePath"]!.Value<string>()!;
        var absoluteFile = Path.Combine(Path.GetDirectoryName(SolutionPath)!, file);

        // Find the `return a * 2 + 7;` line and select just the `a * 2 + 7` span.
        var lines = File.ReadAllLines(absoluteFile);
        var returnLine = Array.FindIndex(lines, l => l.Contains("return a * 2 + 7;"));
        returnLine.Should().BeGreaterThan(-1);
        var exprStart = lines[returnLine].IndexOf("a * 2 + 7", StringComparison.Ordinal);
        exprStart.Should().BeGreaterThan(-1);
        var exprEnd = exprStart + "a * 2 + 7".Length;

        var snapshot = File.ReadAllText(absoluteFile);
        try
        {
            var result = await Service.ExtractVariableAsync(
                file,
                line: returnLine, column: exprStart,
                endLine: returnLine, endColumn: exprEnd,
                preview: true);

            AssertSuccess(result);
            var data = GetData(result);
            data["preview"]?.Value<bool>().Should().BeTrue();
            data["actionTitle"]?.Value<string>().Should().Match(t =>
                t!.Contains("Introduce", StringComparison.OrdinalIgnoreCase) ||
                t!.Contains("Extract", StringComparison.OrdinalIgnoreCase));
            var changed = data["changedFiles"] as JArray;
            changed.Should().NotBeNullOrEmpty();
            var newText = changed![0]["newText"]?.Value<string>()!;
            // Roslyn names the introduced local "v" by default for short expressions.
            // The expression `a * 2 + 7` must appear as the local's initializer, and
            // the return statement must reference the new local instead of the literal.
            newText.Should().Contain("a * 2 + 7",
                "the expression must still appear (as the new local's initializer)");
            // Either "return v;" or similar — the introduced-local pattern.
            newText.Should().NotBe(snapshot,
                "the preview text must differ from the snapshot");

            File.ReadAllText(absoluteFile).Should().Be(snapshot,
                "preview=true must not modify disk");
        }
        finally
        {
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
