using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_tests resolves its target through the member resolution, so a type name that wins
// over a same-named framework type carries the shadow notice, at both levels (ToolPath).
public class FindTestsNoticeTests
{
    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task ShadowingTypeName_CarriesTheNotice(ToolPath toolPath)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(CallFlowSources.Flow, CallFlowSources.FileName);
        var runner = new ToolPathRunner(toolPath, workspace);

        var data = await runner.DataAsync("find_tests", new { typeName = "Random", memberName = "Roll" },
            s => s.FindTestsAsync("Random", "Roll"));

        data["target"]!.Value<string>().Should().Be("Flow.Random.Roll()");
        ((JArray)data["coveringTests"]!).Should().BeEmpty();
        data["notice"]!.Value<string>().Should().Be(
            "'Random' resolved to Flow.Random; also matches System.Random — pass the fully-qualified name to target it.");
    }
}
