using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// get_exception_flow against ExceptionFlowFixture, addressed by name (which
// also exercises the shared name-based addressing path). Every assertion pins
// a specific exception type and a specific line resolved from the fixture
// source, never just "something escaped".
public class ExceptionFlowTests : RoslynServiceTestBase
{
    private string FixturePath => Path.Combine(
        Path.GetDirectoryName(SolutionPath)!, "tests", "SharpLensMcp.Tests", "Fixtures", "ExceptionFlowFixture.cs");

    private int FixtureLineOf(string marker)
    {
        var lines = File.ReadAllLines(FixturePath);
        var index = Array.FindIndex(lines, l => l.Contains(marker));
        index.Should().BeGreaterThan(-1, $"the fixture must contain '{marker}'");
        return index;
    }

    private async Task<JToken> RunOn(string memberName, int? maxDepth = null)
    {
        var result = maxDepth == null
            ? await Service.GetExceptionFlowAsync(null, null, null,
                typeName: "ExceptionFlowTarget", memberName: memberName)
            : await Service.GetExceptionFlowAsync(null, null, null, maxDepth: maxDepth.Value,
                typeName: "ExceptionFlowTarget", memberName: memberName);
        return GetData(result);
    }

    [Fact]
    public async Task Catcher_ReportsCaughtAtItsCatch_WithThrowSiteFromCallee()
    {
        var data = await RunOn("CatcherB");

        (data["escaping"] as JArray)!.Should().BeEmpty(
            "CatcherB catches the only exception its callee throws");

        var caught = (data["caught"] as JArray)!;
        caught.Count.Should().Be(1);
        caught[0]["exceptionType"]!.Value<string>().Should().Be("System.InvalidOperationException");
        caught[0]["catchLocation"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("catch (InvalidOperationException)"),
            "the catch location must be CatcherB's own catch clause");
        caught[0]["thrownFrom"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("throw new InvalidOperationException(\"a\")"),
            "the origin must be ThrowerA's throw statement");
    }

    [Fact]
    public async Task BareCaller_ReportsEscaping_WithCalleeThrowSite()
    {
        var data = await RunOn("BareCallerC");

        (data["caught"] as JArray)!.Should().BeEmpty("BareCallerC has no try/catch");

        var escaping = (data["escaping"] as JArray)!;
        escaping.Count.Should().Be(1);
        escaping[0]["exceptionType"]!.Value<string>().Should().Be("System.InvalidOperationException");
        escaping[0]["sampleThrowSite"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("throw new InvalidOperationException(\"a\")"));
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse(
            "depth 3 fully covers the one-hop chain");
    }

    [Fact]
    public async Task Rethrower_BareThrow_LetsTheOriginalEscape_AndAbsorbsNothing()
    {
        var data = await RunOn("RethrowerD");

        // The catch rethrows everything it catches, so it absorbs nothing...
        (data["caught"] as JArray)!.Should().BeEmpty(
            "a catch whose block rethrows lets the caught exception continue");

        // ...and the callee's exception escapes D unchanged, from its original throw.
        var escaping = (data["escaping"] as JArray)!;
        escaping.Count.Should().Be(1);
        escaping[0]["exceptionType"]!.Value<string>().Should().Be("System.InvalidOperationException");
        escaping[0]["sampleThrowSite"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("throw new InvalidOperationException(\"a\")"),
            "the escaping exception is the one ThrowerA throws, not a new one at the rethrow");
    }

    [Fact]
    public async Task LocalCatch_OwnThrowAbsorbed_NothingEscapes()
    {
        var data = await RunOn("LocalCatchE");

        (data["escaping"] as JArray)!.Should().BeEmpty();
        var caught = (data["caught"] as JArray)!;
        caught.Count.Should().Be(1);
        caught[0]["exceptionType"]!.Value<string>().Should().Be("System.ArgumentException");
        caught[0]["thrownFrom"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("throw new ArgumentException(\"e\")"));
    }

    [Fact]
    public async Task DepthOne_DoesNotDescend_AndReportsTruncation()
    {
        var data = await RunOn("CatcherB", maxDepth: 1);

        (data["escaping"] as JArray)!.Should().BeEmpty();
        (data["caught"] as JArray)!.Should().BeEmpty(
            "depth 1 must not analyze ThrowerA's body, so nothing reaches B's catch");
        data["truncatedByDepth"]!.Value<bool>().Should().BeTrue(
            "the unfollowed in-source callee must be reported as truncation");
    }

    [Fact]
    public async Task ConstructorThrow_FollowedThroughNew_Escapes()
    {
        var data = await RunOn("ConstructsThrowingCtorF");

        (data["caught"] as JArray)!.Should().BeEmpty("there is no try/catch around the new");
        var escaping = (data["escaping"] as JArray)!;
        escaping.Count.Should().Be(1);
        escaping[0]["exceptionType"]!.Value<string>().Should().Be("System.InvalidOperationException");
        escaping[0]["sampleThrowSite"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("throw new InvalidOperationException(\"ctor\")"),
            "the throw site must be inside the constructor body the `new` invokes");
        data["truncatedByDepth"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task PropertyGetterThrow_FollowedThroughRead_Escapes()
    {
        var data = await RunOn("ReadsThrowingPropertyG");

        (data["caught"] as JArray)!.Should().BeEmpty("the property read is not guarded");
        var escaping = (data["escaping"] as JArray)!;
        escaping.Count.Should().Be(1);
        escaping[0]["exceptionType"]!.Value<string>().Should().Be("System.FormatException");
        escaping[0]["sampleThrowSite"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("throw new FormatException(\"getter\")"),
            "the throw site must be inside the getter the read dispatches to");
    }

    [Fact]
    public async Task MaxDepthOutOfRange_ReturnsInvalidParameter()
    {
        var result = await Service.GetExceptionFlowAsync(null, null, null, maxDepth: 0,
            typeName: "ExceptionFlowTarget", memberName: "CatcherB");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("between 1 and 10");
    }
}
