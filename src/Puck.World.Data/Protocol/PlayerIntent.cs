using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>The channel vector's declared ordinal ceiling — every <see cref="PlayerIntent"/> preallocates this many
/// <see cref="FixedQ4816"/> slots inline, independent of the wire's per-tick act-cell bound (a different quantity: that
/// one bounds out-cells per tick, this one bounds how many rows a world's channel table may declare). A PROTOTYPE
/// value: the real ceiling is owed to the fold's preallocation, which is what makes a zero-alloc tick true rather than
/// aspirational — a mount ceiling times this count is the contribution set's fixed size.</summary>
public static class ChannelLimits {
    /// <summary>The total addressable ordinals.</summary>
    public const int MaxChannels = 16;
}

/// <summary>A channel's declared value shape — what a bound destination's value clamps and quantizes to, and what
/// <see cref="Puck.Maths.FixedContributionFold"/> receives through <see cref="Puck.World.WorldChannelTable.CompileFoldShape"/>
/// for a HUMAN-OCCUPIED body's pooled contribution set. An unoccupied body still folds by plain overwrite (the
/// single-submitter precedence ladder); a
/// <see cref="Binary"/> shape's pool-clamp domain is the continuous <c>[0, One]</c> range BEFORE quantization snaps it
/// to a bit.</summary>
[JsonConverter(typeof(StrictEnumConverter<ChannelShape>))]
public enum ChannelShape : byte {
    /// <summary>Ranges <c>[-1, 1]</c> — a stick axis.</summary>
    Bipolar,

    /// <summary>Ranges <c>[0, 1]</c> — an analog trigger.</summary>
    Unipolar,

    /// <summary>A bit: exactly <c>{0, 1}</c> once quantized. Edges derive from the value crossing the channel's
    /// threshold against the previous sub-step (see <c>Puck.World.Server.WorldBody</c>'s per-channel edge
    /// tracking) — never carried.</summary>
    Binary,
}

/// <summary>Zero-alloc fixed-point storage for one <see cref="PlayerIntent"/> — <see cref="ChannelLimits.MaxChannels"/>
/// <see cref="FixedQ4816"/> slots inline in the value itself (an <c>InlineArray</c>, never a heap array), so
/// constructing, copying, and folding an intent every sub-step allocates nothing.</summary>
[InlineArray(ChannelLimits.MaxChannels)]
public struct ChannelValues {
    private FixedQ4816 m_element0;
}

/// <summary>One simulation tick's player intent. The vector retains sixteen positional wire slots; the compiled
/// world channel table assigns every declared channel consecutively and resolves claimed roles to those authored
/// ordinals.</summary>
/// <param name="Channels">The raw per-ordinal fixed-point values.</param>
public readonly record struct PlayerIntent(ChannelValues Channels) {
    /// <summary>Reads one channel's raw value by ordinal — zero for an ordinal no declared channel claims.</summary>
    /// <param name="ordinal">The channel ordinal (<c>0..</c><see cref="ChannelLimits.MaxChannels"/><c>-1</c>).</param>
    public FixedQ4816 this[int ordinal] => Channels[ordinal];

    /// <summary>Returns this intent with one ordinal replaced — the composition-channel write path (a bound kit
    /// effect, a wire press, a held device value).</summary>
    /// <param name="ordinal">The channel ordinal to replace.</param>
    /// <param name="value">The raw fixed-point value to write.</param>
    public PlayerIntent WithChannel(int ordinal, FixedQ4816 value) {
        var channels = Channels;

        channels[ordinal] = value;

        return new PlayerIntent(Channels: channels);
    }

    /// <summary>Determines structural equality with <paramref name="other"/> over the WHOLE vector, comparing per
    /// ordinal. Declared explicitly because the compiler-synthesized record-struct equality would otherwise compare
    /// <see cref="ChannelValues"/> as a single opaque field (an <c>InlineArray</c> exposes only its first backing
    /// field to reflection-based equality) — silently comparing only ordinal 0 instead of the vector.</summary>
    public bool Equals(PlayerIntent other) {
        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (Channels[ordinal] != other.Channels[ordinal]) {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns a hash consistent with <see cref="Equals(PlayerIntent)"/> — folded over every ordinal, for the same
    /// reason <see cref="Equals(PlayerIntent)"/> is declared explicitly.</summary>
    public override int GetHashCode() {
        var hash = new HashCode();

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            hash.Add(value: Channels[ordinal]);
        }

        return hash.ToHashCode();
    }
}

/// <summary>What fills an entity's intent gaps between tape segments.</summary>
/// <remarks>A source is live input, an idle mask, or one authored producer-program name. The merge order is tape,
/// submitted intent unless idle, producer output when named, then zero.</remarks>
[JsonConverter(typeof(IntentSourceJsonConverter))]
public readonly record struct IntentSource {
    private readonly byte m_kind;

    private IntentSource(byte kind, string? producerName) {
        m_kind = kind;
        ProducerName = producerName;
    }

    /// <summary>Gets the live submitted stream.</summary>
    public static IntentSource Live => default;

    /// <summary>Gets an input mask that fills gaps with zero.</summary>
    public static IntentSource Idle => new(kind: 1, producerName: null);

    /// <summary>Gets the producer-program name, or <see langword="null"/> for live and idle sources.</summary>
    public string? ProducerName { get; }

    /// <summary>Gets whether this source admits live submitted input.</summary>
    public bool IsLive => (m_kind == 0);

    /// <summary>Gets whether this source masks submitted input.</summary>
    public bool IsIdle => (m_kind == 1);

    /// <summary>Gets whether this source names an authored producer program.</summary>
    public bool IsProducer => (m_kind == 2);

    /// <summary>Creates a source naming an authored producer program.</summary>
    /// <param name="name">The producer program's stable name.</param>
    /// <returns>The producer source.</returns>
    public static IntentSource Producer(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);

        return new IntentSource(kind: 2, producerName: name);
    }

    /// <inheritdoc/>
    public override string ToString() => (m_kind switch {
        0 => "Live",
        1 => "Idle",
        2 => $"Producer({ProducerName})",
        _ => $"Unknown({m_kind})",
    });
}

