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

    [Fact]
    public void Required_PresentInt_Returns()
    {
        // Counterpart to Required_PresentString — the generic Required<T> must work
        // for value types, not just reference types.
        var p = Make("""{"line":42}""");
        p.Required<int>("line").Should().Be(42);
    }

    [Fact]
    public void OptionalStringFallback_Present_ReturnsValue()
    {
        // Counterpart to OptionalStringFallback_Missing_ReturnsFallback — when the
        // value IS present, the fallback must be ignored.
        var p = Make("""{"verbosity":"detailed"}""");
        p.Optional("verbosity", "compact").Should().Be("detailed");
    }

    [Fact]
    public void OptionalStruct_WrongType_Throws()
    {
        // OptionalStruct must throw on type mismatch with the canonical "wrong type"
        // wording so callers can distinguish missing (null) from invalid (exception).
        var p = Make("""{"maxResults":"not-a-number"}""");
        var act = () => p.OptionalStruct<int>("maxResults");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*maxResults*wrong type*");
    }

    [Fact]
    public void OptionalWithFallback_WrongType_Throws()
    {
        // The fallback overload must also throw on wrong type rather than silently
        // returning the fallback — caller intent was "I supplied a value, parse it",
        // not "fall back if it's malformed".
        var p = Make("""{"maxResults":"not-a-number"}""");
        var act = () => p.Optional("maxResults", 50);
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*maxResults*wrong type*");
    }

    [Fact]
    public void OptionalStringArray_NullElement_DroppedFromResult()
    {
        // Per JsonRpcParameters.cs:95-96, non-string/null entries are filtered out
        // (mapped to empty string, then dropped). Verify a null array element
        // doesn't crash AND doesn't appear in the returned list.
        var p = Make("""{"kinds":["Method",null,"Field"]}""");
        var list = p.OptionalStringArray("kinds");
        list.Should().BeEquivalentTo(new[] { "Method", "Field" },
            "null entries must be silently filtered, not crash or appear as empty strings");
    }

    [Fact]
    public void Raw_PresentField_ReturnsNode()
    {
        // Raw(name) is the escape hatch for complex shapes (array-of-objects, nested
        // objects). It must return the raw JsonNode without type coercion.
        var p = Make("""{"method":{"name":"foo","args":[1,2,3]}}""");
        var raw = p.Raw("method");
        raw.Should().NotBeNull();
        raw!["name"]?.GetValue<string>().Should().Be("foo");
        (raw["args"] as JsonArray)!.Count.Should().Be(3);
    }

    [Fact]
    public void Raw_MissingField_ReturnsNull()
    {
        var p = Make("""{}""");
        p.Raw("missing").Should().BeNull();
    }

    [Fact]
    public void Optional_ClassWrongType_Throws()
    {
        // Counterpart to Required_WrongType_Throws but for the reference-type
        // Optional<T> overload (JsonRpcParameters.cs:42-50). A scalar number
        // passed where a string is expected must surface "has wrong type"
        // rather than silently returning null or crashing.
        var p = Make("""{"kind":42}""");
        var act = () => p.Optional<string>("kind");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*kind*test-tool*wrong type*");
    }

    [Fact]
    public void RequiredStringArray_Missing_ThrowsArraySpecificWording()
    {
        // Locks the DISTINCT "Missing required array parameter" wording from
        // JsonRpcParameters.cs:106-107, separate from the scalar Required's
        // "Missing required parameter" message. A regression unifying the two
        // would slip past the existing wildcard `*typeNames*test-tool*` lock.
        var p = Make("""{}""");
        var act = () => p.RequiredStringArray("typeNames");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*Missing required array parameter*typeNames*test-tool*");
    }

    [Fact]
    public void RequiredStringArray_Present_ReturnsList()
    {
        // Direct success-path test for RequiredStringArray — the existing
        // suite only exercises the missing-field error path.
        var p = Make("""{"typeNames":["Foo","Bar"]}""");
        p.RequiredStringArray("typeNames").Should().BeEquivalentTo(new[] { "Foo", "Bar" });
    }

    [Fact]
    public void RequiredStringArray_NotAnArray_PropagatesArrayShapeError()
    {
        // RequiredStringArray delegates to OptionalStringArray. A scalar value
        // (not an array) must surface the "must be an array of strings"
        // wording — NOT the "Missing required array parameter" wording, which
        // is reserved for the field-absent case. Two distinct error messages
        // route through this method depending on whether the field is missing
        // or malformed.
        var p = Make("""{"typeNames":"not-an-array"}""");
        var act = () => p.RequiredStringArray("typeNames");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*typeNames*test-tool*must be an array of strings*");
    }

    [Fact]
    public void OptionalStringArray_NonStringElement_ThrowsArrayShapeError()
    {
        // Per-element validation: a number/object/array inside the supposed
        // string-array must surface the structured "must be an array of
        // strings" -32602 error, not propagate an uncaught
        // System.Text.Json.JsonException (which the dispatcher's catch-all
        // would wrap as -32603 Internal error, hiding the real cause).
        var p = Make("""{"kinds":["Method",42,"Field"]}""");
        var act = () => p.OptionalStringArray("kinds");
        act.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*kinds*test-tool*must be an array of strings*");
    }

    [Fact]
    public void Constructor_WithNullArgs_AllAccessorsTreatAsEmpty()
    {
        // When the caller's tools/call params lack an `arguments` object entirely,
        // the dispatcher constructs JsonRpcParameters with args=null. The accessors
        // must behave as if every parameter is missing — not NullReferenceException.
        var p = new JsonRpcParameters(args: null, "test-tool");

        p.Optional<string>("x").Should().BeNull();
        p.OptionalStruct<int>("n").Should().BeNull();
        p.Optional("x", "fallback").Should().Be("fallback");
        p.Optional("n", 42).Should().Be(42);
        p.OptionalStringArray("arr").Should().BeNull();
        p.Raw("x").Should().BeNull();

        // Required-* must still throw with the canonical missing wording.
        var requiredAct = () => p.Required<string>("x");
        requiredAct.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*x*test-tool*");

        var requiredArrAct = () => p.RequiredStringArray("arr");
        requiredArrAct.Should().Throw<JsonRpcInvalidParamsException>()
            .WithMessage("*arr*test-tool*");
    }
}
