using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// When remove_unused_code orphans the first using (its only consumer was a removed dead
// member), removing it with KeepNoTrivia must not delete the file header that using
// carries. RemoveNewlyUnusedUsingsAsync routes the survivors through PinHeaderTrivia so the
// header stays at the top. (remove_unused_code is compute-only — read updatedContent.)
public class RemoveUnusedHeaderTriviaTests
{
    [Fact]
    public async Task RemovingDeadMember_ThatOrphansHeaderBearingFirstUsing_KeepsHeaderAtTop()
    {
        const string code = @"// Copyright 2026 Example
using System.Text;
using System.Collections.Generic;

namespace Demo
{
    public class C
    {
        private StringBuilder _dead = new();
        public List<int> Live() => new();
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.RemoveUnusedCodeAsync(
            projectName: "TestProject", symbolNames: new List<string> { "_dead" }))["data"]!;
        var file = (data["files"] as JArray)!.Single();

        file["removedUsings"]!.Value<int>().Should().Be(1,
            "removing _dead orphans its only consumer, using System.Text");

        var updated = file["updatedContent"]!.Value<string>()!;
        updated.Should().StartWith("// Copyright 2026 Example",
            "the file header must survive at the top when the using carrying it is removed");
        updated.Should().NotContain("using System.Text;", "the orphaned using is removed");
        updated.Should().Contain("using System.Collections.Generic;", "the still-used using stays");
    }
}
