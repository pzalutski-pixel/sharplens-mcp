using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests.Consistency;

// Locks the consistency base's normalizers, so an invariant built on them compares
// the forms it claims to: locations as "path:line:column" in canonical order, names as
// a sorted distinct set — and the loading contract.
public class CrossToolConsistencyTestBaseTests : CrossToolConsistencyTestBase
{
    [Fact]
    public void Locations_ProjectsAndOrdersCanonically()
    {
        var items = JArray.Parse("""
            [
              { "location": { "filePath": "src/b.cs", "line": 2, "column": 0 } },
              { "location": { "filePath": "src\\a.cs", "line": 9, "column": 4 } },
              { "location": { "filePath": "src/a.cs", "line": 9, "column": 1 } }
            ]
            """);

        Locations(items, i => i["location"]!).Should().Equal("src/a.cs:9:1", "src/a.cs:9:4", "src/b.cs:2:0");
    }

    [Fact]
    public void Names_AreDistinctAndOrdinallySorted()
    {
        var items = JArray.Parse("""[ { "n": "b" }, { "n": "B" }, { "n": "a" }, { "n": "b" } ]""");

        Names(items, i => i["n"]!.Value<string>()!).Should().Equal("B", "a", "b");
    }

    [Fact]
    public async Task CallingBeforeLoad_Throws()
    {
        var call = () => CallDataAsync("health_check", new { });

        await call.Should().ThrowAsync<InvalidOperationException>().WithMessage("Call Load(workspace) before calling a tool");
    }

    [Fact]
    public async Task CallDataAsync_ReturnsTheDataOfTheLoadedWorkspace()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode("namespace Probe { public class P { } }", "P.cs");
        Load(workspace);

        var data = await CallDataAsync("health_check", new { });

        data["status"]!.Value<string>().Should().Be("Ready");
        data["solution"]!["projects"]!.Value<int>().Should().Be(1);
    }
}
