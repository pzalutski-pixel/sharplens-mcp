using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks the apply-path's introduced-error diff (surfaced as postApplyErrors):
// it compares the WHOLE project compilation (not just changed documents) and keys
// by file path + id + message, so an edit that breaks an untouched file is caught
// and a new error is never suppressed by a pre-existing one elsewhere that shares
// an id+message.
public class IntroducedCompileErrorsScopeTests
{
    [Fact]
    public async Task EditBreakingAnUntouchedFile_IsReported()
    {
        // Renaming the parameter only in the declaration file leaves a named-argument
        // caller in a DIFFERENT, untouched file referring to the old name (CS1739). A
        // changed-documents-only diff would miss it; the whole-project diff catches it.
        var (workspace, documents) = TestHelpers.CreateWorkspaceWithMultipleDocuments(
            ("namespace N { public class C { public int Add(int amount) => amount + 1; } }", "Decl.cs"),
            ("namespace N { public class D { public int Use() => new C().Add(amount: 5); } }", "Caller.cs"));
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var solution = workspace.CurrentSolution;
        var declText = (await solution.GetDocument(documents[0].Id)!.GetTextAsync()).ToString();
        var renamed = declText
            .Replace("int amount", "int value")
            .Replace("amount + 1", "value + 1");
        var newSolution = solution.WithDocumentText(documents[0].Id, SourceText.From(renamed));

        var (ok, _, _, introduced) = await service.ApplyAndReportAsync(newSolution);

        ok.Should().BeTrue("the edit applies (the contract surfaces, it does not refuse)");
        introduced.Should().Contain(
            s => s.Contains("Caller.cs") && s.Contains("CS1739"),
            "the rename breaks the named-argument caller in the untouched file: " + string.Join(" | ", introduced));
        introduced.Should().NotContain(
            s => s.Contains("Decl.cs"),
            "the declaration file itself still compiles after the rename");
    }

    [Fact]
    public async Task NewError_CoincidingWithAPreExistingOneElsewhere_IsNotSuppressed()
    {
        // Bad.cs already fails with CS0103 for 'foo'. Editing Good.cs to also reference
        // 'foo' introduces an IDENTICAL id+message in a different file. A diff keyed by
        // id+message alone would suppress the new one; keying by file path reports it —
        // and never reports the pre-existing Bad.cs error as introduced.
        var (workspace, documents) = TestHelpers.CreateWorkspaceWithMultipleDocuments(
            ("namespace N { public class A { public int X() => foo; } }", "Bad.cs"),
            ("namespace N { public class B { public int Y() => 1; } }", "Good.cs"));
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var solution = workspace.CurrentSolution;
        var goodText = (await solution.GetDocument(documents[1].Id)!.GetTextAsync()).ToString();
        var broken = goodText.Replace("=> 1;", "=> foo;");
        var newSolution = solution.WithDocumentText(documents[1].Id, SourceText.From(broken));

        var (_, _, _, introduced) = await service.ApplyAndReportAsync(newSolution);

        introduced.Should().Contain(
            s => s.Contains("Good.cs") && s.Contains("CS0103"),
            "the new CS0103 in Good.cs must be reported even though Bad.cs already has the same error: "
            + string.Join(" | ", introduced));
        introduced.Should().NotContain(
            s => s.Contains("Bad.cs"),
            "the pre-existing Bad.cs error is not introduced by this edit");
    }
}
