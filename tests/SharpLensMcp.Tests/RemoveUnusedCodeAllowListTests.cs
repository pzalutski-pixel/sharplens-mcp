using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// remove_unused_code with an explicit allow-list must evaluate every named symbol,
// not cap at maxSymbols and then mislabel the overflow as "not reported unused".
public class RemoveUnusedCodeAllowListTests
{
    private const string Code = @"
namespace Demo
{
    public class Holder
    {
        public void DeadA() { }
        public void DeadB() { }
        public void DeadC() { }
    }
}";

    [Fact]
    public async Task AllowListBeyondMaxSymbols_AllRemovable_NoneMislabeled()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Holder.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.RemoveUnusedCodeAsync(
            projectName: "TestProject",
            symbolNames: new List<string> { "DeadA", "DeadB", "DeadC" },
            maxSymbols: 2);
        var data = JObject.FromObject(result)["data"]!;

        var removedNames = (data["removed"] as JArray)!
            .Select(r => r["name"]!.Value<string>()).ToList();
        removedNames.Should().BeEquivalentTo(new[] { "DeadA", "DeadB", "DeadC" },
            "all three allow-listed unused methods are removable even though maxSymbols is 2");

        var skippedReasons = (data["skipped"] as JArray)!
            .Select(s => s["reason"]!.Value<string>()).ToList();
        skippedReasons.Should().NotContain(r => r!.Contains("Not reported unused"),
            "no allow-listed unused symbol may be mislabeled merely for falling beyond the cap");
    }
}
