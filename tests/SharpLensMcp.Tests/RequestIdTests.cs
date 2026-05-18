using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

public class RequestIdTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new RequestId.Converter() }
    };

    [Fact]
    public void Deserialize_IntegerId_ReturnsLongValue()
    {
        var id = JsonSerializer.Deserialize<RequestId>("42", Options);
        id.Value.Should().Be(42L);
    }

    [Fact]
    public void Deserialize_StringId_ReturnsStringValue()
    {
        var id = JsonSerializer.Deserialize<RequestId>("\"abc-123\"", Options);
        id.Value.Should().Be("abc-123");
    }

    [Fact]
    public void Deserialize_GuidStringId_ReturnsStringValue()
    {
        var guid = "\"550e8400-e29b-41d4-a716-446655440000\"";
        var id = JsonSerializer.Deserialize<RequestId>(guid, Options);
        id.Value.Should().Be("550e8400-e29b-41d4-a716-446655440000");
    }

    [Fact]
    public void Deserialize_BooleanId_ThrowsJsonException()
    {
        var act = () => JsonSerializer.Deserialize<RequestId>("true", Options);
        act.Should().Throw<JsonException>().WithMessage("*string or integer*");
    }

    [Fact]
    public void Deserialize_ObjectId_ThrowsJsonException()
    {
        var act = () => JsonSerializer.Deserialize<RequestId>("{}", Options);
        act.Should().Throw<JsonException>().WithMessage("*string or integer*");
    }

    [Fact]
    public void Serialize_IntegerId_WritesNumber()
    {
        var id = new RequestId(42);
        var json = JsonSerializer.Serialize(id, Options);
        json.Should().Be("42");
    }

    [Fact]
    public void Serialize_StringId_WritesString()
    {
        var id = new RequestId("abc-123");
        var json = JsonSerializer.Serialize(id, Options);
        json.Should().Be("\"abc-123\"");
    }

    [Fact]
    public void RoundTrip_IntegerId_PreservesValue()
    {
        var original = new RequestId(99);
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<RequestId>(json, Options);
        deserialized.Should().Be(original);
    }

    [Fact]
    public void RoundTrip_StringId_PreservesValue()
    {
        var original = new RequestId("request-1");
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<RequestId>(json, Options);
        deserialized.Should().Be(original);
    }

    [Fact]
    public void Equality_SameIntegerValue_AreEqual()
    {
        var a = new RequestId(1);
        var b = new RequestId(1);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_SameStringValue_AreEqual()
    {
        var a = new RequestId("test");
        var b = new RequestId("test");
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var a = new RequestId(1);
        var b = new RequestId(2);
        (a != b).Should().BeTrue();
    }

    [Fact]
    public void ToString_IntegerId_ReturnsNumberString()
    {
        new RequestId(42).ToString().Should().Be("42");
    }

    [Fact]
    public void ToString_StringId_ReturnsString()
    {
        new RequestId("abc").ToString().Should().Be("abc");
    }

    [Fact]
    public void Deserialize_ArrayId_ThrowsJsonException()
    {
        // The converter's switch (RequestId.cs:34-39) only accepts String + Number.
        // Arrays must fall into the default branch with the canonical message.
        var act = () => JsonSerializer.Deserialize<RequestId>("[1,2,3]", Options);
        act.Should().Throw<JsonException>().WithMessage("*string or integer*");
    }

    [Fact]
    public void Deserialize_NullId_ThrowsJsonException()
    {
        var act = () => JsonSerializer.Deserialize<RequestId>("null", Options);
        act.Should().Throw<JsonException>().WithMessage("*string or integer*");
    }

    [Fact]
    public void ToString_DefaultRequestId_ReturnsEmptyString()
    {
        // Default constructor leaves Value=null. RequestId.cs:19-22 returns "" for that case.
        // Otherwise the test risks invariant culture / accidental "null" string output.
        default(RequestId).ToString().Should().Be("");
    }

    [Fact]
    public void GetHashCode_DefaultRequestId_ReturnsZero()
    {
        // Per RequestId.cs:26, the null-Value case returns 0.
        default(RequestId).GetHashCode().Should().Be(0);
    }

    [Fact]
    public void Equality_TwoDefaultRequestIds_AreEqual()
    {
        // Both have Value=null → Equals(null, null) is true.
        (default(RequestId) == default(RequestId)).Should().BeTrue();
        default(RequestId).Equals(default(RequestId)).Should().BeTrue();
    }

    [Fact]
    public void EqualsObjectOverride_WithNonRequestId_ReturnsFalse()
    {
        // The Equals(object?) override (RequestId.cs:25) must reject anything that
        // isn't a RequestId — including a string with the same content.
        var id = new RequestId("abc");
        id.Equals((object)"abc").Should().BeFalse(
            "Equals(object) must require RequestId type, not just string-equivalent content");
        id.Equals((object?)null).Should().BeFalse();
    }

    [Fact]
    public void Serialize_DefaultRequestId_WritesJsonNull()
    {
        // RequestId.cs:48 — the converter's Write writes JSON null for the default-Value case.
        var json = JsonSerializer.Serialize(default(RequestId), Options);
        json.Should().Be("null");
    }

    [Fact]
    public void Equality_IntegerVsString_AreNotEqual()
    {
        // RequestId(1L) and RequestId("1") are distinct — Equals(object, object) on
        // (long)1 vs (string)"1" returns false.
        var asInt = new RequestId(1L);
        var asStr = new RequestId("1");
        (asInt == asStr).Should().BeFalse(
            "integer 1 and string \"1\" must be distinct RequestIds");
        asInt.GetHashCode().Should().NotBe(asStr.GetHashCode());
    }
}
