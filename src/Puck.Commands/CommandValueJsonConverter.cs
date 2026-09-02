using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using Puck.Assets.Documents;

namespace Puck.Commands;

/// <summary>
/// The canonical JSON shape of a <see cref="CommandValue"/>: <c>{ "kind": "&lt;declared member name&gt;", "raw":
/// [x, y, z, w] }</c>, strict on both edges — an undeclared or numeric <c>kind</c>, a non-finite or miscounted
/// <c>raw</c>, and any other member are each a hard parse failure.
/// </summary>
/// <remarks>
/// It rides <see cref="CommandValue"/>'s own declaration (<c>[JsonConverter]</c>) rather than a document context's
/// converter list, so every serializer reaching the type — this project's
/// <see cref="BindingProfileJsonContext"/>, the world document's context, a consumer's own — writes and reads the
/// one shape. The default shape a serializer would otherwise pick is not merely different, it is LOSSY:
/// <see cref="CommandValue.Raw"/> is a <see cref="Vector4"/>, whose components are public FIELDS that
/// System.Text.Json does not serialize, so an authored constant would cross the wire as <c>"raw": {}</c> and come
/// back as zero. The computed accessors (<see cref="CommandValue.AsAxis1D"/> and its siblings) would ride along
/// beside it, none of them readable back.
/// <para>
/// The kind is written and parsed by its exact declared member name, with no naming policy applied and no numeric
/// token accepted — the same posture
/// <see cref="Puck.Abstractions.Documents.StrictEnumConverter{TEnum}"/> gives every enum in this project, spelled
/// out here because the converter owns the whole object rather than delegating a member.
/// </para>
/// </remarks>
public sealed class CommandValueJsonConverter : JsonConverter<CommandValue> {
    private static Vector4 ReadRaw(ref Utf8JsonReader reader) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "CommandValue raw must be a four-element array.");
        }

        const string NotFiniteMessage = "CommandValue raw components must be finite numbers.";

        var x = JsonComponentReader.ReadFloat(notFiniteMessage: NotFiniteMessage, notNumberMessage: NotFiniteMessage, reader: ref reader);
        var y = JsonComponentReader.ReadFloat(notFiniteMessage: NotFiniteMessage, notNumberMessage: NotFiniteMessage, reader: ref reader);
        var z = JsonComponentReader.ReadFloat(notFiniteMessage: NotFiniteMessage, notNumberMessage: NotFiniteMessage, reader: ref reader);
        var w = JsonComponentReader.ReadFloat(notFiniteMessage: NotFiniteMessage, notNumberMessage: NotFiniteMessage, reader: ref reader);

        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.EndArray)
        ) {
            throw new JsonException(message: "CommandValue raw must contain exactly four elements.");
        }

        return new Vector4(
            w: w,
            x: x,
            y: y,
            z: z
        );
    }

    /// <inheritdoc/>
    public override CommandValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException(message: "a CommandValue must be an object with kind and raw members.");
        }

        CommandValueKind? kind = null;
        Vector4? raw = null;

        while (
            reader.Read() &&
            (reader.TokenType != JsonTokenType.EndObject)
        ) {
            if (reader.TokenType != JsonTokenType.PropertyName) {
                throw new JsonException(message: "a CommandValue member name was expected.");
            }

            var property = reader.GetString();

            if (!reader.Read()) {
                throw new JsonException(message: $"CommandValue member '{property}' has no value.");
            }

            switch (property) {
                case "kind":
                    var token = ((reader.TokenType == JsonTokenType.String)
                        ? reader.GetString()
                        : null
                    );

                    if (
                        (token is null) ||
                        !Enum.TryParse<CommandValueKind>(
                        ignoreCase: false,
                        result: out var parsed,
                        value: token
                    ) ||
                        !Enum.IsDefined(value: parsed)
                    ) {
                        throw new JsonException(message: $"CommandValue kind '{token}' is not declared.");
                    }
                    kind = parsed;
                    break;
                case "raw":
                    raw = ReadRaw(reader: ref reader);
                    break;
                default:
                    throw new JsonException(message: $"CommandValue contains unmapped member '{property}'.");
            }
        }

        return new CommandValue(
            Kind: (kind ?? throw new JsonException(message: "CommandValue requires member 'kind'.")),
            Raw: (raw ?? throw new JsonException(message: "CommandValue requires member 'raw'."))
        );
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, CommandValue value, JsonSerializerOptions options) {
        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "kind",
            value: value.Kind.ToString()
        );
        writer.WriteStartArray(propertyName: "raw");
        writer.WriteNumberValue(value: value.Raw.X);
        writer.WriteNumberValue(value: value.Raw.Y);
        writer.WriteNumberValue(value: value.Raw.Z);
        writer.WriteNumberValue(value: value.Raw.W);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
