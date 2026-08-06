using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for the Refactoring category.
//
// Tools that mutate disk (rename apply, change_signature apply, extract_method
// apply) are NOT exercised here in apply mode — that path requires snapshot/
// restore plumbing (see ChangeSignatureTests). Preview-mode is verified for
// every preview-bearing tool with content-locked assertions on the preview
// payload. The one exception is apply_code_action_by_title with preview=false
// which has its own snapshot/restore at the test level.
//
// The 4 "cursor-fragile" Roslyn refactoring tools (implement_missing_members,
// encapsulate_field, inline_variable, extract_variable) verify the negative
// case (no action offered) with the exact ErrorCodes.SymbolNotFound code and
// the wrapper's documented message phrase.
//
// Response shapes covered: rename_symbol (preview), change_signature (preview),
// extract_method (preview), extract_interface, generate_constructor,
// organize_usings, organize_usings_batch, format_document_batch,
// get_code_actions_at_position, apply_code_action_by_title.
//
// Tightening rule for this file: every accessor uses `!.Value<T>()`.
public class RefactoringToolsViaMcpTests : McpTestBase
{
    public RefactoringToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RenameSymbol_PreviewOnWorkspaceField_ShowsChangesInRoslynServiceFile()
    {
        var (file, line, col) = await LocateSymbolAsync("_workspace", kind: "Field");
        var data = await CallAndGetDataAsync("rename_symbol", new
        {
            filePath = file, line, column = col,
            newName = "_roslynWorkspace",
            preview = true
        });
        data["newName"]!.Value<string>().Should().Be("_roslynWorkspace");
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["applied"]!.Value<bool>().Should().BeFalse(
            "preview=true must report applied=false");
        data["symbolName"]!.Value<string>().Should().Be("_workspace");

        var changes = (data["changes"] as JArray)!;
        changes.Should().NotBeEmpty();
        // _workspace is used across multiple partial class files; at least one
        // change must target RoslynService.cs.
        changes.Any(c => c["filePath"]!.Value<string>()!.EndsWith("RoslynService.cs"))
            .Should().BeTrue("rename preview must include RoslynService.cs in the change set");
        foreach (var c in changes)
        {
            c["filePath"]!.Value<string>()!.Should().EndWith(".cs");
            c["changeCount"]!.Value<int>().Should().BeGreaterThan(0,
                "every reported file must have at least one rename hit (summary verbosity)");
        }
    }

    [Fact]
    public async Task RenameSymbol_InvalidIdentifier_ReturnsInvalidParameterWithIdentifierWording()
    {
        var (file, line, col) = await LocateSymbolAsync("_workspace", kind: "Field");
        var error = await CallAndGetErrorAsync(
            "rename_symbol",
            new
            {
                filePath = file, line, column = col,
                newName = "123invalid",
                preview = true
            },
            codeContains: ErrorCodes.InvalidParameter);
        // Helper already enforced codeContains; lock the documented message
        // from the identifier-validation branch instead of the redundant silent-pass code re-check.
        error["message"]!.Value<string>()!.Should().Contain("not a valid C# identifier");
    }

