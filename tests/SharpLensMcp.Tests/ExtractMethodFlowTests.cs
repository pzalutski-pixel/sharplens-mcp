using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// extract_method decides the new method's shape from Roslyn's data-flow and
// control-flow analysis of the selected statements: it is static wherever its caller
// cannot supply `this`, async whenever the selection awaits (await foreach and await
// using included), a parameter of the caller that the selection writes flows back to
// the caller, a local the selection declares stays declared for the code after it,
// control leaving the selection (break, continue, goto, yield) is refused, and so is a
// selection that does not cover whole statements of one block.
public class ExtractMethodFlowTests
{
    private static int LineOf(string source, string marker)
    {
        var line = source.Split('\n').ToList().FindIndex(l => l.Contains(marker, StringComparison.Ordinal));
        line.Should().BeGreaterThan(-1, $"marker '{marker}' must exist");
        return line;
    }

    private static async Task<JObject> Extract(RoslynService service, string filePath, string source,
        string startMarker, string endMarker, string methodName, bool preview = false) =>
        Json(await service.ExtractMethodAsync(filePath, LineOf(source, startMarker), LineOf(source, endMarker),
            methodName, preview: preview));

    internal const string StaticHostSource = """
        namespace Lib
        {
            public class Ext
            {
                public static int StaticHost(int a)
                {
                    int b = a + 1;
                    int c = b * 2;
                    return c;
                }
            }
        }
        """;

