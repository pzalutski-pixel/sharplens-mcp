using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// A stack frame for a nested type renders the container with `+`
// (Outer+Inner.Method). It must resolve to the nested method's declaration.
public class StackTraceNestedTypeTests
{
    [Fact]
    public async Task NestedTypeFrame_ResolvesViaMetadataName()
    {
        const string code = @"
namespace NestNs
{
    public class Outer
    {
        public class Inner
        {
            public void Boom() { }
        }
    }
}";
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(code, "Nested.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        var json = JObject.FromObject(await service.ResolveStackTraceAsync("   at NestNs.Outer+Inner.Boom()"));
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        var frames = (json["data"]!["frames"] as JArray)!;
        var frame = frames.Single(f => f["frameKind"]!.Value<string>() != "text");
        frame["resolved"]!.Value<bool>().Should().BeTrue(
            "a nested-type frame must resolve via the type's metadata name");
        frame["symbol"]!.Value<string>().Should().Be("NestNs.Outer.Inner.Boom");
    }
}
