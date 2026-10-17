using FluentAssertions;
using Newtonsoft.Json.Linq;
using SharpLensMcp.Tests.Mcp;
using Xunit;

namespace SharpLensMcp.Tests;

// find_async_issues scans every executable body — methods, constructors, property bodies,
// local functions and lambdas — finds an unforwarded token when the awaited call has an
// OPTIONAL token parameter, when the await goes through ConfigureAwait, and when the
// cancellable overload is generic; reports each finding once for a multi-targeted project
// (with the frameworks it occurs in); and resolves project names through the logical
// project model.
public class AsyncIssuesCoverageTests
{
    private const string Source = """
        using System;
        using System.IO;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Lib
        {
            public class AsyncStuff
            {
                private readonly Task<int> _task = Task.FromResult(1);

                public async void Fire() { await Task.Delay(1); }

                public async void OnClick(object sender, EventArgs e) { await Task.Delay(1); }

                public int BlockInMethod() => _task.Result;

                public int BlockingProperty => _task.Result;

                public AsyncStuff(Task t) { t.Wait(); }

                public void LocalAsyncVoid()
                {
                    async void Local() { await Task.Delay(1); }
                    Local();
                    Action a = async () => await Task.Delay(1);
                    a();
                }

                public async Task<string> OptionalTokenOmitted(string path, CancellationToken ct)
                {
                    return await File.ReadAllTextAsync(path);
                }

                public async Task OverloadTokenOmitted(CancellationToken ct)
                {
                    await Task.Delay(100);
                }

                public async Task ConfigureAwaitOmitted(CancellationToken ct)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                public async Task<int> Generic(CancellationToken ct) { return await Task.Run(() => 1); }

                public async Task Forwarded(CancellationToken ct)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
        }
        """;

    // The (line, column) of the first occurrence of marker at or after anchor.
    private static string Row(string category, string symbol, string marker, string? anchor = null)
    {
        var start = anchor == null ? 0 : Source.IndexOf(anchor, StringComparison.Ordinal);
        var index = Source.IndexOf(marker, start, StringComparison.Ordinal);
        var before = Source[..index];
        return $"{category} {symbol} Lib/AsyncStuff.cs:{before.Count(c => c == '\n')}:{index - (before.LastIndexOf('\n') + 1)}";
    }

    private static readonly string[] Expected =
    {
        Row("asyncVoid", "Lib.AsyncStuff.Fire()", "Fire()"),
        Row("blockingCall", "Lib.AsyncStuff.BlockInMethod()", "_task.Result", anchor: "BlockInMethod"),
        Row("blockingCall", "Lib.AsyncStuff.BlockingProperty", "_task.Result", anchor: "BlockingProperty"),
        Row("blockingCall", "Lib.AsyncStuff.AsyncStuff(System.Threading.Tasks.Task)", "t.Wait()"),
        Row("asyncVoid", "Lib.AsyncStuff.LocalAsyncVoid()", "Local()"),
        Row("asyncVoid", "Lib.AsyncStuff.LocalAsyncVoid()", "async () =>"),
        Row("tokenNotForwarded", "Lib.AsyncStuff.OptionalTokenOmitted(string, System.Threading.CancellationToken)", "await File"),
        Row("tokenNotForwarded", "Lib.AsyncStuff.OverloadTokenOmitted(System.Threading.CancellationToken)", "await Task.Delay(100);"),
        Row("tokenNotForwarded", "Lib.AsyncStuff.ConfigureAwaitOmitted(System.Threading.CancellationToken)", "await Task.Delay(100).ConfigureAwait"),
        Row("tokenNotForwarded", "Lib.AsyncStuff.Generic(System.Threading.CancellationToken)", "await Task.Run"),
    };

    private static List<string> Rows(JToken data) =>
        ((JArray)data["issues"]!).Select(i =>
            $"{i["category"]} {i["symbol"]} {i["location"]!["filePath"]}:{i["location"]!["line"]}:{i["location"]!["column"]}").ToList();

    private static QualitySolutionBuilder Build(bool multiTargeted, out RoslynService service, out InMemoryDispatcher dispatcher)
    {
        var builder = new QualitySolutionBuilder();
        if (multiTargeted) builder.AddMultiTargeted("Lib", new[] { ("AsyncStuff.cs", Source) });
        else builder.AddProject("Lib", new[] { ("AsyncStuff.cs", Source) });
        var workspace = builder.Build();
        service = new RoslynService();
        service.LoadFromWorkspaceForTesting(workspace);
        dispatcher = new InMemoryDispatcher(workspace);
        return builder;
    }

    [Fact]
    public async Task EveryBodyKind_AndEveryUnforwardedTokenShape_IsFound()
    {
        using var builder = Build(multiTargeted: false, out var service, out _);

        var data = JObject.FromObject(await service.FindAsyncIssuesAsync("Lib"))["data"]!;

        Rows(data).Should().Equal(Expected);
        data["countByCategory"]!["asyncVoid"]!.Value<int>().Should().Be(3);
        data["countByCategory"]!["blockingCall"]!.Value<int>().Should().Be(3);
        data["countByCategory"]!["tokenNotForwarded"]!.Value<int>().Should().Be(4);
        ((JArray)data["issues"]!).Select(i => i["targetFrameworks"]!.Type).Should().AllBeEquivalentTo(JTokenType.Null);
    }

    [Fact]
    public async Task MultiTargetedProject_ReportsEachIssueOnce_WithItsFrameworks()
    {
        using var builder = Build(multiTargeted: true, out var service, out _);

        var all = JObject.FromObject(await service.FindAsyncIssuesAsync("Lib"))["data"]!;
        Rows(all).Should().Equal(Expected);
        ((JArray)all["issues"]!).Select(i => string.Join(",", i["targetFrameworks"]!.Values<string>()))
            .Should().AllBe("net10.0,net8.0");

        var net8 = JObject.FromObject(await service.FindAsyncIssuesAsync("Lib(net8.0)"))["data"]!;
        Rows(net8).Should().Equal(Expected);
        ((JArray)net8["issues"]!).Select(i => string.Join(",", i["targetFrameworks"]!.Values<string>()))
            .Should().AllBe("net8.0");
    }

    [Fact]
    public async Task UnknownProject_AndZeroMaxResults_AreInvalidParameters()
    {
        using var builder = Build(multiTargeted: true, out var service, out _);

        var unknown = JObject.FromObject(await service.FindAsyncIssuesAsync("Nope"));
        unknown["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        unknown["error"]!["Message"]!.Value<string>().Should().Be("Project not found: Nope");

        var zero = JObject.FromObject(await service.FindAsyncIssuesAsync("Lib", maxResults: 0));
        zero["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        zero["error"]!["Message"]!.Value<string>().Should().Be("maxResults must be between 1 and 1000 (got 0)");
    }

    [Fact]
    public async Task ViaDispatcher_MultiTargetedProjectByPlainName()
    {
        using var builder = Build(multiTargeted: true, out _, out var dispatcher);

        var data = await dispatcher.DataAsync("find_async_issues", new { projectName = "Lib" });
        Rows(data).Should().Equal(Expected);

        var error = await dispatcher.ErrorAsync("find_async_issues", new { projectName = "Nope" }, ErrorCodes.InvalidParameter);
        error["message"]!.Value<string>().Should().Be("Project not found: Nope");
    }
}
