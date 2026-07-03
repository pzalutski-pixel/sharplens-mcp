using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// generate_equality_members compares stored state once: a manual settable property and
// the user field it wraps are the same storage, so only the property is compared — the
// backing field must not be added as a second, redundant comparison.
public class GenerateEqualityBackingFieldTests
{
    [Fact]
    public async Task ManualPropertyOverUserField_ComparesTheStorageOnce()
    {
        const string code = @"
namespace Production
{
    public class Box
    {
        private int _x;
        public int X { get => _x; set => _x = value; }
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Box.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var result = await service.GenerateEqualityMembersAsync(
            filePath: null, line: null, column: null, typeName: "Box");
        var data = JObject.FromObject(result)["data"]!;

        var members = (data["membersCompared"] as JArray)!.Select(m => m.Value<string>()).ToList();
        members.Should().Contain("X", "the property is the compared member");
        members.Should().NotContain("_x",
            "_x is the property's backing field — comparing it too would double-count the storage");
    }
}
