using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>An opaque stored placement translation: a dealt child's displacement from its slot, otherwise an
/// authored local offset. Geometry reads a resolved frame through <see cref="WorldPlacementFrameCompilation"/>,
/// never this value as a position. Fixed-point writes and their JSON round trips retain every Q48.16 bit.</summary>
[JsonConverter(typeof(WorldPlacementTranslation.Converter))]
public sealed class WorldPlacementTranslation : IDocumentSpatialValue, IEquatable<WorldPlacementTranslation> {
    private readonly DocumentVector3? m_authored;
    private readonly FixedVector3 m_fixed;

    private WorldPlacementTranslation(DocumentVector3 authored) => m_authored = authored;

    /// <summary>Stores an exact displacement or local offset without a presentation conversion.</summary>
    public WorldPlacementTranslation(FixedVector3 value) => m_fixed = value;

    /// <inheritdoc/>
    public string? Reference => m_authored?.Reference;
    /// <inheritdoc/>
    public object? Binder => m_authored?.Binder;
    /// <inheritdoc/>
    public string ExpectedValue => "a placement translation array [x, y, z]";
    /// <summary>Whether the authored translation has finite components.</summary>
    public bool IsFinite => ((m_authored is null) || VectorFunctions.IsFinite(vector: m_authored.Value));

    // Only frame compilation interprets storage as coordinates. No public vector getter or conversion exists.
    internal FixedVector3 FixedValue => ((m_authored is null) ? m_fixed : FixedVector3.FromVector3(value: m_authored.Value));

    /// <inheritdoc/>
    public IDocumentStateValue Bound(object binder, Func<string?> read) =>
        new WorldPlacementTranslation(authored: ((DocumentVector3)m_authored!.Bound(binder: binder, read: read)));
    /// <inheritdoc/>
    public bool TryResolved(string text, bool detach, out IDocumentStateValue resolved, out string reason) {
        if (m_authored is not null) {
            if (m_authored.TryResolved(detach: detach, reason: out reason, resolved: out var authored, text: text)) {
                resolved = new WorldPlacementTranslation(authored: ((DocumentVector3)authored));
                return true;
            }
            resolved = this;
            return false;
        }
        resolved = this;
        reason = "translation reference did not resolve";
        return false;
    }

    /// <summary>Accepts an authored literal; it cannot be read back as a vector.</summary>
    public static implicit operator WorldPlacementTranslation(Vector3 value) => new(authored: new DocumentVector3(value: value));
    /// <summary>Accepts an authored literal or state reference; it cannot be read back as a vector.</summary>
    public static implicit operator WorldPlacementTranslation(DocumentVector3 value) => new(authored: value);
    /// <summary>Accepts an exact stored displacement.</summary>
    public static implicit operator WorldPlacementTranslation(FixedVector3 value) => new(value: value);

    /// <inheritdoc/>
    public bool Equals(WorldPlacementTranslation? other) => ((other is not null) &&
        ((Reference is { } reference) ? (reference == other.Reference) : ((other.Reference is null) &&
        ((IsFinite && other.IsFinite) ? (FixedValue == other.FixedValue) : Equals(objA: m_authored, objB: other.m_authored)))));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is WorldPlacementTranslation other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() => ((Reference is { } reference) ? StringComparer.Ordinal.GetHashCode(obj: reference) :
        (IsFinite ? FixedValue.GetHashCode() : m_authored!.GetHashCode()));

    /// <summary>Serializes the stored translation, preserving exact fixed-point literals and authored references.</summary>
    public sealed class Converter : JsonConverter<WorldPlacementTranslation>, IJsonSchemaNodeConverter {
        /// <inheritdoc/>
        public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new DocumentVector3JsonConverter().BuildSchema(exportType: exportType);
        /// <inheritdoc/>
        public override WorldPlacementTranslation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType == JsonTokenType.String) {
                return new WorldPlacementTranslation(authored: new DocumentVector3JsonConverter().Read(options: options, reader: ref reader, typeToConvert: typeof(DocumentVector3)));
            }
            if (reader.TokenType != JsonTokenType.StartArray) { throw new JsonException(message: "Expected a placement translation array."); }
            var x = ReadComponent(reader: ref reader);
            var y = ReadComponent(reader: ref reader);
            var z = ReadComponent(reader: ref reader);

            if (!reader.Read() || (reader.TokenType != JsonTokenType.EndArray)) { throw new JsonException(message: "Expected three translation components."); }
            return new(value: new FixedVector3(X: x, Y: y, Z: z));
        }

        private static FixedQ4816 ReadComponent(ref Utf8JsonReader reader) {
            return FixedQ4816.Parse(JsonComponentReader.ReadDecimal(ref reader, "Expected a translation number.").ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <inheritdoc/>
        public override void Write(Utf8JsonWriter writer, WorldPlacementTranslation value, JsonSerializerOptions options) {
            if (value.Reference is { } reference) { writer.WriteStringValue(value: reference); return; }
            if (!value.IsFinite) { throw new JsonException(message: "Placement translation components must be finite."); }
            var fixedValue = value.FixedValue;

            writer.WriteStartArray();
            writer.WriteNumberValue(value: decimal.CreateChecked(value: fixedValue.X));
            writer.WriteNumberValue(value: decimal.CreateChecked(value: fixedValue.Y));
            writer.WriteNumberValue(value: decimal.CreateChecked(value: fixedValue.Z));
            writer.WriteEndArray();
        }
    }
}
