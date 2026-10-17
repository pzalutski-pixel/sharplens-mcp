using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLensMcp;

/// <summary>
/// Represents a JSON-RPC request identifier: a string or a number, per the JSON-RPC 2.0
/// specification. A number that fits a <see cref="long"/> exactly is held as a long; any
/// other number (a fraction, an exponent form, or beyond 64 bits) is held as its exact JSON
/// text, so the response echoes the id byte-for-byte as JSON-RPC 2.0 §5 requires.
/// </summary>
[JsonConverter(typeof(RequestId.Converter))]
public readonly struct RequestId : IEquatable<RequestId>
{
    // True when Value is the verbatim JSON text of a number rather than a string id, so the
    // two never compare equal and the number is written back unquoted.
    private readonly bool _isNumberText;

    public object? Value { get; }

    public RequestId(string value) => Value = value;
    public RequestId(long value) => Value = value;

    private RequestId(string numberText, bool isNumberText)
    {
        Value = numberText;
        _isNumberText = isNumberText;
    }

    /// <summary>
    /// The id for a JSON number token: a long when the text is a canonical 64-bit integer,
    /// otherwise the exact number text.
    /// </summary>
    public static RequestId FromJsonNumber(string numberText) =>
        long.TryParse(numberText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            && integer.ToString(CultureInfo.InvariantCulture) == numberText
            ? new RequestId(integer)
            : new RequestId(numberText, isNumberText: true);

    public override string ToString() =>
        Value is string s ? s :
        Value is long l ? l.ToString(CultureInfo.InvariantCulture) :
        string.Empty;

    public bool Equals(RequestId other) => _isNumberText == other._isNumberText && Equals(Value, other.Value);
    public override bool Equals(object? obj) => obj is RequestId other && Equals(other);
    public override int GetHashCode() => Value == null ? 0 : HashCode.Combine(Value, _isNumberText);
    public static bool operator ==(RequestId left, RequestId right) => left.Equals(right);
    public static bool operator !=(RequestId left, RequestId right) => !left.Equals(right);

    public sealed class Converter : JsonConverter<RequestId>
    {
        public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => new RequestId(reader.GetString()!),
                JsonTokenType.Number => FromJsonNumber(Encoding.UTF8.GetString(
                    reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray())),
                _ => throw new JsonException("id must be a string or number")
            };
        }

        public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
        {
            switch (value.Value)
            {
                case string s when value._isNumberText: writer.WriteRawValue(s); break;
                case string s: writer.WriteStringValue(s); break;
                case long l: writer.WriteNumberValue(l); break;
                default: writer.WriteNullValue(); break;
            }
        }
    }
}
