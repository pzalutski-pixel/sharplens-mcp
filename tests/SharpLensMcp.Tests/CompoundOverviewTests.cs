using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;
using static SharpLensMcp.Tests.RefactoringTestKit;

namespace SharpLensMcp.Tests;

// The compound overviews count what they list: get_type_overview's baseTypes is the
// whole base-class chain below the framework roots and baseTypeCount is its length, and
// its member summary counts the members every member-listing tool lists;
// get_file_overview's memberCount is the number of member entries; analyze_method
// resolves the method through the shared member resolution (one overloadIndex meaning
// everywhere) and validates its limits.
public class CompoundOverviewTests
{
    private const string Source = """
        namespace M
        {
            public class Plain { }
            public struct Val { }
            public class L1 { } public class L2 : L1 { } public class L3 : L2 { } public class L4 : L3 { } public class L5 : L4 { }

            public class Fields
            {
                private int a, b;
                public int C;
                public int Sum() => a + b + C;
                public int this[int i] => i;
                public int Auto { get; set; }
                public event System.EventHandler? Changed;
                public class Nested { }
                public static Fields operator +(Fields x, Fields y) => x;
            }

            public class Over
            {
                public void Do(string s) { }
                public void Do(int i) { }
                public int Prop { get; set; }
                public void Call() { Do(1); Do("x"); }
            }
        }
        """;

    [Theory]
    [InlineData("M.Plain", new string[0])]
    [InlineData("M.Val", new string[0])]
    [InlineData("M.L5", new[] { "M.L4", "M.L3", "M.L2", "M.L1" })]
    public async Task BaseTypeCount_IsTheLengthOfTheListedChain(string typeName, string[] chain)
    {
        var (service, _, _) = Load(Source);

        var json = Json(await service.GetTypeOverviewAsync(typeName));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        ((JArray)json["data"]!["baseTypes"]!).Select(b => b.Value<string>()).Should().Equal(chain);
        json["data"]!["baseTypeCount"]!.Value<int>().Should().Be(chain.Length);
    }

    [Fact]
    public async Task MemberSummary_CountsTheListedMembersByKind()
    {
        var (service, _, _) = Load(Source);

        var json = Json(await service.GetTypeOverviewAsync("M.Fields"));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var summary = (JObject)json["data"]!["memberSummary"]!;
        summary.Properties().Select(p => $"{p.Name}={p.Value}").Should().Equal(
            "methods=1", "properties=1", "fields=3", "events=1", "constructors=0",
            "indexers=1", "operators=1", "nestedTypes=1");
    }

    [Fact]
    public async Task FileOverviewMemberCount_CountsEveryListedEntry()
    {
        var (service, _, document) = Load(Source);

        var json = Json(await service.GetFileOverviewAsync(document.FilePath!));

        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());
        var fields = ((JArray)json["data"]!["typeDeclarations"]!).Single(t => t["name"]!.Value<string>() == "Fields");
        ((JArray)fields["members"]!).Count.Should().Be(9);
        fields["memberCount"]!.Value<int>().Should().Be(9, "`int a, b;` is two members, as listed");
    }

    [Fact]
    public async Task AnalyzeMethod_OverloadIndex_FollowsTheCanonicalOverloadOrder()
    {
        var (service, _, _) = Load(Source);

        var first = Json(await service.AnalyzeMethodAsync("M.Over", "Do", overloadIndex: 0));
        var second = Json(await service.AnalyzeMethodAsync("M.Over", "Do", overloadIndex: 1));

        first["data"]!["signature"]!["fullSignature"]!.Value<string>().Should().Be("M.Over.Do(string)");
        second["data"]!["signature"]!["fullSignature"]!.Value<string>().Should().Be("M.Over.Do(int)");
        first["data"]!["overloadCount"]!.Value<int>().Should().Be(2);
    }

    [Fact]
    public async Task AnalyzeMethod_WithoutAnOverloadIndex_ListsTheCandidates()
    {
        var (service, _, _) = Load(Source);

        var json = Json(await service.AnalyzeMethodAsync("M.Over", "Do"));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.AmbiguousMatch);
        json["error"]!["Message"]!.Value<string>().Should().Be("Member 'Do' has 2 overloads in 'M.Over'");
        ((JArray)json["error"]!["Context"]!["candidates"]!).Select(c => c["signature"]!.Value<string>())
            .Should().Equal("M.Over.Do(string)", "M.Over.Do(int)");
    }

    [Fact]
    public async Task AnalyzeMethod_OnAProperty_IsNotAMethod()
    {
        var (service, _, _) = Load(Source);

        var json = Json(await service.AnalyzeMethodAsync("M.Over", "Prop"));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.NotAMethod);
        json["error"]!["Message"]!.Value<string>().Should().Be("'M.Over.Prop' is not a method");
    }

    [Theory]
    [InlineData(0, 50, "maxCallers must be between 1 and 1000 (got 0)")]
    [InlineData(20, -1, "maxOutgoingCalls must be between 1 and 1000 (got -1)")]
    public async Task AnalyzeMethod_LimitBelowOne_IsRejected(int maxCallers, int maxOutgoingCalls, string message)
    {
        var (service, _, _) = Load(Source);

        var json = Json(await service.AnalyzeMethodAsync("M.Over", "Call",
            maxCallers: maxCallers, maxOutgoingCalls: maxOutgoingCalls));

        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be(message);
    }
}
