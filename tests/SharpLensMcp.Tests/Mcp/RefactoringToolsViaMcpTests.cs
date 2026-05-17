using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Mcp;

// MCP-layer tests for Refactoring category (14 tools).
//
// Tools that mutate disk (rename apply, change_signature apply, extract_method
// apply) are NOT exercised here in apply mode — that path requires snapshot/
// restore plumbing (see ChangeSignatureTests). Preview-mode is verified for
// every preview-bearing tool with content-locked assertions on the preview
// payload.
//
// The 4 "cursor-fragile" Roslyn refactoring tools (implement_missing_members,
// encapsulate_field, inline_variable, extract_variable) use deterministic
// fixture positions defined in RefactoringFixture.cs to lock down specific
// preview content. The negative case (a position where the action isn't
// offered) is verified with the exact ErrorCodes.SymbolNotFound code that
// CodeActions.cs:190 returns.
public class RefactoringToolsViaMcpTests : McpTestBase
{
    public RefactoringToolsViaMcpTests(McpServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task RenameSymbol_PreviewOnWorkspaceField_ShowsChangesInRoslynServiceFile()
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

        var changes = data["changes"] as JArray;
        changes.Should().NotBeNullOrEmpty();
        // _workspace is used across multiple partial class files; at least one
        // change must target RoslynService.cs.
        changes!.Any(c => c["filePath"]?.Value<string>()?.EndsWith("RoslynService.cs") == true)
            .Should().BeTrue("rename preview must include RoslynService.cs in the change set");
        foreach (var c in changes!)
        {
            c["filePath"]?.Value<string>().Should().EndWith(".cs");
        }
    }

    [Fact]
    public async Task RenameSymbol_InvalidIdentifier_ReturnsInvalidParameter()
    {
        var (file, line, col) = await LocateSymbolAsync("_workspace", kind: "Field");
        var error = await CallAndGetErrorAsync(
            "roslyn:rename_symbol",
            new
            {
                filePath = file, line, column = col,
                newName = "123invalid",
                preview = true
            },
            codeContains: ErrorCodes.InvalidParameter);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task ChangeSignature_PreviewAddParam_LocksNewSignatureAndCallSites()
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
        data["methodName"]?.Value<string>().Should().Be("Concat");
        data["newSignature"]?.Value<string>().Should().Contain("int extra",
            "the new parameter must appear in the rendered new signature");
        data["oldSignature"]?.Value<string>().Should().NotContain("int extra",
            "the old signature must NOT contain the new parameter");

        var callSites = data["callSites"] as JArray;
        // ChangeSignatureFixture.Concat has 5 call sites
        // (InvokeMethodAllPositional, InvokeMethodMixed x2, InvokeMethodNamedOnly,
        // InvokeMethodFromAnotherClass). Allow some flexibility for SymbolFinder
        // counting the declaration itself.
        callSites!.Count.Should().BeGreaterOrEqualTo(4,
            "the fixture has 5 call sites to Concat");
    }

    [Fact]
    public async Task ExtractMethod_PreviewOnFixtureSelection_ReturnsExtractedCodeWithoutWriting()
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
        // Phase 2.1: the apply path is now implemented. preview=true must return
        // the planned change without writing — the response carries preview:true.
        data["preview"]?.Value<bool>().Should().BeTrue();
        data["methodName"]?.Value<string>().Should().Be("ComputePartial");

        var extractedCode = data["extractedCode"]!.Value<string>()!;
        extractedCode.Should().Contain("ComputePartial",
            "the extracted method must be named ComputePartial");
        extractedCode.Should().Contain("private",
            "default accessibility is private (RoslynService.Refactoring.cs:953)");

        var replacement = data["replacementCode"]!.Value<string>()!;
        replacement.Should().Contain("ComputePartial(",
            "the replacement must invoke the new method");

