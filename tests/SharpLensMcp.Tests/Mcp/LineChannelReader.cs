using System.Threading.Channels;

namespace SharpLensMcp.Tests.Mcp;

// The server's stdin for in-process RunAsync tests: each line the test sends is returned by
// ReadLineAsync in order, and Complete() makes the next read return null (EOF).
internal sealed class LineChannelReader : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public void Send(string line) => _lines.Writer.TryWrite(line);

    public void Complete() => _lines.Writer.TryComplete();

    public override async Task<string?> ReadLineAsync()
    {
        try
        {
            return await _lines.Reader.ReadAsync();
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}