    [Fact]
    public async Task SelectionInAStaticMethod_ExtractsAStaticMethod()
    {
        var (service, workspace, document) = Load(StaticHostSource);

        var json = await Extract(service, document.FilePath!, StaticHostSource, "int b = a + 1;", "int c = b * 2;", "M1");

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be("""
            namespace Lib
            {
                public class Ext
                {
                    public static int StaticHost(int a)
                    {
                        var c = M1(a);
                        return c;
                    }

                    private static int M1(int a)
                    {
                        int b = a + 1;
                        int c = b * 2;
                        return c;
                    }
                }
            }
            """);
        json["data"]!["signature"]!.Value<string>().Should().Be("private static int M1(int a)");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_ShowsTheMethodAndCallExactlyAsTheyWillBeWritten()
    {
        var (service, workspace, document) = Load(StaticHostSource);

        var json = await Extract(service, document.FilePath!, StaticHostSource, "int b = a + 1;", "int c = b * 2;", "M1",
            preview: true);

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["extractedCode"]!.Value<string>().Should().Be("""
            private static int M1(int a)
            {
                int b = a + 1;
                int c = b * 2;
                return c;
            }
            """);
        json["data"]!["replacementCode"]!.Value<string>().Should().Be("var c = M1(a);");
        json["data"]!["location"]!["filePath"]!.Value<string>().Should().Be(Reported(document.FilePath!));
        (await TextOf(workspace, document.Id)).Should().Be(StaticHostSource);
    }

    internal const string StaticClassSource = """
        namespace Lib
        {
            public static class Tools
            {
                public static int Twice(int a)
                {
                    int b = a * 2;
                    return b;
                }
            }
        }
        """;

    [Fact]
    public async Task SelectionInAStaticClass_ExtractsAStaticMember()
    {
        var (service, workspace, document) = Load(StaticClassSource);

        var json = await Extract(service, document.FilePath!, StaticClassSource, "int b = a * 2;", "int b = a * 2;", "Calc");

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be("""
            namespace Lib
            {
                public static class Tools
                {
                    public static int Twice(int a)
                    {
                        var b = Calc(a);
                        return b;
                    }

                    private static int Calc(int a)
                    {
                        int b = a * 2;
                        return b;
                    }
                }
            }
            """);
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string StaticLocalFunctionSource = """
        namespace Lib
        {
            public class Host
            {
                public int Run(int a)
                {
                    return Local(a);

                    static int Local(int v)
                    {
                        int w = v + 1;
                        return w;
                    }
                }
            }
        }
        """;

    [Fact]
    public async Task SelectionInAStaticLocalFunction_ExtractsAStaticMethod()
    {
        var (service, workspace, document) = Load(StaticLocalFunctionSource);

        var json = await Extract(service, document.FilePath!, StaticLocalFunctionSource, "int w = v + 1;", "int w = v + 1;", "Bump");

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be("""
            namespace Lib
            {
                public class Host
                {
                    public int Run(int a)
                    {
                        return Local(a);

                        static int Local(int v)
                        {
                            var w = Bump(v);
                            return w;
                        }
                    }

                    private static int Bump(int v)
                    {
                        int w = v + 1;
                        return w;
                    }
                }
            }
            """);
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string JumpSource = """
        using System.Collections.Generic;

        namespace Lib
        {
            public class Jumps
            {
                public int Loop(int[] xs)
                {
                    int sum = 0;
                    foreach (var x in xs)
                    {
                        if (x < 0) break;
                        if (x == 0) continue;
                        sum += x;
                    }
                    return sum;
                }

                public int Go(int n)
                {
                    if (n > 0) goto done;
                    n = 1;
                    done:
                    return n;
                }

                public IEnumerable<int> Yield()
                {
                    yield return 1;
                    yield return 2;
                }
            }
        }
        """;

    [Theory]
    [InlineData("if (x < 0) break;", "The selection contains 'break' (line 11), which transfers control out of the selection")]
    [InlineData("if (x == 0) continue;", "The selection contains 'continue' (line 12), which transfers control out of the selection")]
    [InlineData("if (n > 0) goto done;", "The selection contains 'goto' (line 20), which transfers control out of the selection")]
    [InlineData("yield return 1;", "The selection contains 'yield return' (line 28); an iterator's yield cannot move into another method")]
    public async Task ControlFlowLeavingTheSelection_IsRefused(string marker, string message)
    {
        var (service, workspace, document) = Load(JumpSource);

        var json = await Extract(service, document.FilePath!, JumpSource, marker, marker, "Extracted");

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(message);
        (await TextOf(workspace, document.Id)).Should().Be(JumpSource);
    }

    internal const string AsyncSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        namespace Lib
        {
            public class Streams
            {
                public async Task AwaitForeach(IAsyncEnumerable<int> xs)
                {
                    await foreach (var x in xs)
                    {
                        Console.WriteLine(x);
                    }
                    Console.WriteLine("done");
                }

                public async Task AwaitUsing(IAsyncDisposable d)
                {
                    await using (d)
                    {
                        Console.WriteLine(1);
                    }
                }
            }
        }
        """;

    [Fact]
    public async Task AwaitForeach_ExtractsAnAsyncMethodAndAwaitsIt()
    {
        var (service, workspace, document) = Load(AsyncSource);
        var start = LineOf(AsyncSource, "await foreach");

        var json = Json(await service.ExtractMethodAsync(document.FilePath!, start, start + 3, "M4", preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(AsyncSource, ("""
                            await foreach (var x in xs)
                            {
                                Console.WriteLine(x);
                            }
                            Console.WriteLine("done");
                        }
                """, """
                            await M4(xs);
                            Console.WriteLine("done");
                        }

                        private async Task M4(IAsyncEnumerable<int> xs)
                        {
                            await foreach (var x in xs)
                            {
                                Console.WriteLine(x);
                            }
                        }
                """)));
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task AwaitUsing_ExtractsAnAsyncMethodAndAwaitsIt()
    {
        var (service, workspace, document) = Load(AsyncSource);
        var start = LineOf(AsyncSource, "await using (d)");

        var json = Json(await service.ExtractMethodAsync(document.FilePath!, start, start + 3, "Release", preview: false));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        json["data"]!["signature"]!.Value<string>().Should().Be("private async Task Release(IAsyncDisposable d)");
        (await ErrorsOf(workspace)).Should().BeEmpty();
        (await TextOf(workspace, document.Id)).Should().Contain("""
                    public async Task AwaitUsing(IAsyncDisposable d)
                    {
                        await Release(d);
                    }
            """);
    }

    internal const string WriteBackSource = """
        using System;

        namespace Lib
        {
            public class Writes
            {
                public void ParamWrite(int p)
                {
                    p = p * 2;
                    Console.WriteLine(p);
                }

                public void DeclReuse()
                {
                    int y = 0;
                    Console.WriteLine(y);
                    y = 5;
                    Console.WriteLine(y);
                }
            }
        }
        """;

    [Fact]
    public async Task WriteToACallerParameter_FlowsBackToTheCaller()
    {
        var (service, workspace, document) = Load(WriteBackSource);

        var json = await Extract(service, document.FilePath!, WriteBackSource, "p = p * 2;", "p = p * 2;", "M3");

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(WriteBackSource, ("""
                        p = p * 2;
                        Console.WriteLine(p);
                    }
            """, """
                        p = M3(p);
                        Console.WriteLine(p);
                    }

                    private int M3(int p)
                    {
                        p = p * 2;
                        return p;
                    }
            """)));
        json["data"]!["returnVariable"]!.Value<string>().Should().Be("p");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    [Fact]
    public async Task LocalDeclaredInTheSelectionAndUsedAfter_StaysDeclaredForTheCaller()
    {
        var (service, workspace, document) = Load(WriteBackSource);

        var json = await Extract(service, document.FilePath!, WriteBackSource, "int y = 0;", "Console.WriteLine(y);", "M6");

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        (await TextOf(workspace, document.Id)).Should().Be(Edited(WriteBackSource, ("""
                        int y = 0;
                        Console.WriteLine(y);
                        y = 5;
                        Console.WriteLine(y);
                    }
            """, """
                        int y;
                        M6();
                        y = 5;
                        Console.WriteLine(y);
                    }

                    private void M6()
                    {
                        int y = 0;
                        Console.WriteLine(y);
                    }
            """)));
        json["data"]!["replacementCode"]!.Value<string>().Should().Be("int y;\nM6();");
        (await ErrorsOf(workspace)).Should().BeEmpty();
    }

    internal const string NestSource = """
        using System;

        namespace Lib
        {
            public class Nesting
            {
                public void Nest(bool f)
                {
                    if (f)
                    {
                        Console.WriteLine("a");
                        Console.WriteLine("b");
                    }
                    Console.WriteLine("c");
                }
            }
        }
        """;

    [Fact]
    public async Task SelectionSpanningNestedAndOuterStatements_IsRefused()
    {
        var (service, workspace, document) = Load(NestSource);

        var json = await Extract(service, document.FilePath!, NestSource, "Console.WriteLine(\"b\");", "Console.WriteLine(\"c\");", "M7");

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            "The selection must cover complete statements of one block, but lines 11 and 13 belong to different blocks");
        (await TextOf(workspace, document.Id)).Should().Be(NestSource);
    }

    [Fact]
    public async Task SelectionCuttingThroughAStatement_IsRefused()
    {
        var (service, workspace, document) = Load(NestSource);

        var json = await Extract(service, document.FilePath!, NestSource, "if (f)", "Console.WriteLine(\"a\");", "M8");

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(
            "The selection cuts through the statement at lines 8-12; select it whole or only statements inside it");
        (await TextOf(workspace, document.Id)).Should().Be(NestSource);
    }
}
