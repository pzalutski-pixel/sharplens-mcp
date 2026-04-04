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
}
