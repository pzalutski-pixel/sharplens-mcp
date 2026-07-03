using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// resolve_stack_trace frames must carry a uniform key set: a non-frame "text" line
// (exception message, separator) was missing the ambiguousOverload key every resolved
// frame carries.
public class StackTraceFrameShapeTests
{
    [Fact]
    public async Task TextFrames_CarryAmbiguousOverloadKey_LikeOtherFrames()
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(
            "namespace Demo { public class C { public void M() { } } }", "C.cs");
        var service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);

        // An exception-message line (not a stack frame) followed by a method frame.
        const string trace = "System.InvalidOperationException: boom\n   at Demo.C.M()";
        var frames = (JObject.FromObject(await service.ResolveStackTraceAsync(trace))
            ["data"]!["frames"] as JArray)!;

        frames.Should().OnlyContain(f => f["ambiguousOverload"] != null,
            "every frame, including text frames, must carry ambiguousOverload");
        var textFrame = frames.First(f => f["frameKind"]!.Value<string>() == "text");
        textFrame["ambiguousOverload"]!.Value<bool>().Should().BeFalse();
    }
}
