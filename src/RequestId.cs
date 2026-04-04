using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpLensMcp;

/// <summary>
/// Represents a JSON-RPC request identifier, which can be either a string or an integer
/// per the JSON-RPC 2.0 specification.
/// </summary>
[JsonConverter(typeof(RequestId.Converter))]
public readonly struct RequestId : IEquatable<RequestId>
{
    public object? Value { get; }

    public RequestId(string value) => Value = value;
    public RequestId(long value) => Value = value;

    public override string ToString() =>
        Value is string s ? s :
        Value is long l ? l.ToString(CultureInfo.InvariantCulture) :
        string.Empty;

    public bool Equals(RequestId other) => Equals(Value, other.Value);
    public override bool Equals(object? obj) => obj is RequestId other && Equals(other);
    public override int GetHashCode() => Value?.GetHashCode() ?? 0;
    public static bool operator ==(RequestId left, RequestId right) => left.Equals(right);
    public static bool operator !=(RequestId left, RequestId right) => !left.Equals(right);

    public sealed class Converter : JsonConverter<RequestId>
    {
        public override RequestId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.String => new RequestId(reader.GetString()!),
                JsonTokenType.Number => new RequestId(reader.GetInt64()),
                _ => throw new JsonException("id must be a string or integer")
            };
        }

        public override void Write(Utf8JsonWriter writer, RequestId value, JsonSerializerOptions options)
        {
            switch (value.Value)
            {
                case string s: writer.WriteStringValue(s); break;
                case long l: writer.WriteNumberValue(l); break;
                default: writer.WriteNullValue(); break;
            }
        }
    }
}
