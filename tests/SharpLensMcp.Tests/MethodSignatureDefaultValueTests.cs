using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_method_signature must render parameter default values as the C# literal a
// caller would write — `true`/`false`, quoted strings, and `null` — not the
// boxed object.ToString() (which produced `True`/`False` and an ambiguous null).
public class MethodSignatureDefaultValueTests
{
    private const string Code = @"
namespace Production
{
    public enum Mode { Off, On }

    public class Defaults
    {
        public void M(bool flag = true, string label = ""hi"", string? note = null, int count = 3, Mode mode = Mode.On) { }
    }
}";

    [Fact]
    public async Task DefaultValues_AreRenderedAsCSharpLiterals()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(Code);
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.GetMethodSignatureAsync("Defaults", "M"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var byName = (json["data"]!["parameters"] as JArray)!
            .ToDictionary(p => p["name"]!.Value<string>()!, p => p["defaultValue"]?.Value<string>());

        byName["flag"].Should().Be("true", "a bool default must be the C# literal `true`, not boxed `True`");
        byName["label"].Should().Be("\"hi\"", "a string default must be quoted");
        byName["note"].Should().Be("null", "an explicit null default must be the literal `null`, not a missing value");
        byName["count"].Should().Be("3");
        byName["mode"].Should().Be("Mode.On",
            "an enum default must render as its member name, not the boxed underlying int (1)");
    }
}
