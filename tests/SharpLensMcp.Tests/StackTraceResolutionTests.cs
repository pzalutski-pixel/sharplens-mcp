using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Fixtures;
using Xunit;

namespace SharpLensMcp.Tests;

// resolve_stack_trace: one test feeds a REAL exception captured from the
// fixture at runtime (PDB file/line paths point into this checkout), the
// rest feed hand-written mangled frame shapes that modern runtimes no longer
// emit but logs and older frameworks do.
public class StackTraceResolutionTests : RoslynServiceTestBase
{
    private string FixturePath => Path.Combine(
        Path.GetDirectoryName(SolutionPath)!, "tests", "SharpLensMcp.Tests", "Fixtures", "StackTraceFixture.cs");

    private int FixtureLineOf(string marker)
    {
        var lines = File.ReadAllLines(FixturePath);
        var index = Array.FindIndex(lines, l => l.Contains(marker));
        index.Should().BeGreaterThan(-1, $"the fixture must contain '{marker}'");
        return index;
    }

    private async Task<JArray> FramesOf(string trace, int? contextLines = null)
    {
        var result = contextLines == null
            ? await Service.ResolveStackTraceAsync(trace)
            : await Service.ResolveStackTraceAsync(trace, contextLines.Value);
        return (GetData(result)["frames"] as JArray)!;
    }

    [Fact]
    public async Task RealCapturedTrace_ResolvesFixtureThrowSite_AndLeavesBclFramesUnresolved()
    {
        var trace = StackTraceSource.CaptureTrace();
        trace.Should().Contain("stack-trace-fixture", "the fixture must have thrown");

        var frames = await FramesOf(trace);

        // The exception-message line is preserved as text, not parsed as a frame.
        frames[0]["frameKind"]!.Value<string>().Should().Be("text");
        frames[0]["raw"]!.Value<string>().Should().Contain("stack-trace-fixture");

        var throwFrame = frames.First(f =>
            f["symbol"]?.Value<string>()?.EndsWith("StackTraceSource.ThrowingAsync") == true);
        throwFrame["resolved"]!.Value<bool>().Should().BeTrue();
        throwFrame["location"]!["filePath"]!.Value<string>().Should().EndWith("StackTraceFixture.cs");
        throwFrame["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("throw new InvalidOperationException(\"stack-trace-fixture\")"),
            "the PDB's 1-based throw line must convert to the zero-based convention");
        throwFrame["snippet"]!.Value<string>().Should().Contain("throw new InvalidOperationException");

        // net8 strips awaiter BCL frames AND the rethrow separator for this
        // shape (verified empirically): the whole trace is the message line
        // plus exactly three fixture frames, every one of which must resolve.
        frames.Count.Should().Be(4, "message + ThrowingAsync + Trigger + CaptureTrace");
        frames.Skip(1).Should().OnlyContain(f => f["resolved"]!.Value<bool>() == true,
            "every frame of this trace lives in the loaded solution");

        var triggerFrame = frames.First(f =>
            f["symbol"]?.Value<string>()?.EndsWith("StackTraceSource.Trigger") == true);
        triggerFrame["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("ThrowingAsync().GetAwaiter().GetResult()"),
            "Trigger's frame points at its blocking call site");
    }

    [Fact]
    public async Task SeparatorAndInnerExceptionLines_PassThroughAsText()
    {
        var frames = await FramesOf(
            "System.InvalidOperationException: outer\n" +
            "   at SharpLensMcp.Tests.Fixtures.StackTraceSource.Trigger()\n" +
            "--- End of stack trace from previous location ---\n" +
            "   at SharpLensMcp.Tests.Fixtures.StackTraceSource.CaptureTrace()");

        frames.Count.Should().Be(4);
        frames[0]["frameKind"]!.Value<string>().Should().Be("text");
        frames[2]["frameKind"]!.Value<string>().Should().Be("text");
        frames[2]["raw"]!.Value<string>().Should().Be("--- End of stack trace from previous location ---");
        frames[1]["resolved"]!.Value<bool>().Should().BeTrue(
            "a file-less frame must still resolve by demangled name to the declaration");
        frames[3]["resolved"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task MangledAsyncFrame_DemanglesAndResolvesToTheMethodDeclaration()
    {
        var frames = await FramesOf(
            "   at SharpLensMcp.Tests.Fixtures.StackTraceSource+<ThrowingAsync>d__2.MoveNext()");

        frames.Count.Should().Be(1);
        frames[0]["frameKind"]!.Value<string>().Should().Be("async");
        frames[0]["symbol"]!.Value<string>().Should().Be(
            "SharpLensMcp.Tests.Fixtures.StackTraceSource.ThrowingAsync");
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue();
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("private static async Task ThrowingAsync()"));
    }

    [Fact]
    public async Task MangledLocalFunctionFrame_ResolvesToTheLocalFunction()
    {
        var frames = await FramesOf(
            "   at SharpLensMcp.Tests.Fixtures.StackTraceSource.<UseHelper>g__Stamp|3_0()");

        frames.Count.Should().Be(1);
        frames[0]["frameKind"]!.Value<string>().Should().Be("localFunction");
        frames[0]["symbol"]!.Value<string>().Should().Be(
            "SharpLensMcp.Tests.Fixtures.StackTraceSource.UseHelper.Stamp");
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue();
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("static string Stamp() =>"),
            "the frame must resolve to the local function's own declaration, not just UseHelper");
    }

