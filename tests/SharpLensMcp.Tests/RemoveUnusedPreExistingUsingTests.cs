using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// remove_unused_code must not delete a PRE-EXISTING unused using just because removing a
// dead member before it shifted its source offset. The newly-vs-pre-existing distinction
// is keyed on the using directive's text, not its offset. (remove_unused_code is
// compute-only; the result carries the would-be updatedContent.)
public class RemoveUnusedPreExistingUsingTests
{
    [Fact]
    public async Task PreExistingUnusedUsingAfterRemovedMember_IsNotDeleted()
    {
        // Removing _dead shifts the offset of the later namespace's `using System.Text;`
        // (pre-existing and already unused) — which must survive.
        const string code = @"using System;

namespace First
{
    public class Holder
    {
        private int _dead;
        public int Live() => 1;
    }
}

namespace Second
{
    using System.Text;

    public class Thing { public int N() => 1; }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.RemoveUnusedCodeAsync(
            projectName: "TestProject", symbolNames: new List<string> { "_dead" }))["data"]!;
        var file = (data["files"] as JArray)!.Single();

        file["removedDeclarations"]!.Value<int>().Should().Be(1, "_dead is removed");
        file["removedUsings"]!.Value<int>().Should().Be(0,
            "the pre-existing unused using must not be counted as newly unused");

        var updated = file["updatedContent"]!.Value<string>()!;
        updated.Should().NotContain("_dead", "the dead field is removed");
        updated.Should().Contain("using System.Text;",
            "a pre-existing unused using is not deleted by removing a dead member");
    }
}
