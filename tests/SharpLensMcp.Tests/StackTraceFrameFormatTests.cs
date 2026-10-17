using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// resolve_stack_trace on the frame shapes real runtimes print, each at both levels
// (ToolPath): lambda and closure frames ("<>c." containers, async lambdas), nested types
// and explicit interface implementations, overloads told apart by the frame's parameter
// types and by the reported line, demangled iterator frames ("Seq()+MoveNext()"), Mono /
// Unity frames, static constructors, localized traces, and a line number too large for
// an int. Each frame also carries the type resolution's shadow notice.
public class StackTraceFrameFormatTests
{
    internal const string Source = """
        namespace StackNs
        {
            public interface IRun { void Go(); }

            public class Runner : IRun
            {
                void IRun.Go()
                {
                    System.Console.WriteLine();
                }
            }

            public class Outer
            {
                public class Inner
                {
                    public void Work()
                    {
                        System.Console.WriteLine();
                    }
                }
            }

            public class Over
            {
                public void Process(int x)
                {
                    System.Console.WriteLine(x);
                }

                public void Process(string s)
                {
                    System.Console.WriteLine(s);
                }
            }

            public class Lam
            {
                static Lam() { System.Console.WriteLine(); }

                public void NoCapture()
                {
                    System.Action a = () => System.Console.WriteLine();
                    a();
                }

                public void Capture(int v)
                {
                    System.Action a = () => System.Console.WriteLine(v);
                    a();
                }

                public async System.Threading.Tasks.Task AsyncLambda()
                {
                    System.Func<System.Threading.Tasks.Task> f = async () => await System.Threading.Tasks.Task.Yield();
                    await f();
                }
            }

            public class Iters
            {
                public System.Collections.Generic.IEnumerable<int> Seq()
                {
                    yield return 1;
                }

                public System.Collections.Generic.IEnumerable<int> Seq(int n)
                {
                    yield return n;
                }
            }

            public class Random
            {
                public void Roll() { }
            }
        }
        """;

    private static int LineOf(string marker) => RetargetedSources.Locate(Source, marker).line;

    private static async Task<(JArray frames, string path)> ResolveAsync(ToolPath toolPath, Func<string, string> trace)
    {
        var (workspace, document) = TestHelpers.CreateWorkspaceWithCode(Source, "Stack.cs");
        var runner = new ToolPathRunner(toolPath, workspace);
        var path = document.FilePath!;
        var text = trace(path);
        var data = await runner.DataAsync("resolve_stack_trace", new { stackTrace = text },
            s => s.ResolveStackTraceAsync(text));
        return ((JArray)data["frames"]!, path);
    }