    [Fact]
    public async Task MangledLambdaFrame_FlagsLambdaAndResolvesContainingMethod()
    {
        var frames = await FramesOf(
            "   at SharpLensMcp.Tests.Fixtures.StackTraceSource.<CaptureTrace>b__0_0()");

        frames.Count.Should().Be(1);
        frames[0]["frameKind"]!.Value<string>().Should().Be("lambda");
        frames[0]["symbol"]!.Value<string>().Should().Be(
            "SharpLensMcp.Tests.Fixtures.StackTraceSource.CaptureTrace");
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue();
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("public static string CaptureTrace()"));
    }

    [Fact]
    public async Task ConstructorFrame_ComposesSingleDotCtorSymbol_AndResolves()
    {
        var frames = await FramesOf(
            "   at SharpLensMcp.Tests.Fixtures.StackTraceOverloads..ctor(System.String label)");

        frames.Count.Should().Be(1);
        frames[0]["symbol"]!.Value<string>().Should().Be(
            "SharpLensMcp.Tests.Fixtures.StackTraceOverloads.ctor",
            "a constructor frame composes a single-dot .ctor symbol, not a doubled dot");
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue();
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("public StackTraceOverloads(string label)"));
    }

    [Fact]
    public async Task OverloadWithTupleParameter_MatchesByArityNotCommaCount()
    {
        // The single tuple-typed parameter carries a top-level comma; counting it
        // as two parameters would resolve the Combine(int, int) overload instead.
        var frames = await FramesOf(
            "   at SharpLensMcp.Tests.Fixtures.StackTraceOverloads.Combine((int, int) pair)");

        frames.Count.Should().Be(1);
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue();
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("public int Combine((int, int) pair)"),
            "a single tuple-typed parameter is arity 1, resolving the tuple overload");
    }

    [Fact]
    public async Task StaleFileLine_OutsideTheNamedMethod_FallsBackToTheDeclaration()
    {
        // A file:line frame whose line (1-based 2 → 0-based 1, the namespace area)
        // does not fall inside Trigger — a stale PDB. The resolver must not trust
        // the bogus line; it falls back to Trigger's real declaration.
        var frames = await FramesOf(
            $"   at SharpLensMcp.Tests.Fixtures.StackTraceSource.Trigger() in {FixturePath}:line 2");

        frames.Count.Should().Be(1);
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue();
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("private static void Trigger()"),
            "a line outside the named method is a stale PDB; resolution falls back to the declaration");
    }

    [Fact]
    public async Task GenericMethodFrame_StripsArityMarker_AndResolves()
    {
        var frames = await FramesOf(
            "   at SharpLensMcp.Tests.Fixtures.StackTraceOverloads.Echo[T](T value)");

        frames.Count.Should().Be(1);
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue(
            "the [T] arity marker must be stripped before the member lookup");
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("public T Echo<T>(T value) =>"));
    }

    [Fact]
    public async Task AmbiguousOverload_WithoutArity_ResolvesButFlagsAmbiguity()
    {
        // A lambda-shaped frame carries no parameter count, so the two Ambiguous
        // overloads cannot be disambiguated — resolution flags the ambiguity.
        var frames = await FramesOf(
            "   at SharpLensMcp.Tests.Fixtures.StackTraceOverloads.<Ambiguous>b__0_0()");

        frames.Count.Should().Be(1);
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue();
        frames[0]["ambiguousOverload"]!.Value<bool>().Should().BeTrue(
            "two same-name overloads with no arity to choose between is ambiguous");
    }

    [Fact]
    public async Task NestedTypeAsyncFrame_PreservesNestingSeparator_AndResolves()
    {
        var frames = await FramesOf(
            "   at SharpLensMcp.Tests.Fixtures.StackTraceOuter+StackTraceInner+<NestedAsync>d__0.MoveNext()");

        frames.Count.Should().Be(1);
        frames[0]["frameKind"]!.Value<string>().Should().Be("async");
        frames[0]["symbol"]!.Value<string>().Should().Be(
            "SharpLensMcp.Tests.Fixtures.StackTraceOuter.StackTraceInner.NestedAsync");
        frames[0]["resolved"]!.Value<bool>().Should().BeTrue(
            "the Outer+Inner nesting must resolve by metadata name even on a demangled frame");
        frames[0]["location"]!["line"]!.Value<int>().Should().Be(
            FixtureLineOf("public static async Task NestedAsync()"));
    }

    [Fact]
    public async Task BclFrame_PassesThroughUnresolvedWithRawIntact()
    {
        const string raw = "   at System.String.Concat(String str0, String str1)";
        var frames = await FramesOf(raw);

        frames.Count.Should().Be(1);
        frames[0]["raw"]!.Value<string>().Should().Be(raw.Trim());
        frames[0]["symbol"]!.Value<string>().Should().Be("System.String.Concat");
        frames[0]["resolved"]!.Value<bool>().Should().BeFalse(
            "String.Concat resolves to a metadata type with no source in this solution");
        frames[0]["location"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task ContextLinesOutOfRange_ReturnsInvalidParameter()
    {
        var result = await Service.ResolveStackTraceAsync("at X.Y()", contextLines: 21);

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("between 0 and 20");
    }
}
