using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// change_signature must not drop the surplus positional arguments a params tail
// lets a call site pass. Read-only: asserts the preview's computed
// transformedCallSites, not a disk write.
public class ChangeSignatureParamsTailTests
{
    private const string Code = @"
namespace Probe
{
    public class ParamsHost
    {
        public int Sum(int seed, params int[] rest) => seed;
        public int Call() => Sum(1, 2, 3, 4);
    }
}";

    [Fact]
    public async Task ChangeSignature_PreservesSurplusParamsArguments()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var changes = new List<SignatureChange>
        {
            new() { Action = "rename", Name = "seed", NewName = "start" }
        };
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null, changes, preview: true,
            typeName: "ParamsHost", memberName: "Sum");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var transformed = (json["data"]!["transformedCallSites"] as JArray)!;
        var call = transformed.FirstOrDefault(t =>
            t["before"]!.Value<string>()!.Replace(" ", "") == "(1,2,3,4)");
        call.Should().NotBeNull("the Sum(1, 2, 3, 4) call site must be among the transformed sites");

        var after = call!["after"]!.Value<string>()!.Replace(" ", "");
        after.Should().Be("(1,2,3,4)",
            "every params argument must survive the rewrite — none of 2, 3, 4 may be dropped");

        // The rewritten declaration must keep the `params` modifier, or the surplus
        // call arguments above would no longer compile.
        json["data"]!["newSignature"]!.Value<string>().Should().Contain("params int[] rest",
            "the params modifier must survive the declaration rewrite");
    }
}