    private static (string kind, string? symbol, bool resolved, int? line) Frame(JToken frame) =>
        (frame["frameKind"]!.Value<string>()!,
         frame["symbol"]!.Value<string?>(),
         frame["resolved"]!.Value<bool>(),
         frame["location"]!.Type == JTokenType.Null ? null : frame["location"]!["line"]!.Value<int>());

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task LambdaClosureAndAsyncLambdaFrames_DemangleToTheContainingMethod(ToolPath toolPath)
    {
        var (frames, _) = await ResolveAsync(toolPath, _ => string.Join("\n",
            "   at StackNs.Lam.<>c.<NoCapture>b__1_0()",
            "   at StackNs.Lam.<>c__DisplayClass2_0.<Capture>b__0()",
            "   at StackNs.Lam.<>c.<<AsyncLambda>b__3_0>d.MoveNext()"));

        frames.Select(Frame).Should().Equal(
            ("lambda", "StackNs.Lam.NoCapture", true, LineOf("public void NoCapture()")),
            ("lambda", "StackNs.Lam.Capture", true, LineOf("public void Capture(int v)")),
            ("lambda", "StackNs.Lam.AsyncLambda", true, LineOf("public async System.Threading.Tasks.Task AsyncLambda()")));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task NestedTypeAndExplicitImplementationFrames_ResolveWithoutAFileLine(ToolPath toolPath)
    {
        var (frames, _) = await ResolveAsync(toolPath, _ => string.Join("\n",
            "   at StackNs.Outer.Inner.Work()",
            "   at StackNs.Runner.StackNs.IRun.Go()"));

        frames.Select(Frame).Should().Equal(
            ("method", "StackNs.Outer.Inner.Work", true, LineOf("public void Work()")),
            ("method", "StackNs.Runner.StackNs.IRun.Go", true, LineOf("void IRun.Go()")));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task OverloadFrames_MatchTheParameterTypes_AndKeepAReportedLineInsideTheMatch(ToolPath toolPath)
    {
        var bodyLine = LineOf("System.Console.WriteLine(s);");
        var (frames, _) = await ResolveAsync(toolPath, path => string.Join("\n",
            "   at StackNs.Over.Process(String s)",
            "   at StackNs.Over.Process(Int32 x)",
            $"   at StackNs.Over.Process(String s) in {path}:line {bodyLine + 1}"));

        frames.Select(Frame).Should().Equal(
            ("method", "StackNs.Over.Process", true, LineOf("public void Process(string s)")),
            ("method", "StackNs.Over.Process", true, LineOf("public void Process(int x)")),
            ("method", "StackNs.Over.Process", true, bodyLine));
        frames.Select(f => f["ambiguousOverload"]!.Value<bool>()).Should().Equal(false, false, false);
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task DemangledIteratorFrames_ResolveTheOverloadTheirParametersName(ToolPath toolPath)
    {
        var (frames, _) = await ResolveAsync(toolPath, _ => string.Join("\n",
            "   at StackNs.Iters.Seq()+MoveNext()",
            "   at StackNs.Iters.Seq(Int32 n)+MoveNext()"));

        frames.Select(Frame).Should().Equal(
            ("iterator", "StackNs.Iters.Seq", true, LineOf("IEnumerable<int> Seq()")),
            ("iterator", "StackNs.Iters.Seq", true, LineOf("IEnumerable<int> Seq(int n)")));
        frames.Select(f => f["ambiguousOverload"]!.Value<bool>()).Should().Equal(false, false);
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task LineNumberTooLargeForAnInt_IsTreatedAsNoLine(ToolPath toolPath)
    {
        var (frames, _) = await ResolveAsync(toolPath, path => $"   at StackNs.Outer.Inner.Work() in {path}:line 99999999999");

        frames.Select(Frame).Should().Equal(("method", "StackNs.Outer.Inner.Work", true, LineOf("public void Work()")));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task MonoFrameAndStaticConstructorFrame_Resolve(ToolPath toolPath)
    {
        var bodyLine = LineOf("System.Console.WriteLine(s);");
        var (frames, _) = await ResolveAsync(toolPath, path => string.Join("\n",
            $"  at StackNs.Over.Process (System.String s) [0x00000] in {path}:{bodyLine + 1}",
            "   at StackNs.Lam..cctor()"));

        frames.Select(Frame).Should().Equal(
            ("method", "StackNs.Over.Process", true, bodyLine),
            ("method", "StackNs.Lam.cctor", true, LineOf("static Lam()")));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task LocalizedFrames_ParseAsFrames_AndTheirMessagesStayText(ToolPath toolPath)
    {
        var bodyLine = LineOf("System.Console.WriteLine(s);");
        var workLine = LineOf("public void Work()") + 2;
        var (frames, _) = await ResolveAsync(toolPath, path => string.Join("\n",
            "Unbehandelte Ausnahme: System.InvalidOperationException: kaputt (x)",
            $"   bei StackNs.Over.Process(String s) in {path}:Zeile {bodyLine + 1}",
            $"   à StackNs.Outer.Inner.Work() dans {path}:ligne {workLine + 1}"));

        frames.Select(Frame).Should().Equal(
            ("text", null, false, null),
            ("method", "StackNs.Over.Process", true, bodyLine),
            ("method", "StackNs.Outer.Inner.Work", true, workLine));
    }

    [Theory]
    [InlineData(ToolPath.Service)]
    [InlineData(ToolPath.Dispatcher)]
    public async Task EachFrame_CarriesItsTypeResolutionsShadowNotice(ToolPath toolPath)
    {
        var (frames, _) = await ResolveAsync(toolPath, _ => string.Join("\n",
            "   at Random.Roll()",
            "   at StackNs.Outer.Inner.Work()"));

        frames.Select(Frame).Should().Equal(
            ("method", "Random.Roll", true, LineOf("public void Roll()")),
            ("method", "StackNs.Outer.Inner.Work", true, LineOf("public void Work()")));
        frames[0]["notice"]!.Value<string>().Should().Be(
            "'Random' resolved to StackNs.Random; also matches System.Random — pass the fully-qualified name to target it.");
        frames[1]["notice"]!.Type.Should().Be(JTokenType.Null);
    }
}
