using FluentAssertions;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace SharpLensMcp.Tests;

// add_null_checks and generate_equality_members route their inserted code through
// WithDocumentLineEndings so a CRLF document does not receive lone-LF (mixed) generated
// guards/members. DetectLineEnding reads the document's first line break.
public class GeneratedLineEndingTests
{
    [Fact]
    public void CrlfDocument_ConvertsGeneratedLfToCrlf()
    {
        var document = SourceText.From("class C\r\n{\r\n}\r\n");

        RoslynService.DetectLineEnding(document).Should().Be("\r\n");
        RoslynService.WithDocumentLineEndings("\nguard1;\nguard2;\n", document)
            .Should().Be("\r\nguard1;\r\nguard2;\r\n",
                "every inserted newline must match the document's CRLF ending");
    }

    [Fact]
    public void LfDocument_LeavesGeneratedLfUnchanged()
    {
        var document = SourceText.From("class C\n{\n}\n");

        RoslynService.DetectLineEnding(document).Should().Be("\n");
        RoslynService.WithDocumentLineEndings("\nguard1;\nguard2;\n", document)
            .Should().Be("\nguard1;\nguard2;\n");
    }

    [Fact]
    public void LfDocument_NormalizesCrlfGeneratedTextToLf()
    {
        var document = SourceText.From("class C\n{\n}\n");

        // CRLF-bearing generated text (Windows AppendLine / NormalizeWhitespace) spliced into
        // an LF document must be collapsed to LF, not left as mixed endings.
        var result = RoslynService.WithDocumentLineEndings("a;\r\nb;\r\n", document);
        result.Should().Be("a;\nb;\n");
        result.Should().NotContain("\r", "no carriage returns may leak into an LF document");
    }

    [Fact]
    public void CrlfDocument_NormalizesAlreadyCrlfGeneratedText()
    {
        var document = SourceText.From("class C\r\n{\r\n}\r\n");

        // NormalizeWhitespace may already emit CRLF on some hosts; the result must not
        // double up to \r\r\n.
        RoslynService.WithDocumentLineEndings("a;\r\nb;\r\n", document)
            .Should().Be("a;\r\nb;\r\n");
    }
}
