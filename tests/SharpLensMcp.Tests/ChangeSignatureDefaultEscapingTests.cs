using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// change_signature renders an existing parameter's default value as a valid C#
// literal: a char like '\'' must come back escaped as '\'' , not the invalid '''.
// Read-only: asserts the preview's rendered default, not a disk write.
public class ChangeSignatureDefaultEscapingTests
{
    private const string Code = @"
namespace Probe
{
    public class EscapeHost
    {
        public void M(int a, char delimiter = '\'')
        {
        }
    }
}";

    [Fact]
    public async Task ChangeSignature_RendersCharDefaultWithProperEscaping()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // Order keeps the optional `delimiter` last (reordering it before the
        // required `a` would not compile, CS1737); the char default must still
        // render escaped in oldParameters and the new signature.
        var changes = new List<SignatureChange>
        {
            new() { Action = "reorder", Order = new List<string> { "a", "delimiter" } }
        };
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null, changes, preview: true,
            typeName: "EscapeHost", memberName: "M");

        var json = JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var data = json["data"]!;
        var delimiterDefault = (data["oldParameters"] as JArray)!
            .First(p => p["name"]!.Value<string>() == "delimiter")["defaultValue"]!.Value<string>();
        delimiterDefault.Should().Be("'\\''",
            "the single-quote char must render as the escaped literal '\\'', not the invalid '''");

        data["newSignature"]!.Value<string>().Should().Contain("'\\''",
            "the rewritten signature must carry the escaped default");
    }
}
