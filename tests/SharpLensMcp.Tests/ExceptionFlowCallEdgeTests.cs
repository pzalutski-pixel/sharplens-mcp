using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_exception_flow over the CallFlowSources fixture (namespace Flow.Ex), each behavior at
// both levels (ToolPath): the walk follows the same call edges as the call tools (extension
// methods, partial implementations, the setter a ++ runs, indexers, dispatch to the
// implementations the receiver can reach, flagged viaDispatch), a local function's throw
// is matched against the try/catch around its call sites, a primary constructor runs only
// its initializers, and a bare `throw;` lets the original exception escape.
public class ExceptionFlowCallEdgeTests
{
    private static ToolPathRunner Load(ToolPath toolPath)
    {
        var (workspace, _) = TestHelpers.CreateWorkspaceWithCode(CallFlowSources.Flow, CallFlowSources.FileName);
        return new ToolPathRunner(toolPath, workspace);
    }

    private static Task<JToken> FlowAsync(ToolPathRunner runner, string typeName, string memberName) =>
        runner.DataAsync("get_exception_flow", new { typeName, memberName },
            s => s.GetExceptionFlowAsync(null, null, null, typeName: typeName, memberName: memberName));

    private static List<(string type, int line, bool viaDispatch)> Escaping(JToken data) =>
        ((JArray)data["escaping"]!)
            .Select(e => (e["exceptionType"]!.Value<string>()!, e["sampleThrowSite"]!["line"]!.Value<int>(),
                e["viaDispatch"]!.Value<bool>()))
            .ToList();

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task PrimaryConstructor_RunsOnlyItsInitializers_NotTheTypesOtherThrows(ToolPath toolPath)
    {
        var data = await FlowAsync(Load(toolPath), "WidgetUser", "Make");

        ((JArray)data["escaping"]!).Should().BeEmpty("new Widget(1) runs no throw; Boom is never called");
        ((JArray)data["caught"]!).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task LocalFunctionThrow_EscapesThroughItsCallSite(ToolPath toolPath)
    {
        var data = await FlowAsync(Load(toolPath), "LocalThrow", "Run");

        Escaping(data).Should().Equal(
            ("System.ArgumentException", CallFlowSources.LineOf("ArgumentException(\"local\")"), false));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task LocalFunctionThrow_IsCaughtByTheTryAroundItsCallSite(ToolPath toolPath)
    {
        var data = await FlowAsync(Load(toolPath), "LocalCaught", "Run");

        ((JArray)data["escaping"]!).Should().BeEmpty("the only call of Local sits in a try that catches ArgumentException");
        var caught = ((JArray)data["caught"]!).Single();
        caught["exceptionType"]!.Value<string>().Should().Be("System.ArgumentException");
        caught["catchLocation"]!["line"]!.Value<int>().Should().Be(CallFlowSources.LineOf("catch (System.ArgumentException) { }"));
        caught["thrownFrom"]!["line"]!.Value<int>().Should().Be(CallFlowSources.LineOf("ArgumentException(\"caught local\")"));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task ExtensionCall_FollowsIntoTheExtensionMethod(ToolPath toolPath)
    {
        var data = await FlowAsync(Load(toolPath), "ExtThrowUser", "Go");

        Escaping(data).Should().Equal(("System.FormatException", CallFlowSources.LineOf("FormatException(\"ext\")"), false));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task BareRethrow_LetsTheOriginalExceptionEscape_AndIsNotACatch(ToolPath toolPath)
    {
        var data = await FlowAsync(Load(toolPath), "Rethrower", "M");

        Escaping(data).Should().Equal(
            ("System.InvalidOperationException", CallFlowSources.LineOf("InvalidOperationException(\"inner\")"), false));
        ((JArray)data["caught"]!).Should().BeEmpty("the catch rethrows what it catches, so it absorbs nothing");
    }

    [Theory]
    [InlineData(ToolPath.Service, "IncHost", "Bump", "System.NotSupportedException", "NotSupportedException(\"set\")")]
    [InlineData(ToolPath.Dispatcher, "IncHost", "Bump", "System.NotSupportedException", "NotSupportedException(\"set\")")]
    [InlineData(ToolPath.Service, "Indexed", "Get", "System.IndexOutOfRangeException", "IndexOutOfRangeException(\"index\")")]
    [InlineData(ToolPath.Dispatcher, "Indexed", "Get", "System.IndexOutOfRangeException", "IndexOutOfRangeException(\"index\")")]
    [InlineData(ToolPath.Service, "PartialThrow", "Fire2", "System.TimeoutException", "TimeoutException(\"partial\")")]
    [InlineData(ToolPath.Dispatcher, "PartialThrow", "Fire2", "System.TimeoutException", "TimeoutException(\"partial\")")]
    public async Task AccessorIndexerAndPartialCalls_FollowTheBodyThatRuns(
        ToolPath toolPath, string typeName, string memberName, string exceptionType, string throwMarker)
    {
        var data = await FlowAsync(Load(toolPath), typeName, memberName);

        Escaping(data).Should().Equal((exceptionType, CallFlowSources.LineOf(throwMarker), false));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task InterfaceCall_IncludesTheImplementationsThrows_FlaggedViaDispatch(ToolPath toolPath)
    {
        var data = await FlowAsync(Load(toolPath), "IfaceUser", "Use");

        Escaping(data).Should().Equal(
            ("System.DivideByZeroException", CallFlowSources.LineOf("DivideByZeroException(\"dispatch\")"), true));
        ((JArray)data["analyzedMembers"]!).Select(m => m.Value<string>()).Should().Equal(
            "Flow.Ex.IfaceUser.Use(Flow.Ex.IThrower)", "Flow.Ex.ThrowerImpl.Do()");
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task ShadowingTypeName_CarriesTheNotice(ToolPath toolPath)
    {
        var data = await FlowAsync(Load(toolPath), "Random", "Roll");

        data["notice"]!.Value<string>().Should().Be(
            "'Random' resolved to Flow.Random; also matches System.Random — pass the fully-qualified name to target it.");
    }
}
