using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

// Reads and writes source files in the file's own encoding. Reading goes through Roslyn's
// FileTextLoader - the loader MSBuildWorkspace uses - so a file decodes exactly as
// load_solution decoded it: a byte-order mark selects its encoding, BOM-less bytes decode
// as UTF-8 when they are valid UTF-8 and with the fallback code page (windows-1252 on a
// Western system) when they are not. The decoded SourceText carries that encoding.
// Writing emits the encoding's preamble only when the encoding has one, so a file read and
// written back keeps its encoding and its BOM presence byte for byte.
internal static class SourceFileIO
{
    private static readonly LoadTextOptions LoadOptions = new(SourceHashAlgorithm.Sha256);

    // UTF-8 without a byte-order mark: the encoding of a file that has no other to keep.
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<SourceText> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var loaded = await new FileTextLoader(path, defaultEncoding: null)
            .LoadTextAndVersionAsync(LoadOptions, cancellationToken);
        return loaded.Text;
    }

    // Writes the text with the encoding's preamble (when it has one) followed by the
    // encoded characters. Creates the directory for a new file.
    public static void Write(string path, string text, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, bytes);
    }
}
