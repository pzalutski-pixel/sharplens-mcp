using System.Threading;
using System.Threading.Tasks;

namespace SharpLensMcp.Tests.Fixtures;

// One violation per find_async_issues category, plus a legitimate event
// handler that must NOT be flagged.
public class AsyncIssuesTarget
{
    // (a) async void that is not an event handler — flagged.
    public async void FireAndForget()
    {
        await Task.Yield();
    }

    // Legitimate event handler shape (object, EventArgs) — NOT flagged.
    public async void OnClick(object sender, System.EventArgs e)
    {
        await Task.Yield();
    }

    // (b) blocking on async — flagged.
    public int BlockOnResult()
    {
        return ComputeAsync().Result;
    }

    // (c) token-not-forwarded: this method has a token but awaits a call with a
    // token overload without passing it — flagged.
    public async Task<int> ForwardsNothing(CancellationToken cancellationToken)
    {
        return await ComputeAsync();
    }

    // token-not-forwarded FALSE-positive guard: Process(string) has NO cancellable
    // variant (no Process(string, CancellationToken)); the only token overload is the
    // unrelated Process(CancellationToken). Calling Process("x") must NOT be flagged,
    // even though a same-named overload happens to take a token.
    public async Task ProcessText(CancellationToken cancellationToken)
    {
        await Process("x");
    }

    private Task<int> ComputeAsync() => Task.FromResult(1);
    private Task<int> ComputeAsync(CancellationToken cancellationToken) => Task.FromResult(1);
    private Task Process(string text) => Task.CompletedTask;
    private Task Process(CancellationToken cancellationToken) => Task.CompletedTask;
}
