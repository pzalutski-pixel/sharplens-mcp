using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

// ParameterValidation: the documented value domains of limits, offsets, numeric ranges
// and enumerated strings, and the INVALID_PARAMETER error each violation produces.
public class ParameterValidationTests
{
    private static JObject Context(RoslynError error) => JObject.FromObject(error.Context!);

    [Theory]
    [InlineData(1)]
    [InlineData(250)]
    [InlineData(500)]
    public void Limit_WithinRange_IsValid(int value)
    {
        ParameterValidation.Limit("maxResults", value, maximum: 500).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(501)]
    [InlineData(int.MinValue)]
    public void Limit_OutOfRange_NamesTheParameterAndTheRange(int value)
    {
        var error = ParameterValidation.Limit("maxResults", value, maximum: 500)!;

        error.Code.Should().Be(ErrorCodes.InvalidParameter);
        error.Message.Should().Be($"maxResults must be between 1 and 500 (got {value})");
        error.Hint.Should().Be("Pass maxResults as an integer from 1 to 500.");
        var context = Context(error);
        context["parameter"]!.Value<string>().Should().Be("maxResults");
        context["value"]!.Value<int>().Should().Be(value);
        context["minimum"]!.Value<int>().Should().Be(1);
        context["maximum"]!.Value<int>().Should().Be(500);
    }

    [Fact]
    public void Limit_DocumentedMinimum_OverridesOne()
    {
        ParameterValidation.Limit("contextLines", 0, maximum: 20, minimum: 0).Should().BeNull();
        ParameterValidation.Limit("contextLines", -1, maximum: 20, minimum: 0)!.Message
            .Should().Be("contextLines must be between 0 and 20 (got -1)");
    }

    [Fact]
    public void Limit_Nullable_AbsentIsValid_PresentIsChecked()
    {
        ParameterValidation.Limit("maxResults", (int?)null, maximum: 100).Should().BeNull();
        ParameterValidation.Limit("maxResults", (int?)0, maximum: 100)!.Message
            .Should().Be("maxResults must be between 1 and 100 (got 0)");
    }

    [Fact]
    public void Offset_ZeroOrMore_IsValid_NegativeIsNot()
    {
        ParameterValidation.Offset("offset", 0).Should().BeNull();
        ParameterValidation.Offset("offset", 12345).Should().BeNull();

        var error = ParameterValidation.Offset("offset", -1)!;
        error.Code.Should().Be(ErrorCodes.InvalidParameter);
        error.Message.Should().Be("offset must be 0 or greater (got -1)");
        Context(error)["minimum"]!.Value<int>().Should().Be(0);
    }

    [Fact]
    public void Range_InclusiveBounds()
    {
        ParameterValidation.Range("threshold", 0, 0, 1).Should().BeNull();
        ParameterValidation.Range("threshold", 1, 0, 1).Should().BeNull();
        ParameterValidation.Range("threshold", 0.75, 0, 1).Should().BeNull();
        ParameterValidation.Range("threshold", 1.5, 0, 1)!.Message
            .Should().Be("threshold must be between 0 and 1 (got 1.5)");
        ParameterValidation.Range("threshold", -0.25, 0, 1)!.Message
            .Should().Be("threshold must be between 0 and 1 (got -0.25)");
        ParameterValidation.Range("threshold", double.NaN, 0, 1)!.Message
            .Should().Be("threshold must be between 0 and 1 (got NaN)");
    }

    private static readonly string[] Verbosities = { "summary", "compact", "full" };

    [Theory]
    [InlineData("full", "full")]
    [InlineData("FULL", "full")]
    [InlineData("Compact", "compact")]
    public void Choice_MatchesCaseInsensitively_ReturningTheDocumentedSpelling(string value, string expected)
    {
        ParameterValidation.Choice("verbosity", value, Verbosities, out var canonical).Should().BeNull();
        canonical.Should().Be(expected);
    }

    [Fact]
    public void Choice_Null_IsAbsent()
    {
        ParameterValidation.Choice("verbosity", null, Verbosities, out var canonical).Should().BeNull();
        canonical.Should().BeNull();
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("")]
    [InlineData(" full")]
    public void Choice_Unknown_ListsTheAllowedValues(string value)
    {
        var error = ParameterValidation.Choice("verbosity", value, Verbosities, out var canonical)!;

        canonical.Should().BeNull();
        error.Code.Should().Be(ErrorCodes.InvalidParameter);
        error.Message.Should().Be($"verbosity must be one of: summary, compact, full (got '{value}')");
        error.Hint.Should().Be("Pass verbosity as one of: summary, compact, full (case-insensitive).");
        ((JArray)Context(error)["allowedValues"]!).Select(v => v.Value<string>())
            .Should().Equal("summary", "compact", "full");
    }

    [Fact]
    public void ErrorEnvelope_CarriesTheValidationErrorUnchanged()
    {
        var error = ParameterValidation.Limit("topN", 0, maximum: 50)!;
        var json = JObject.FromObject(RoslynService.CreateErrorResponse(error));

        json["success"]!.Value<bool>().Should().BeFalse();
        json["error"]!["Code"]!.Value<string>().Should().Be(ErrorCodes.InvalidParameter);
        json["error"]!["Message"]!.Value<string>().Should().Be("topN must be between 1 and 50 (got 0)");
        json["error"]!["Context"]!["parameter"]!.Value<string>().Should().Be("topN");
    }
}
