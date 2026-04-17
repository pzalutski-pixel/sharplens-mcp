using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp;

/// <summary>
/// Adapter that exposes a Roslyn <see cref="TextDocument"/> as an <see cref="AdditionalText"/>
/// so it can be passed to <see cref="Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver"/>.
/// </summary>
internal sealed class AdditionalTextFile : AdditionalText
{
    private readonly TextDocument _document;

    public AdditionalTextFile(TextDocument document)
    {
        _document = document;
    }

    public override string Path => _document.FilePath ?? _document.Name;

    public override SourceText? GetText(CancellationToken cancellationToken = default)
    {
        return _document.GetTextAsync(cancellationToken).GetAwaiter().GetResult();
    }
}