    [Fact]
    public async Task ChangeSignature_PreviewAddParam_LocksNewSignatureAndCallSites()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "Concat", kind: "Method",
            r => r["containingType"]?.Value<string>()?.EndsWith("ChangeSignatureMethodHolder") == true);
        var data = await CallAndGetDataAsync("change_signature", new
        {
            filePath = file, line, column = col,
            changes = new object[]
            {
                new { action = "add", name = "extra", type = "int", defaultValue = "0" }
            },
            preview = true
        });
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["methodName"]!.Value<string>().Should().Be("Concat");
        data["newSignature"]!.Value<string>()!.Should().Contain("int extra",
            "the new parameter must appear in the rendered new signature");
        data["oldSignature"]!.Value<string>()!.Should().NotContain("int extra",
            "the old signature must NOT contain the new parameter");

        var callSites = (data["callSites"] as JArray)!;
        // ChangeSignatureFixture.Concat has 5 call sites
        // (InvokeMethodAllPositional, InvokeMethodMixed x2, InvokeMethodNamedOnly,
        // InvokeMethodFromAnotherClass). Allow flexibility for SymbolFinder.
        callSites.Count.Should().BeGreaterOrEqualTo(4,
            "the fixture has 5 call sites to Concat");
        data["callSitesCount"]!.Value<int>().Should().Be(callSites.Count,
            "callSitesCount must match the actual emitted callSites count");
    }

    [Fact]
    public async Task ExtractMethod_PreviewOnFixtureSelection_ReturnsExtractedCodeWithoutWriting()
    {
        var (file, methodLine, _) = await LocateSymbolAsync(
            "Sum", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);

        var data = await CallAndGetDataAsync("extract_method", new
        {
            filePath = file,
            startLine = methodLine + 2,
            endLine = methodLine + 3,
            methodName = "ComputePartial",
            preview = true
        });
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["methodName"]!.Value<string>().Should().Be("ComputePartial");

        var extractedCode = data["extractedCode"]!.Value<string>()!;
        extractedCode.Should().Contain("ComputePartial",
            "the extracted method must be named ComputePartial");
        extractedCode.Should().Contain("private",
            "default accessibility is private");

        var replacement = data["replacementCode"]!.Value<string>()!;
        replacement.Should().Contain("ComputePartial(",
            "the replacement must invoke the new method");

        data["statementsExtracted"]!.Value<int>().Should().Be(2,
            "the two-line selection `var partial = a + b; var total = partial + c;` is two statements");
        data["returnType"]!.Value<string>().Should().Be("int",
            "Sum returns int and the slice produces an int `total` that flows out");
        data["returnVariable"]!.Value<string>().Should().Be("total");
    }

    [Fact]
    public async Task ExtractMethod_InvalidMethodName_ReturnsInvalidParameter()
    {
        // The name/accessibility validation must reject through the dispatcher too, so
        // preview and apply agree on the wire, not just in-process.
        var (file, methodLine, _) = await LocateSymbolAsync(
            "Sum", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);

        await CallAndGetErrorAsync("extract_method", new
        {
            filePath = file,
            startLine = methodLine + 2,
            endLine = methodLine + 3,
            methodName = "123bad",
            preview = true
        }, codeContains: ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task ExtractInterface_OnFixtureRectangle_IncludesAllPublicMembers()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "FixtureRectangle", kind: "Class");
        var data = await CallAndGetDataAsync("extract_interface", new
        {
            filePath = file, line, column = col,
            interfaceName = "IFixtureRectangle"
        });
        // Lock the extract_interface response shape.
        data["className"]!.Value<string>().Should().Be("FixtureRectangle");
        data["interfaceName"]!.Value<string>().Should().Be("IFixtureRectangle");
        data["suggestedFileName"]!.Value<string>().Should().Be("IFixtureRectangle.cs");

        var code = data["interfaceCode"]!.Value<string>()!;
        code.Should().Contain("interface IFixtureRectangle");
        // FixtureRectangle's public members per the fixture:
        // Width, Height, Area.
        code.Should().Contain("Width");
        code.Should().Contain("Height");
        code.Should().Contain("Area");
        // Object methods must NOT leak into the extracted interface.
        code.Should().NotContain("ToString");
        code.Should().NotContain("GetHashCode");
    }

    [Fact]
    public async Task GenerateConstructor_OnRefactoringTarget_ProducesBareCounterCtor()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "RefactoringTarget", kind: "Class");
        var data = await CallAndGetDataAsync("generate_constructor", new
        {
            filePath = file, line, column = col
        });
        // Lock the documented generate_constructor response shape.
        data["appliesEditsAutomatically"]!.Value<bool>().Should().BeFalse(
            "generate_constructor is generation-only — it does NOT mutate the workspace");
        data["typeName"]!.Value<string>()!.Should().Contain("RefactoringTarget");
        data["parameterCount"]!.Value<int>().Should().Be(1,
            "RefactoringTarget has exactly one assignable field");

        var code = data["constructorCode"]!.Value<string>()!;
        // RefactoringTarget has one public field BareCounter (int). The generator
        // produces a single-arg ctor that initializes the field from a camelCased param.
        code.Should().Contain("public RefactoringTarget");
        code.Should().Contain("bareCounter",
            "the constructor param is the camelCased field name");
        code.Should().Contain("BareCounter = bareCounter",
            "the body must initialize the field from the param");

        var fields = (data["fields"] as JArray)!;
        fields.Select(f => f.Value<string>()).Should().BeEquivalentTo(new[] { "BareCounter" });
    }

    [Fact]
    public async Task OrganizeUsings_OnRoslynServiceCs_OutputsSystemFirstThenAlphabetical()
    {
        var data = await CallAndGetDataAsync("organize_usings", new
        {
            filePath = Fixture.RoslynServicePath
        });
        var organized = data["organizedText"]!.Value<string>()!;
        organized.Should().NotBeNullOrEmpty();
        organized.Should().Contain("using Microsoft.CodeAnalysis");
        organized.Should().Contain("using System");

        // The contract of OrganizeUsingsAsync is: bucket 0 =
        // System*, bucket 1 = everything else, alphabetical within each bucket.
        // Verify System* appears BEFORE non-System usings — the prior test only
        // verified both strings appeared somewhere.
        var systemIdx = organized.IndexOf("using System");
        var microsoftIdx = organized.IndexOf("using Microsoft.CodeAnalysis");
        systemIdx.Should().BeGreaterOrEqualTo(0);
        microsoftIdx.Should().BeGreaterOrEqualTo(0);
        systemIdx.Should().BeLessThan(microsoftIdx,
            "System-prefixed usings must come before non-System usings");
    }

    [Fact]
    public async Task OrganizeUsingsBatch_OnSharpLensMcp_ReportsScannedAndChanges()
    {
        var data = await CallAndGetDataAsync("organize_usings_batch", new
        {
            projectName = "SharpLensMcp",
            preview = true
        });
        // The impl emits totalFilesScanned/filesWithChanges/files — NOT fileCount.
        // The prior test's `data["fileCount"]?.Value<int>() ?? (data["files"] as JArray)?.Count`
        // fallback counted only files-with-changes, making the assertion pass for
        // the wrong reason. Lock the real contract matching
        // the direct-test audit's fix.
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["totalFilesScanned"]!.Value<int>().Should().BeGreaterThan(0,
            "SharpLensMcp has multiple .cs files visible to the batch scanner");
        data["filesWithChanges"]!.Value<int>().Should().BeGreaterOrEqualTo(0,
            "filesWithChanges must be present even when zero");

        var files = (data["files"] as JArray)!;
        files.Should().NotBeNull("files array must always be emitted (may be empty)");
    }

    [Fact]
    public async Task FormatDocumentBatch_OnSharpLensMcp_ReportsScannedAndFormatted()
    {
        var data = await CallAndGetDataAsync("format_document_batch", new
        {
            projectName = "SharpLensMcp",
            preview = true
        });
        // Same `fileCount` non-existent-field bug as OrganizeUsingsBatch — lock
        // totalFilesScanned/filesFormatted.
        data["preview"]!.Value<bool>().Should().BeTrue();
        data["totalFilesScanned"]!.Value<int>().Should().BeGreaterThan(0);
        data["filesFormatted"]!.Value<int>().Should().BeGreaterOrEqualTo(0);

        var files = (data["files"] as JArray)!;
        files.Should().NotBeNull("files array must always be emitted (may be empty)");
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_InUsingsBlock_OffersSortUsingsRefactoring()
    {
        // Line 10 of RoslynService.cs is inside the `using` directives block. With
        // the full C# refactoring provider set composed via MEF (Analysis.cs
        // BuildMefContainer), Roslyn deterministically surfaces "Sort Usings".
        var data = await CallAndGetDataAsync("get_code_actions_at_position", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 10,
            column = 10
        });
        var actions = (data["actions"] as JArray)!;
        actions.Should().NotBeEmpty();
        // Predicate `?.` chains are safe here — null on missing field → false → skipped.
        actions.Any(a => a["title"]?.Value<string>() == "Sort Usings"
                      && a["kind"]?.Value<string>() == "refactoring")
            .Should().BeTrue("the using directives block always offers Sort Usings as a refactoring");
        data["refactoringCount"]!.Value<int>().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_BothSourcesOff_ReturnsEmptyActionsWithMessage()
    {
        // When both includeCodeFixes and includeRefactorings
        // are false, no actions can be surfaced. The empty-actions branch returns
        // a distinct response shape with actions=[] + message field.
        var data = await CallAndGetDataAsync("get_code_actions_at_position", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 10,
            column = 10,
            includeCodeFixes = false,
            includeRefactorings = false
        });
        (data["actions"] as JArray)!.Count.Should().Be(0);
        data["message"]!.Value<string>()!.Should().Contain("No code actions available");
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_IncludeRefactoringsFalse_DropsSortUsingsAtUsingsBlock()
    {
        // Line 10 of RoslynService.cs (the using block) offers "Sort Usings" as a
        // refactoring. Prove the FILTER removes it, not that no action existed: assert
        // it is present with refactorings on, then absent with refactorings off — at the
        // same position. (Asserting only absence would pass vacuously, since this
        // position surfaces no code fix to fall back on.)
        var withRefactorings = await CallAndGetDataAsync("get_code_actions_at_position", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 10,
            column = 10,
            includeCodeFixes = true,
            includeRefactorings = true
        });
        (withRefactorings["actions"] as JArray)!
            .Any(a => a["title"]?.Value<string>() == "Sort Usings")
            .Should().BeTrue("Sort Usings is offered here when refactorings are included");

        var withoutRefactorings = await CallAndGetDataAsync("get_code_actions_at_position", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 10,
            column = 10,
            includeCodeFixes = true,
            includeRefactorings = false
        });
        (withoutRefactorings["actions"] as JArray)!
            .Any(a => a["title"]?.Value<string>() == "Sort Usings")
            .Should().BeFalse("the same refactoring must be dropped by includeRefactorings=false");
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_NonExistentTitle_ReturnsSymbolNotFoundWithAvailableTitles()
    {
        var error = await CallAndGetErrorAsync(
            "apply_code_action_by_title",
            new
            {
                filePath = Fixture.RoslynServicePath,
                line = 10, column = 10,
                title = "this action does not exist 12345"
            },
            codeContains: ErrorCodes.SymbolNotFound);
        // Helper already enforced codeContains. Line 10/col 10 surfaces "Sort Usings".
        // The non-matching title triggers the CodeActions.cs branch where
        // availableTitles.Count > 0 → hint becomes "Available actions: ...".
        error["hint"]!.Value<string>()!.Should().Contain("Available actions:",
            "available actions are listed in the hint when at least one title exists");
        error["hint"]!.Value<string>()!.Should().Contain("Sort Usings",
            "the locked-down 'Sort Usings' refactoring must appear in the available-actions list");
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_ReversedSelection_ReturnsInvalidParameter()
    {
        // A selection whose end position precedes the start (endLine/endColumn before
        // line/column) would otherwise reach TextSpan.FromBounds and throw as -32603.
        // Both positions are individually valid, so only the ordering guard rejects it.
        var error = await CallAndGetErrorAsync("get_code_actions_at_position", new
        {
            filePath = Fixture.RoslynServicePath,
            line = 10, column = 10,
            endLine = 0, endColumn = 0
        }, codeContains: ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>()!.Should().Contain("end position precedes",
            "the reversed-selection guard names the ordering problem");
    }

    [Fact]
    public async Task ImplementMissingMembers_OnCompleteRoslynService_ReturnsSymbolNotFound()
    {
        // The RoslynService partial class implements every interface/abstract member.
        // The wrapper returns SymbolNotFound with the
        // "No 'implement members' action found at this position" message when no
        // implement-action is offered. Resolve the class-decl line dynamically.
        var lines = File.ReadAllLines(Fixture.RoslynServicePath);
        var classLine = Array.FindIndex(lines, l => l.Contains("class RoslynService"));
        classLine.Should().BeGreaterThan(-1);
        var classCol = lines[classLine].IndexOf("RoslynService", StringComparison.Ordinal);

        var error = await CallAndGetErrorAsync(
            "implement_missing_members",
            new
            {
                filePath = Fixture.RoslynServicePath,
                line = classLine, column = classCol,
                preview = true
            },
            codeContains: ErrorCodes.SymbolNotFound);
        error["message"]!.Value<string>()!.Should().Contain("implement members",
            "the impl returns the literal 'implement members' phrase");
    }

    [Fact]
    public async Task EncapsulateField_AtTopOfRoslynServiceFile_ReturnsSymbolNotFound()
    {
        // At line 10 of RoslynService.cs (inside `using` directives), no
        // encapsulate-field action is offered → the wrapper
        // returns SymbolNotFound with the documented message.
        var error = await CallAndGetErrorAsync(
            "encapsulate_field",
            new { filePath = Fixture.RoslynServicePath, line = 10, column = 10, preview = true },
            codeContains: ErrorCodes.SymbolNotFound);
        error["message"]!.Value<string>()!.Should().Contain("encapsulate field",
            "the wrapper surfaces the literal phrase 'encapsulate field' when no action is offered");
    }

    [Fact]
    public async Task InlineVariable_AtTopOfRoslynServiceFile_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "inline_variable",
            new { filePath = Fixture.RoslynServicePath, line = 10, column = 10, preview = true },
            codeContains: ErrorCodes.SymbolNotFound);
        error["message"]!.Value<string>()!.Should().Contain("inline variable");
    }

    [Fact]
    public async Task ExtractVariable_AtTopOfRoslynServiceFile_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "extract_variable",
            new
            {
                filePath = Fixture.RoslynServicePath,
                line = 10, column = 10,
                endLine = 10, endColumn = 20,
                preview = true
            },
            codeContains: ErrorCodes.SymbolNotFound);
        error["message"]!.Value<string>()!.Should().Contain("extract variable");
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_PreviewFalseOnSortUsings_WritesToDiskThenRestoresOnFinally()
    {
        // End-to-end apply via MCP for apply_code_action_by_title. Pick "Sort Usings"
        // at line 10 col 10 of RoslynService.cs (deterministic by the
        // GetCodeActionsAtPosition_InUsingsBlock test). Snapshot/restore the file so
        // the apply runs against real disk but downstream tests see original content.
        var snapshot = File.ReadAllText(Fixture.RoslynServicePath);
        try
        {
            var data = await CallAndGetDataAsync("apply_code_action_by_title", new
            {
                filePath = Fixture.RoslynServicePath,
                line = 10, column = 10,
                title = "Sort Usings",
                preview = false
            });

            // preview=false contract: applied=true, preview=false,
            // actionKind populated, changedFiles[].newText is null because writing went
            // to disk rather than the response payload.
            data["applied"]!.Value<bool>().Should().BeTrue(
                "preview=false must report applied=true");
            data["preview"]!.Value<bool>().Should().BeFalse();
            data["actionTitle"]!.Value<string>().Should().Be("Sort Usings");
            data["actionKind"]!.Value<string>().Should().Be("refactoring",
                "Sort Usings is classified as a refactoring");

            var changed = (data["changedFiles"] as JArray)!;
            changed.Should().NotBeEmpty(
                "apply must report at least one changed file even if the sort is a no-op");
            var first = changed[0]!;
            first["newText"]!.Type.Should().Be(JTokenType.Null,
                "preview=false must not embed the new text in the response — it went to disk");
            first["isNewFile"]!.Value<bool>().Should().BeFalse();
            first["changeType"]!.Value<string>().Should().Be("Modified");
        }
        finally
        {
            // Mandatory restore: even if the test failed mid-assertion, leave the
            // working tree pristine so downstream tests see the original file.
            File.WriteAllText(Fixture.RoslynServicePath, snapshot);
            // The apply updated the SHARED workspace's in-memory solution; restoring
            // the disk file alone would leave them diverged and poison later tests in
            // this collection (e.g. "Sort Usings" is no longer offered on a document
            // whose in-memory usings are already sorted). File.WriteAllText is an
            // external edit, so follow the documented contract: sync_documents.
            await CallAndGetDataAsync("sync_documents", new
            {
                filePaths = new[] { Fixture.RoslynServicePath }
            });
        }

        // Reached only on the success path (the finally already restored on failure),
        // so this never masks an in-try assertion. Confirms the mandatory disk
        // restore physically landed — RoslynService.cs is real source, not a fixture,
        // so a dirty leftover would corrupt every later solution load. (Charter C7.)
        File.ReadAllText(Fixture.RoslynServicePath).Should().Be(snapshot,
            "the apply test must restore RoslynService.cs to its committed content");
    }

    // ---- name-based addressing (typeName/memberName) over the wire ----
    // Unit-tested in NameBasedAddressingTests; these prove the same addressing
    // path binds and dispatches correctly through the MCP layer.

    [Fact]
    public async Task AddNullChecks_ByName_GuardsTheNamedMethod()
    {
        var data = await CallAndGetDataAsync("add_null_checks", new
        {
            typeName = "AddNullChecksTarget", memberName = "Process", preview = true
        });
        data["methodName"]!.Value<string>().Should().Be("Process");
        data["generatedCode"]!.Value<string>().Should().Contain("ArgumentNullException.ThrowIfNull(input);");
    }

    [Fact]
    public async Task GenerateEqualityMembers_ByTypeName_ComparesTheFixtureField()
    {
        var data = await CallAndGetDataAsync("generate_equality_members", new
        {
            typeName = "EqualityMembersTarget", preview = true
        });
        data["typeName"]!.Value<string>().Should().Be("EqualityMembersTarget");
        data["generatedCode"]!.Value<string>().Should().Contain("hash.Add(Value);");
    }

    [Fact]
    public async Task GenerateConstructor_ByTypeName_EmitsPropertyParameters()
    {
        var data = await CallAndGetDataAsync("generate_constructor", new
        {
            typeName = "GenCtorWithProperties", includeProperties = true, preview = true
        });
        data["constructorCode"]!.Value<string>().Should().Contain(
            "GenCtorWithProperties(string name, int age)");
    }

    [Fact]
    public async Task ExtractInterface_ByTypeName_ListsPublicMembers()
    {
        var data = await CallAndGetDataAsync("extract_interface", new
        {
            typeName = "NameAddressingTarget", interfaceName = "INameAddressingTarget"
        });
        data["className"]!.Value<string>().Should().Be("NameAddressingTarget");
        data["interfaceCode"]!.Value<string>().Should().Contain("UniqueComputeValue");
    }

    [Fact]
    public async Task ChangeSignature_OverloadedByNameWithoutIndex_ReturnsAmbiguousMatch()
    {
        // NameAddressingTarget.Describe has two overloads; addressing by name
        // without overloadIndex must surface AMBIGUOUS_MATCH through the wire.
        await CallAndGetErrorAsync("change_signature", new
        {
            typeName = "NameAddressingTarget",
            memberName = "Describe",
            changes = new object[] { new { action = "rename", name = "count", newName = "n" } },
            preview = true
        }, codeContains: ErrorCodes.AmbiguousMatch);
    }
}
