using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// find_similar_code against SimilarCodeFixture: Alpha and Beta are
// token-identical modulo identifiers, so the renamed twin must score exactly
// 1.0, the query method itself must be excluded, and the structurally
// unrelated method must stay below the default threshold.
public class SimilarCodeTests : RoslynServiceTestBase
{
    [Fact]
    public async Task MethodQuery_FindsRenamedTwin_ExcludesSelfAndUnrelated()
    {
        var result = await Service.FindSimilarCodeAsync(
            typeName: "SimilarCodeHost", methodName: "SumOfSquaresAlpha");

        var data = GetData(result);
        data["query"]!.Value<string>().Should().Contain("SumOfSquaresAlpha");

        var results = (data["results"] as JArray)!;
        var names = results.Select(r => r["method"]!.Value<string>()).ToList();
        names.Should().Contain("SimilarCodeHost.SumOfCubesBeta",
            "Beta is Alpha with every identifier renamed");
        names.Should().NotContain("SimilarCodeHost.SumOfSquaresAlpha",
            "the query method must be excluded from its own results");
        names.Should().NotContain("SimilarCodeHost.CompletelyDifferent",
            "a structurally unrelated method must stay below the 0.75 threshold");

        var beta = results.First(r => r["method"]!.Value<string>() == "SimilarCodeHost.SumOfCubesBeta");
        beta["score"]!.Value<double>().Should().Be(1.0,
            "identifier and literal normalization makes the two bodies fingerprint-identical");
        beta["statementCount"]!.Value<int>().Should().Be(3);
        beta["location"]!["filePath"]!.Value<string>().Should().EndWith("SimilarCodeFixture.cs");
    }

    [Fact]
    public async Task ExpressionBodiedQuery_FindsExpressionBodiedTwin()
    {
        // ClampBeta is ClampAlpha with every identifier renamed; both are
        // expression-bodied. A candidate with no block body must still be
        // reachable — the prior candidate loop skipped every expression body.
        var result = await Service.FindSimilarCodeAsync(
            typeName: "SimilarCodeHost", methodName: "ClampAlpha");

        var data = GetData(result);
        var results = (data["results"] as JArray)!;
        var beta = results.FirstOrDefault(r => r["method"]!.Value<string>() == "SimilarCodeHost.ClampBeta");
        beta.Should().NotBeNull("an expression-bodied twin must be returned as a candidate");
        beta!["score"]!.Value<double>().Should().Be(1.0,
            "identifier normalization makes the two expression bodies fingerprint-identical");
        beta["statementCount"]!.Value<int>().Should().Be(1,
            "a single-expression body reports a statement count of 1");
    }

    [Fact]
    public async Task MethodQuery_FindsTwinsInConstructorsLocalFunctionsAndAccessors()
    {
        // SumOfSquaresAlpha's loop is mirrored in a constructor, a local function,
        // and a property getter — none of which the method-only sweep ever saw.
        var result = await Service.FindSimilarCodeAsync(
            typeName: "SimilarCodeHost", methodName: "SumOfSquaresAlpha", threshold: 0.5, maxResults: 50);

        var names = (GetData(result)["results"] as JArray)!
            .Select(r => r["method"]!.Value<string>()!)
            .ToList();

        names.Should().Contain("SimilarCodeMemberKinds.Inner",
            "a local function with the same body must be reachable as a candidate");
        names.Should().Contain("SimilarCodeMemberKinds.Squared.get",
            "a property accessor with the same body must be reachable as a candidate");
        names.Should().Contain("SimilarCodeMemberKinds.SimilarCodeMemberKinds",
            "a constructor with the same loop must be reachable as a candidate");
    }

    [Fact]
    public async Task MethodQuery_CapsResults_AndReportsTruncation()
    {
        // SumOfSquaresAlpha has several twins above the 0.5 threshold; cap to one.
        var result = await Service.FindSimilarCodeAsync(
            typeName: "SimilarCodeHost", methodName: "SumOfSquaresAlpha", threshold: 0.5, maxResults: 1);
        var json = Newtonsoft.Json.Linq.JObject.FromObject(result);
        json["success"]!.Value<bool>().Should().BeTrue(json.ToString());

        (json["data"]!["results"] as Newtonsoft.Json.Linq.JArray)!.Count.Should().Be(1,
            "maxResults=1 caps the returned matches to one");
        json["meta"]!["TotalCount"]!.Value<int>().Should().BeGreaterThan(1,
            "more twins exist above the threshold than were returned");
        json["meta"]!["ReturnedCount"]!.Value<int>().Should().Be(1);
        json["meta"]!["Truncated"]!.Value<bool>().Should().BeTrue(
            "returnedCount < totalCount must surface Truncated");
    }

    [Fact]
    public async Task PartialMethodQuery_FingerprintsTheImplementingPart()
    {
        // PartialTwin's body lives in the second partial declaration; resolving
        // reference [0] (the bodyless part) would fail. Its body mirrors
        // SumOfSquaresAlpha, so the query must succeed and find that twin.
        var result = await Service.FindSimilarCodeAsync(
            typeName: "SimilarCodePartial", methodName: "PartialTwin");

        AssertSuccess(result);
        var names = (GetData(result)["results"] as JArray)!
            .Select(r => r["method"]!.Value<string>()!)
            .ToList();
        names.Should().Contain("SimilarCodeHost.SumOfSquaresAlpha",
            "the implementing partial body fingerprints to the same shape as SumOfSquaresAlpha");
    }

    [Fact]
    public async Task SnippetQuery_FindsBothTwins()
    {
        var result = await Service.FindSimilarCodeAsync(snippet: """
            var sum = 0;
            foreach (var item in items)
            {
                sum += item * item;
            }
            return sum;
            """);

        var data = GetData(result);
        data["query"]!.Value<string>().Should().Be("snippet");
        var names = (data["results"] as JArray)!.Select(r => r["method"]!.Value<string>()).ToList();
        names.Should().Contain("SimilarCodeHost.SumOfSquaresAlpha");
        names.Should().Contain("SimilarCodeHost.SumOfCubesBeta");
    }

    [Fact]
    public async Task BothQueryModes_ReturnsInvalidParameter()
    {
        var result = await Service.FindSimilarCodeAsync(
            typeName: "SimilarCodeHost", methodName: "SumOfSquaresAlpha", snippet: "var x = 1;");

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("not both");
    }

    [Fact]
    public async Task NeitherQueryMode_ReturnsInvalidParameter()
    {
        var result = await Service.FindSimilarCodeAsync();

        AssertError(result, ErrorCodes.InvalidParameter);
    }

    [Fact]
    public async Task ThresholdOutOfRange_ReturnsInvalidParameter()
    {
        var result = await Service.FindSimilarCodeAsync(
            typeName: "SimilarCodeHost", methodName: "SumOfSquaresAlpha", threshold: 1.5);

        AssertError(result, ErrorCodes.InvalidParameter);
        var json = JObject.FromObject(result);
        json["error"]!["Message"]!.Value<string>().Should().Contain("threshold");
    }

    [Fact]
    public async Task UnknownMethod_PropagatesSymbolNotFound()
    {
        var result = await Service.FindSimilarCodeAsync(
            typeName: "SimilarCodeHost", methodName: "NoSuchMethod");

        AssertError(result, ErrorCodes.SymbolNotFound);
    }
}
