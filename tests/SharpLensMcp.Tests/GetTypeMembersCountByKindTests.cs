using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_type_members' memberCounts must reflect ALL members and sum to totalMembers,
// even when the returned members list is capped by maxResults.
public class GetTypeMembersCountByKindTests
{
    private const string Code = @"
namespace Demo
{
    public class Wide
    {
        public void A() { }
        public void B() { }
        public void C() { }
        public int P { get; set; }
        public int Q { get; set; }
    }
}";

    [Fact]
    public async Task MemberCounts_SumToTotalMembers_EvenWhenPageIsCapped()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "Wide.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var data = JObject.FromObject(await service.GetTypeMembersAsync("Wide", maxResults: 2))["data"]!;

        var total = data["totalMembers"]!.Value<int>();
        (data["members"] as JArray)!.Count.Should().Be(2, "maxResults caps the returned page");
        total.Should().BeGreaterThan(2, "there are more members than the page, so the counts could diverge");

        var counts = (data["memberCounts"] as JObject)!;
        counts.Properties().Sum(p => p.Value.Value<int>()).Should().Be(total,
            "per-kind counts cover every member and sum to totalMembers, not just the returned page");
    }
}