/// <summary>Reads the closed intent-source union.</summary>
public sealed class IntentSourceJsonConverter : JsonConverter<IntentSource> {
    /// <inheritdoc/>
    public override IntentSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.String) {
            return reader.GetString() switch {
                "Live" => IntentSource.Live,
                "Idle" => IntentSource.Idle,
                var token => throw new JsonException(message: $"Unknown {nameof(IntentSource)} token '{token}'."),
            };
        }
        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException(message: $"Expected {nameof(IntentSource)} to be 'Live', 'Idle', or a producer object.");
        }

        string? discriminator = null;
        string? name = null;
        var sawDiscriminator = false;
        var sawName = false;

        while (reader.Read() && (reader.TokenType != JsonTokenType.EndObject)) {
            if (reader.TokenType != JsonTokenType.PropertyName) {
                throw new JsonException(message: $"Expected a property name in {nameof(IntentSource)}.");
            }

            var property = reader.GetString();
            if (!reader.Read()) {
                throw new JsonException(message: $"Truncated {nameof(IntentSource)}.");
            }

            switch (property) {
                case "$type" when !sawDiscriminator:
                    discriminator = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException(message: $"{nameof(IntentSource)}.$type must be a string.");
                    sawDiscriminator = true;
                    break;
                case "name" when !sawName:
                    name = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException(message: $"{nameof(IntentSource)}.name must be a string.");
                    sawName = true;
                    break;
                case "$type" or "name":
                    throw new JsonException(message: $"Duplicate {nameof(IntentSource)} member '{property}'.");
                default:
                    throw new JsonException(message: $"Unknown {nameof(IntentSource)} member '{property}'.");
            }
        }

        if (!string.Equals(a: discriminator, b: "producer", comparisonType: StringComparison.Ordinal)) {
            throw new JsonException(message: $"{nameof(IntentSource)}.$type must be 'producer'.");
        }

        return IntentSource.Producer(name: name ?? throw new JsonException(message: $"{nameof(IntentSource)}.name is required."));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IntentSource value, JsonSerializerOptions options) {
        if (value.IsLive) {
            writer.WriteStringValue(value: "Live");
        } else if (value.IsIdle) {
            writer.WriteStringValue(value: "Idle");
        } else if (value.IsProducer && (value.ProducerName is { } name)) {
            writer.WriteStartObject();
            writer.WriteString(propertyName: "$type", value: "producer");
            writer.WriteString(propertyName: "name", value: name);
            writer.WriteEndObject();
        } else {
            throw new JsonException(message: $"Unknown {nameof(IntentSource)} value '{value}'.");
        }
    }
}
