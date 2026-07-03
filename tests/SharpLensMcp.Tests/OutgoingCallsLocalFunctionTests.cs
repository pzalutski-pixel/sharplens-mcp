using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Locks that get_outgoing_calls analyzes the local function under the cursor,
// not the enclosing method. The cursor sits inside a local function whose only
// call differs from the outer method's call.
public class OutgoingCallsLocalFunctionTests
{
    private const string Code = @"
namespace Production;

public class Host
{
    public void Outer()
    {
        OuterHelper();

        void Inner()
        {
            InnerHelper();
        }
    }

    public void OuterHelper() { }
    public void InnerHelper() { }
}";

    [Fact]
    public async Task CursorInLocalFunction_AnalyzesTheLocalFunction_NotTheOuterMethod()
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Code, "Host.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var lines = Code.Split('\n');
        var line = Array.FindIndex(lines, l => l.Contains("void Inner()", StringComparison.Ordinal));
        line.Should().BeGreaterThan(-1);
        var column = lines[line].IndexOf("Inner", StringComparison.Ordinal);

        var result = await service.GetOutgoingCallsAsync(document.FilePath!, line, column);
        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("response envelope must include success");
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var shortNames = (json["data"]!["calls"] as JArray)!
            .Select(c => c["shortName"]!.Value<string>()).ToList();
        shortNames.Should().Contain("Host.InnerHelper",
            "the local function's own call must be reported");
        shortNames.Should().NotContain("Host.OuterHelper",
            "the enclosing method's call must NOT be reported — the cursor is in the local function");
    }
}
