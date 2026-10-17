using System.Text;
using System.Threading.Channels;

namespace SharpLensMcp.Tests.Mcp;

// The server's stdout for in-process RunAsync tests: every line the server writes is queued
// and handed to the test by ReadLineAsync, which fails the test if no line arrives in time.
internal sealed class LineChannelWriter : TextWriter
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public override Encoding Encoding => Encoding.UTF8;

    public override Task WriteLineAsync(string? value)
    {
        _lines.Writer.TryWrite(value ?? "");
        return Task.CompletedTask;
    }

    public override void WriteLine(string? value) => _lines.Writer.TryWrite(value ?? "");

    public override Task FlushAsync() => Task.CompletedTask;

    public async Task<string> ReadLineAsync(TimeSpan timeout, string because)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _lines.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"No response line within {timeout.TotalSeconds:0}s: {because}");
        }
    }

    public bool TryReadLine(out string? line) => _lines.Reader.TryRead(out line);
}
