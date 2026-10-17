using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// The shared apply path surfaces compile errors an edit introduces (R4): a tool must
// not report clean success when its edit left code that does not compile. Verified
// through change_signature removing a parameter its method body still uses.
public class ApplyPathPostApplyErrorsTests
{
    private const string Code = @"
namespace Demo
{
    public class C
    {
        public int Add(int amount) => amount + 1;
    }
}";

    private static RoslynService Loaded()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code, "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        return service;
    }

    [Fact]
    public async Task EditThatBreaksCompilation_SurfacesPostApplyErrors()
    {
        var service = Loaded();

        // Removing a body-used parameter leaves the body referencing it.
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "remove", Name = "amount" } },
            preview: false, typeName: "C", memberName: "Add");
        var root = JObject.FromObject(result);

        // The surface contract: the edit IS applied (success:true / applied:true) and the
        // breakage is disclosed in postApplyErrors. isError:true is set at the MCP envelope
        // (asserted in the dispatcher/wire test), not in this in-process envelope.
        root["success"]!.Value<bool>().Should().BeTrue(
            "the edit applies under the surface contract; the breakage is disclosed, not refused");
        var data = root["data"]!;
        data["applied"]!.Value<bool>().Should().BeTrue("the edit was applied");

        var errors = data["postApplyErrors"] as JArray;
        errors.Should().NotBeNull("the edit introduced a compile error and must not report clean success");
        errors!.Select(e => e.Value<string>()).Should().ContainSingle(s => s!.EndsWith(
                " (line 5) CS0103: The name 'amount' does not exist in the current context"),
            "the body still references the removed parameter 'amount'");
    }

    [Fact]
    public async Task CleanEdit_HasNoPostApplyErrors()
    {
        var service = Loaded();

        // Appending an optional parameter keeps the body valid.
        var result = await service.ChangeSignatureAsync(
            filePath: null, line: null, column: null,
            new List<SignatureChange> { new() { Action = "add", Name = "extra", Type = "int", DefaultValue = "0" } },
            preview: false, typeName: "C", memberName: "Add");
        var data = JObject.FromObject(result)["data"]!;

        var errors = data["postApplyErrors"];
        (errors == null || errors.Type == JTokenType.Null).Should().BeTrue(
            "a compiling edit reports no post-apply errors");
    }
}
