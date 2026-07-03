using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// The shared snippet truncation must cut on a UTF-16 rune boundary: a raw Substring at
// the limit can split a surrogate pair (an emoji at the boundary) into a lone, invalid
// code unit. Used by find_dead_branches, find_reflection_usage, and the symbol-info
// debug path.
public class TruncateForSnippetTests
{
    [Fact]
    public void ShortText_ReturnedUnchanged()
    {
        RoslynService.TruncateForSnippet("hello", 160).Should().Be("hello");
    }

    [Fact]
    public void EmojiAtBoundary_NotSplitIntoLoneSurrogate()
    {
        const string emoji = "\U0001F600"; // 😀 — two UTF-16 code units
        // The emoji occupies indices 159 and 160; a cut at 160 would keep only the high
        // surrogate at 159, emitting a lone surrogate.
        var text = new string('a', 159) + emoji + "tail";

        var result = RoslynService.TruncateForSnippet(text, 160, "…");

        result.Should().Be(new string('a', 159) + "…",
            "the high surrogate at the boundary is dropped so the cut lands on a rune boundary");
        // No lone surrogate survives: every char in the result is well-formed.
        result.Any(char.IsSurrogate).Should().BeFalse("a lone surrogate must never appear");
    }

    [Fact]
    public void EmojiWhollyBeforeBoundary_Preserved()
    {
        const string emoji = "\U0001F600";
        // Emoji at indices 158/159, both kept by a cut at 160.
        var text = new string('a', 158) + emoji + "tail";

        var result = RoslynService.TruncateForSnippet(text, 160, "…");

        result.Should().Be(new string('a', 158) + emoji + "…");
        char.ConvertToUtf32(emoji, 0).Should().Be(0x1F600, "the intact emoji round-trips");
    }
}
