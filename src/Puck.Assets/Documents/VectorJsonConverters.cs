using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Assets.Documents;

/// <summary>
/// Reads and writes a <see cref="Vector2"/> as a two-element JSON array <c>[x, y]</c> — the one spelling every
/// document family (world, creation, audio, synth) carries a <see cref="Vector2"/> in. Numbers ride STJ's default
/// shortest-round-trip invariant formatting, so a value round-trips bit-exactly. The object form (<c>{"x":…,"y":…}</c>)
/// this converter's predecessor accepted is refused outright — wrong arity and the object form are both hard parse
/// failures, by name; STJ appends the offending JSON path to the exception when it propagates, so the refusal names
/// both what shape was expected and where the document went wrong.
/// </summary>
public sealed class Vector2JsonConverter : JsonConverter<Vector2> {
    private static float ReadComponent(ref Utf8JsonReader reader) {
        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.Number)
        ) {
            throw new JsonException(message: "a Vector2 element must be a finite number.");
        }

        return reader.GetSingle();
    }

    /// <inheritdoc/>
    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "a Vector2 must be a two-element [x, y] array (the object form is no longer accepted).");
        }

        var x = ReadComponent(reader: ref reader);
        var y = ReadComponent(reader: ref reader);

        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.EndArray)
        ) {
            throw new JsonException(message: "a Vector2 array must contain exactly two elements.");
        }

        return new Vector2(
            x: x,
            y: y
        );
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options) {
        writer.WriteStartArray();
        writer.WriteNumberValue(value: value.X);
        writer.WriteNumberValue(value: value.Y);
        writer.WriteEndArray();
    }
}
/// <summary>
/// Reads and writes a <see cref="Vector3"/> as a three-element JSON array <c>[x, y, z]</c> — the one spelling every
/// document family carries a <see cref="Vector3"/> in (world placements/spawn points/colliders/<c>bodyDirection</c>,
/// creation shapes, chain goals/poles, camera offsets, text-run positions, …). See <see cref="Vector2JsonConverter"/>'s
/// remarks for the arity/object-form refusal posture, which this converter shares exactly.
/// </summary>
public sealed class Vector3JsonConverter : JsonConverter<Vector3> {
    private static float ReadComponent(ref Utf8JsonReader reader) {
        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.Number)
        ) {
            throw new JsonException(message: "a Vector3 element must be a finite number.");
        }

        return reader.GetSingle();
    }

    /// <inheritdoc/>
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "a Vector3 must be a three-element [x, y, z] array (the object form is no longer accepted).");
        }

        var x = ReadComponent(reader: ref reader);
        var y = ReadComponent(reader: ref reader);
        var z = ReadComponent(reader: ref reader);

        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.EndArray)
        ) {
            throw new JsonException(message: "a Vector3 array must contain exactly three elements.");
        }

        return new Vector3(
            x: x,
            y: y,
            z: z
        );
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options) {
        writer.WriteStartArray();
        writer.WriteNumberValue(value: value.X);
        writer.WriteNumberValue(value: value.Y);
        writer.WriteNumberValue(value: value.Z);
        writer.WriteEndArray();
    }
}
/// <summary>
/// Reads and writes a <see cref="Quaternion"/> as a four-element JSON array <c>[x, y, z, w]</c> — the
/// <see cref="System.Numerics"/>/HLSL/glTF component order, stated once here rather than re-decided per document
/// family. The retired object form (<c>{"isIdentity":…,"w":…,"x":…,"y":…,"z":…}</c> — <see cref="Quaternion"/>'s raw
/// fields plus its computed <see cref="Quaternion.IsIdentity"/> property, which an author had no way to omit under
/// <c>IncludeFields</c>) is refused outright, matching <see cref="Vector3JsonConverter"/>'s posture exactly: wrong
/// arity and the object form are both hard parse failures, by name, with the offending JSON path appended by STJ.
/// </summary>
public sealed class QuaternionJsonConverter : JsonConverter<Quaternion> {
    private static float ReadComponent(ref Utf8JsonReader reader) {
        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.Number)
        ) {
            throw new JsonException(message: "a Quaternion element must be a finite number.");
        }

        return reader.GetSingle();
    }

    /// <inheritdoc/>
    public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "a Quaternion must be a four-element [x, y, z, w] array (the object form with 'isIdentity' is no longer accepted).");
        }

        var x = ReadComponent(reader: ref reader);
        var y = ReadComponent(reader: ref reader);
        var z = ReadComponent(reader: ref reader);
        var w = ReadComponent(reader: ref reader);

        if (
            !reader.Read() ||
            (reader.TokenType != JsonTokenType.EndArray)
        ) {
            throw new JsonException(message: "a Quaternion array must contain exactly four elements ([x, y, z, w]).");
        }

        return new Quaternion(
            x: x,
            y: y,
            z: z,
            w: w
        );
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options) {
        writer.WriteStartArray();
        writer.WriteNumberValue(value: value.X);
        writer.WriteNumberValue(value: value.Y);
        writer.WriteNumberValue(value: value.Z);
        writer.WriteNumberValue(value: value.W);
        writer.WriteEndArray();
    }
}
