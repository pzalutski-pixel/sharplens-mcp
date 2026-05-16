using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace SharpLensMcp.Tests;

// Covers the typed-parameter helper that replaced ~80 inline
// `arguments?["x"]?.GetValue<T>() ?? throw new Exception("x required")` blocks
// in McpServer.HandleToolCallAsync.
public class JsonRpcParametersTests
{
    private static JsonRpcParameters Make(string json) =>
        new(JsonNode.Parse(json)?.AsObject(), "test-tool");

    [Fact]
    public void Required_PresentString_Returns()
    {
        var p = Make("""{"filePath":"foo.cs"}""");
        p.Required<string>("filePath").Should().Be("foo.cs");
    }

    [Fact]
    public void Required_Missing_Throws()
    {
        var p = Make("""{}""");
        var act = () => p.Required<string>("filePath");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*filePath*test-tool*");
    }

    [Fact]
    public void Required_WrongType_Throws()
    {
        var p = Make("""{"line":"not-a-number"}""");
        var act = () => p.Required<int>("line");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*line*wrong type*");
    }

    [Fact]
    public void Optional_Present_Returns()
    {
        var p = Make("""{"kind":"Method"}""");
        p.Optional<string>("kind").Should().Be("Method");
    }

    [Fact]
    public void Optional_Missing_ReturnsNull()
    {
        var p = Make("""{}""");
        p.Optional<string>("kind").Should().BeNull();
    }

    [Fact]
    public void OptionalStruct_Missing_ReturnsNull()
    {
        var p = Make("""{}""");
        p.OptionalStruct<int>("maxResults").Should().BeNull();
    }

    [Fact]
    public void OptionalStruct_Present_Returns()
    {
        var p = Make("""{"maxResults":42}""");
        p.OptionalStruct<int>("maxResults").Should().Be(42);
    }

    [Fact]
    public void OptionalWithFallback_Missing_ReturnsFallback()
    {
        var p = Make("""{}""");
        p.Optional("maxResults", 50).Should().Be(50);
        p.Optional("preview", true).Should().BeTrue();
    }

    [Fact]
    public void OptionalWithFallback_Present_ReturnsValue()
    {
        var p = Make("""{"maxResults":7,"preview":false}""");
        p.Optional("maxResults", 50).Should().Be(7);
        p.Optional("preview", true).Should().BeFalse();
    }

    [Fact]
    public void OptionalStringFallback_Missing_ReturnsFallback()
    {
        var p = Make("""{}""");
        p.Optional("verbosity", "compact").Should().Be("compact");
    }

    [Fact]
    public void OptionalStringArray_Present_ReturnsList()
    {
        var p = Make("""{"kinds":["Method","Field"]}""");
        p.OptionalStringArray("kinds").Should().BeEquivalentTo(new[] { "Method", "Field" });
    }

    [Fact]
    public void OptionalStringArray_Missing_ReturnsNull()
    {
        var p = Make("""{}""");
        p.OptionalStringArray("kinds").Should().BeNull();
    }

    [Fact]
    public void OptionalStringArray_NotAnArray_Throws()
    {
        var p = Make("""{"kinds":"not-an-array"}""");
        var act = () => p.OptionalStringArray("kinds");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*kinds*array of strings*");
    }

    [Fact]
    public void RequiredStringArray_Missing_Throws()
    {
        var p = Make("""{}""");
        var act = () => p.RequiredStringArray("typeNames");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*typeNames*test-tool*");
    }
}
