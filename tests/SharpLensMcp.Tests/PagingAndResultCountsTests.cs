using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// Paging.Slice / ResultPage, ResultCounts, and the two CreateSuccessResponse overloads:
// explicit truncation (results cut by a cap or page) versus the count-inferred legacy
// rule. In-process meta casing is PascalCase (ResponseMetadata).
public class PagingAndResultCountsTests
{
    private static readonly int[] Ten = Enumerable.Range(0, 10).ToArray();

    [Fact]
    public void FirstPage_HasMore_WithTheNextOffset()
    {
        var page = Paging.Slice(Ten, offset: 0, limit: 3);

        page.Items.Should().Equal(0, 1, 2);
        page.TotalCount.Should().Be(10);
        page.HasMore.Should().BeTrue();
        page.NextOffset.Should().Be(3);
    }

    [Fact]
    public void PartialLastPage_HasNoMore_AndNoNextOffset()
    {
        var page = Paging.Slice(Ten, offset: 9, limit: 3);

        page.Items.Should().Equal(9);
        page.HasMore.Should().BeFalse();
        page.NextOffset.Should().BeNull();
    }

    [Fact]
    public void PageEndingExactlyAtTheEnd_HasNoMore()
    {
        var page = Paging.Slice(Ten, offset: 7, limit: 3);

        page.Items.Should().Equal(7, 8, 9);
        page.HasMore.Should().BeFalse();
        page.NextOffset.Should().BeNull();
    }

    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    public void OffsetAtOrPastTheEnd_IsAnEmptyLastPage(int offset)
    {
        var page = Paging.Slice(Ten, offset, limit: 3);

        page.Items.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.NextOffset.Should().BeNull();
        page.Offset.Should().Be(offset);
    }

    [Fact]
    public void EmptyList_IsOneEmptyLastPage()
    {
        var page = Paging.Slice(Array.Empty<int>(), offset: 0, limit: 5);

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.HasMore.Should().BeFalse();
        page.NextOffset.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(-1, 3)]
    public void UnvalidatedRequest_Throws(int offset, int limit)
    {
        var slice = () => Paging.Slice(Ten, offset, limit);
        slice.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(25)]
    public void FollowingNextOffset_VisitsEveryItemOnce_AndTerminates(int limit)
    {
        var visited = new List<int>();
        var pages = 0;
        int? offset = 0;
        while (offset is { } current)
        {
            var page = Paging.Slice(Ten, current, limit);
            visited.AddRange(page.Items);
            pages++;
            pages.Should().BeLessOrEqualTo(10, "the pager must never loop");
            offset = page.NextOffset;
        }

        visited.Should().Equal(Ten);
        pages.Should().Be((int)Math.Ceiling(10.0 / limit));
    }

    [Fact]
    public void ResultCounts_Factories_StateTruncationExplicitly()
    {
        ResultCounts.Complete(4).Should().Be(new ResultCounts(4, 4, false));
        ResultCounts.Capped(10, 3).Should().Be(new ResultCounts(10, 3, true));
        ResultCounts.Capped(3, 3).Should().Be(new ResultCounts(3, 3, false));
        ResultCounts.Capped(0, 0).Should().Be(new ResultCounts(0, 0, false));
        ResultCounts.Page(Paging.Slice(Ten, 0, 4)).Should().Be(new ResultCounts(10, 4, true));
        ResultCounts.Page(Paging.Slice(Ten, 8, 4)).Should().Be(new ResultCounts(10, 2, false),
            "a last page returns fewer than the total but nothing was cut");
        ResultCounts.Bounded(7, truncated: true).Should().Be(new ResultCounts(null, 7, true));
        ResultCounts.Explicit(4, 3, truncated: false).Should().Be(new ResultCounts(4, 3, false),
            "a batch with one failed item is not truncated");
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(3, -1)]
    public void ResultCounts_Capped_RejectsImpossibleCounts(int total, int returned)
    {
        var capped = () => ResultCounts.Capped(total, returned);
        capped.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ResultCounts_CompleteAndBounded_RejectNegativeCounts()
    {
        ((Action)(() => ResultCounts.Complete(-1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => ResultCounts.Bounded(-1, false))).Should().Throw<ArgumentOutOfRangeException>();
    }

    private static JToken Meta(object response) => JObject.FromObject(response)["meta"]!;

    [Fact]
    public void ExplicitOverload_LastPage_IsNotTruncated()
    {
        var page = Paging.Slice(Ten, 8, 4);
        var meta = Meta(RoslynService.CreateSuccessResponse(new { items = page.Items }, ResultCounts.Page(page),
            suggestedNextTools: new[] { "next" }, verbosity: "compact"));

        meta["TotalCount"]!.Value<int>().Should().Be(10);
        meta["ReturnedCount"]!.Value<int>().Should().Be(2);
        meta["Truncated"]!.Value<bool>().Should().BeFalse();
        ((JArray)meta["SuggestedNextTools"]!).Select(t => t.Value<string>()).Should().Equal("next");
        meta["Verbosity"]!.Value<string>().Should().Be("compact");
    }

    [Fact]
    public void ExplicitOverload_CappedAndBounded()
    {
        var capped = Meta(RoslynService.CreateSuccessResponse(new { }, ResultCounts.Capped(50, 20)));
        capped["Truncated"]!.Value<bool>().Should().BeTrue();

        var bounded = Meta(RoslynService.CreateSuccessResponse(new { }, ResultCounts.Bounded(100, truncated: true)));
        bounded["TotalCount"]!.Type.Should().Be(JTokenType.Null);
        bounded["ReturnedCount"]!.Value<int>().Should().Be(100);
        bounded["Truncated"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void LegacyOverload_StillInfersTruncationFromTheCounts()
    {
        // The default for existing callers is unchanged: returned < total reads as
        // truncated (even on a last page), OR-combined with an explicit flag.
        Meta(RoslynService.CreateSuccessResponse(new { }, totalCount: 10, returnedCount: 2))["Truncated"]!
            .Value<bool>().Should().BeTrue();
        Meta(RoslynService.CreateSuccessResponse(new { }, totalCount: 10, returnedCount: 10))["Truncated"]!
            .Value<bool>().Should().BeFalse();
        Meta(RoslynService.CreateSuccessResponse(new { }, totalCount: 10, returnedCount: 10, truncated: true))["Truncated"]!
            .Value<bool>().Should().BeTrue();
        Meta(RoslynService.CreateSuccessResponse(new { }))["Truncated"]!.Value<bool>().Should().BeFalse();
    }
}