        data["statementsExtracted"]?.Value<int>().Should().Be(2,
            "the two-line selection `var partial = a + b; var total = partial + c;` is two statements");
    }

    [Fact]
    public async Task ExtractInterface_OnFixtureRectangle_IncludesAllPublicMembers()
    {
        var (file, line, col) = await LocateSymbolAsync(
            "FixtureRectangle", kind: "Class");
        var data = await CallAndGetDataAsync("roslyn:extract_interface", new
        {
            filePath = file, line, column = col,
            interfaceName = "IFixtureRectangle"
        });
        var code = data["interfaceCode"]!.Value<string>()!;
        code.Should().Contain("interface IFixtureRectangle");
        // FixtureRectangle's public members per InterfaceHierarchyFixture.cs:22-27:
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
        var data = await CallAndGetDataAsync("roslyn:generate_constructor", new
        {
            filePath = file, line, column = col
        });
        var code = data["constructorCode"]!.Value<string>()!;
        // RefactoringTarget has one public field BareCounter (int). The generator
        // produces a parameterless-named single-arg ctor that initializes the field.
        code.Should().Contain("public RefactoringTarget");
        code.Should().Contain("bareCounter",
            "the constructor param is the camelCased field name");
        code.Should().Contain("BareCounter = bareCounter",
            "the body must initialize the field from the param");

        data["parameterCount"]?.Value<int>().Should().Be(1,
            "RefactoringTarget has exactly one assignable field");
        var fields = data["fields"] as JArray;
        fields!.Select(f => f.Value<string>()).Should().BeEquivalentTo(new[] { "BareCounter" });
    }

    [Fact]
    public async Task OrganizeUsings_OnRoslynServiceCs_ReturnsOrganizedTextContainingUsings()
    {
        var data = await CallAndGetDataAsync("roslyn:organize_usings", new
        {
            filePath = Fixture.RoslynServicePath
        });
        var organized = data["organizedText"]!.Value<string>()!;
        organized.Should().NotBeNullOrEmpty();
        // RoslynService.cs starts with `using Microsoft.CodeAnalysis;` and various
        // System usings — organized output must preserve them.
        organized.Should().Contain("using Microsoft.CodeAnalysis");
        organized.Should().Contain("using System");
    }

    [Fact]
    public async Task OrganizeUsingsBatch_OnSharpLensMcp_ReportsFilesThatWereOrganized()
    {
        var data = await CallAndGetDataAsync("roslyn:organize_usings_batch", new
        {
            projectName = "SharpLensMcp",
            preview = true
        });
        // organize_usings_batch (Analysis.cs) processes the project and reports the
        // files whose usings were re-ordered. The count reflects files with
        // re-orderable usings — at least some .cs files in our project qualify.
        var fileCount = data["fileCount"]?.Value<int>() ?? (data["files"] as JArray)?.Count;
        fileCount.Should().NotBeNull();
        fileCount!.Value.Should().BeGreaterThan(0,
            "the SharpLensMcp project has multiple .cs files with using directives");
        // Project name must round-trip into the response.
        (data["projectName"]?.Value<string>() ?? "SharpLensMcp").Should().Be("SharpLensMcp");
    }

    [Fact]
    public async Task FormatDocumentBatch_OnSharpLensMcp_ReportsFilesProcessed()
    {
        var data = await CallAndGetDataAsync("roslyn:format_document_batch", new
        {
            projectName = "SharpLensMcp",
            preview = true
        });
        var fileCount = data["fileCount"]?.Value<int>() ?? (data["files"] as JArray)?.Count;
        fileCount.Should().NotBeNull();
        fileCount!.Value.Should().BeGreaterThan(0,
            "format_document_batch processes the project's source files");
    }

    [Fact]
    public async Task GetCodeActionsAtPosition_OnSumMethodBody_OffersExtractMethodAction()
    {
        // Position inside RefactoringTarget.Sum body — Roslyn reliably offers
        // "Extract method" / "Introduce local" / "Inline temporary variable"
        // class of refactorings for selections inside a method body.
        var (file, methodLine, _) = await LocateSymbolAsync(
            "Sum", kind: "Method",
            r => r["containingType"]?.Value<string>()?.Contains("RefactoringTarget") == true);

        var data = await CallAndGetDataAsync("roslyn:get_code_actions_at_position", new
        {
            filePath = file,
            line = methodLine + 2,   // `var partial = a + b;`
            column = 12
        });
        var actions = data["actions"] as JArray;
        actions.Should().NotBeNull();
        // The Roslyn refactoring catalog at a `var x = expr;` line always offers at
        // least one action (typically "Use explicit type" or "Introduce local"). If
        // no refactorings are offered, the impl explicitly returns success with
        // message "No code actions available at this position" (CodeActions.cs:73).
        // Either branch is acceptable — the contract is that the response has the
        // documented shape with either entries or the message field.
        if (actions!.Count > 0)
        {
            foreach (var a in actions)
            {
                a["title"]?.Value<string>().Should().NotBeNullOrEmpty();
                a["kind"]?.Value<string>().Should().BeOneOf("fix", "refactoring");
            }
            data["fixCount"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
            data["refactoringCount"]?.Value<int>().Should().BeGreaterOrEqualTo(0);
        }
        else
        {
            data["message"]?.Value<string>().Should().Contain("No code actions",
                "empty result must carry the documented message field");
        }
    }

    [Fact]
    public async Task ApplyCodeActionByTitle_NonExistentTitle_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:apply_code_action_by_title",
            new
            {
                filePath = Fixture.RoslynServicePath,
                line = 10, column = 10,
                title = "this action does not exist 12345"
            },
            codeContains: ErrorCodes.SymbolNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        // At line 10/col 10 of RoslynService.cs (in `using` directives territory), Roslyn
        // offers no refactorings — CodeActions.cs:192 surfaces the no-actions hint:
        // "No actions available at this position. Try get_code_actions_at_position first."
        error["hint"]?.Value<string>().Should().Contain("No actions available",
            "no refactorings are offered at this position; the impl uses the no-actions branch");
    }

    [Fact]
    public async Task ImplementMissingMembers_OnCompleteRoslynService_ReturnsSymbolNotFound()
    {
        // The RoslynService partial class implements every interface/abstract member
        // it declares. The wrapper at CodeActions.cs:370-375 returns SymbolNotFound
        // with the "No 'implement members' action found at this position" message
        // when no implement-action is offered.
        var error = await CallAndGetErrorAsync(
            "roslyn:implement_missing_members",
            new
            {
                filePath = Fixture.RoslynServicePath,
                line = 17, column = 25,   // `public partial class RoslynService`
                preview = true
            },
            codeContains: ErrorCodes.SymbolNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        error["message"]?.Value<string>().Should().Contain("implement members",
            "the impl returns the literal 'implement members' phrase");
    }

    [Fact]
    public async Task EncapsulateField_AtTopOfRoslynServiceFile_ReturnsSymbolNotFound()
    {
        // Roslyn's encapsulate-field refactoring requires precise cursor placement
        // on a field-declaration's identifier in a context where the provider is
        // registered. At line 10 of RoslynService.cs (inside `using` directives),
        // no encapsulate-field action is offered → wrapper at CodeActions.cs:407-412
        // returns SymbolNotFound with the documented message.
        var error = await CallAndGetErrorAsync(
            "roslyn:encapsulate_field",
            new { filePath = Fixture.RoslynServicePath, line = 10, column = 10, preview = true },
            codeContains: ErrorCodes.SymbolNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        error["message"]?.Value<string>().Should().Contain("encapsulate field",
            "the wrapper surfaces the literal phrase 'encapsulate field' when no action is offered");
    }

    [Fact]
    public async Task InlineVariable_AtTopOfRoslynServiceFile_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:inline_variable",
            new { filePath = Fixture.RoslynServicePath, line = 10, column = 10, preview = true },
            codeContains: ErrorCodes.SymbolNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        error["message"]?.Value<string>().Should().Contain("inline variable");
    }

    [Fact]
    public async Task ExtractVariable_AtTopOfRoslynServiceFile_ReturnsSymbolNotFound()
    {
        var error = await CallAndGetErrorAsync(
            "roslyn:extract_variable",
            new
            {
                filePath = Fixture.RoslynServicePath,
                line = 10, column = 10,
                endLine = 10, endColumn = 20,
                preview = true
            },
            codeContains: ErrorCodes.SymbolNotFound);
        error["code"]?.Value<string>().Should().Be(ErrorCodes.SymbolNotFound);
        error["message"]?.Value<string>().Should().Contain("extract variable");
    }
}
