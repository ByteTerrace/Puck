using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>A binding destination's channel reference, resolved by its declared name.</summary>
[JsonConverter(typeof(ChannelRefJsonConverter))]
public abstract record ChannelRef {
    private ChannelRef() {
    }

    /// <summary>Describes this reference in binding diagnostics.</summary>
    internal string Describe() => this switch {
        Name name => $"name \"{name.Value}\"",
        _ => "reference",
    };

    /// <summary>References a channel by its authored name.</summary>
    /// <param name="Value">The declared channel name.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record Name(string Value) : ChannelRef;
}
/// <summary>Reads and writes a channel reference as its authored name string.</summary>
public sealed class ChannelRefJsonConverter : JsonConverter<ChannelRef> {
    /// <inheritdoc/>
    public override ChannelRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException(message: "a channel reference must be a name string.");
        }

        return new ChannelRef.Name(Value: reader.GetString()!);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ChannelRef value, JsonSerializerOptions options) {
        switch (value) {
            case ChannelRef.Name name:
                writer.WriteStringValue(value: name.Value);
                break;
            default:
                throw new JsonException(message: "channel reference variant is not declared.");
        }
    }
}
