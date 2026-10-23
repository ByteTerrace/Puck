using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Commands;
using Puck.Maths;

namespace Puck.World.Protocol;

/// <summary>Independent active input bank budgets. Fold scratch is preallocated against their sum.</summary>
public static class ChannelLimits {
    /// <summary>The total addressable ordinals.</summary>
    public const int MaxChannels = (MaxAxes + MaxButtons);
    /// <summary>The numeric bank's capacity.</summary>
    public const int MaxAxes = 32;
    /// <summary>The packed button bank's capacity.</summary>
    public const int MaxButtons = 64;
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

    /// <summary>A Q32.32 displacement accumulated since the preceding input submission, consumed once.</summary>
    Relative,
}
/// <summary>The inline numeric bank; copying it allocates nothing.</summary>
[InlineArray(ChannelLimits.MaxAxes)]
public struct ChannelAxes {
    private long m_element0;
}
/// <summary>Thirty-two numeric lanes and sixty-four packed held buttons. Compiled addresses below
/// <see cref="ChannelLimits.MaxAxes"/> select numeric lanes; the remaining addresses select buttons.</summary>
public struct ChannelValues {
    /// <summary>The numeric value bank.</summary>
    public ChannelAxes Axes;
    /// <summary>The packed held button bank.</summary>
    public ulong Buttons;

    /// <summary>Reads a numeric bank slot as Q32.32; resolve and validate its shape through WorldChannelTable first.</summary>
    public readonly FixedQ3232 Relative(int ordinal) {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, ChannelLimits.MaxAxes);
        return FixedQ3232.FromRawBits(value: Axes[ordinal]);
    }
    /// <summary>Writes a numeric bank slot as Q32.32; resolve and validate its shape through WorldChannelTable first.</summary>
    public void SetRelative(int ordinal, FixedQ3232 value) {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, ChannelLimits.MaxAxes);
        Axes[ordinal] = value.Value;
    }

    /// <summary>Reads or writes an already-quantized level through its compiled bank address.</summary>
    public FixedQ4816 this[int ordinal] {
        readonly get {
            ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, ChannelLimits.MaxChannels);
            return ((ordinal < ChannelLimits.MaxAxes) ? FixedQ4816.FromRawBits(value: Axes[ordinal])
                : (((Buttons & (1UL << (ordinal - ChannelLimits.MaxAxes))) != 0) ? FixedQ4816.One : FixedQ4816.Zero));
        }
        set {
            ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, ChannelLimits.MaxChannels);
            if (ordinal < ChannelLimits.MaxAxes) {
                Axes[ordinal] = value.Value;
            } else {
                if ((value != FixedQ4816.Zero) && (value != FixedQ4816.One)) {
                    throw new ArgumentOutOfRangeException(nameof(value), "A packed button requires a quantized zero or one.");
                }
                var bit = (1UL << (ordinal - ChannelLimits.MaxAxes));

                Buttons = ((value == FixedQ4816.One) ? Buttons | bit : Buttons & ~bit);
            }
        }
    }
}
/// <summary>One tick's packed levels, addressed through the compiled world channel table, and optional pointer ray.</summary>
/// <param name="Channels">The numeric and button banks.</param>
/// <param name="SourceRay">The seat's world-space pointer ray this tick, quantized once at the host
/// (<see cref="Puck.Commands.CommandValueQuantization.QuantizeAxis3D"/>), or <see langword="null"/> when the seat points
/// at nothing. The server maps it through a <c>Simulation</c> screen's row in fixed point
/// (<see cref="WorldScreenMappings.Normalized"/>) and never trusts a host-computed hit.</param>
public readonly record struct PlayerIntent(ChannelValues Channels, SourceRay? SourceRay = null) {
    /// <summary>Reads one channel's raw value by ordinal — zero for an ordinal no declared channel claims.</summary>
    /// <param name="ordinal">The channel ordinal (<c>0..</c><see cref="ChannelLimits.MaxChannels"/><c>-1</c>).</param>
    public FixedQ4816 this[int ordinal] => Channels[ordinal];

    /// <summary>Determines structural equality with <paramref name="other"/> over the WHOLE vector, comparing per
    /// ordinal, and the pointer ray. Declared explicitly because the compiler-synthesized record-struct equality would
    /// otherwise compare <see cref="ChannelValues"/> as a single opaque field (an <c>InlineArray</c> exposes only its
    /// first backing field to reflection-based equality) — silently comparing only ordinal 0 instead of the
    /// vector.</summary>
    public bool Equals(PlayerIntent other) {
        for (var ordinal = 0; (ordinal < ChannelLimits.MaxAxes); ordinal++) {
            if (Channels.Axes[ordinal] != other.Channels.Axes[ordinal]) {
                return false;
            }
        }

        return ((Channels.Buttons == other.Channels.Buttons) && (SourceRay == other.SourceRay));
    }
    /// <summary>Returns a hash consistent with <see cref="Equals(PlayerIntent)"/> — folded over every ordinal and the
    /// pointer ray, for the same reason <see cref="Equals(PlayerIntent)"/> is declared explicitly.</summary>
    public override int GetHashCode() {
        var hash = new HashCode();

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxAxes); ordinal++) {
            hash.Add(value: Channels.Axes[ordinal]);
        }

        hash.Add(value: Channels.Buttons);
        hash.Add(value: SourceRay);

        return hash.ToHashCode();
    }
    /// <summary>Returns this image with one Q32.32 displacement replaced.</summary>
    public PlayerIntent WithRelative(int ordinal, FixedQ3232 value) {
        var channels = Channels;

        channels.SetRelative(ordinal: ordinal, value: value);
        return this with { Channels = channels };
    }
    /// <summary>Returns this intent with one ordinal replaced — the composition-channel write path (a bound kit
    /// effect, a wire press, a held device value). The pointer ray is kept.</summary>
    /// <param name="ordinal">The channel ordinal to replace.</param>
    /// <param name="value">The raw fixed-point value to write.</param>
    public PlayerIntent WithChannel(int ordinal, FixedQ4816 value) {
        var channels = Channels;

        channels[ordinal] = value;

        return (this with { Channels = channels });
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
    public static IntentSource Idle => new(
        kind: 1,
        producerName: null
    );
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

        return new IntentSource(
            kind: 2,
            producerName: name
        );
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
public sealed class IntentSourceJsonConverter : JsonConverter<IntentSource>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["anyOf"] = new JsonArray(
        new JsonObject {
            ["type"] = "string",
            ["enum"] = new JsonArray(
            "Live",
            "Idle"
        ),
        },
        new JsonObject {
            ["type"] = "object",
            ["properties"] = new JsonObject {
                ["$type"] = new JsonObject { ["const"] = "producer" },
                ["name"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray(
            "$type",
            "name"
        ),
            ["additionalProperties"] = false,
        }
    ),
    };
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

        while (
            reader.Read() &&
            (reader.TokenType != JsonTokenType.EndObject)
        ) {
            if (reader.TokenType != JsonTokenType.PropertyName) {
                throw new JsonException(message: $"Expected a property name in {nameof(IntentSource)}.");
            }

            var property = reader.GetString();

            if (!reader.Read()) {
                throw new JsonException(message: $"Truncated {nameof(IntentSource)}.");
            }

            switch (property) {
                case "$type" when !sawDiscriminator:
                    discriminator = ((reader.TokenType == JsonTokenType.String)
                        ? reader.GetString()
                        : throw new JsonException(message: $"{nameof(IntentSource)}.$type must be a string.")
                    );
                    sawDiscriminator = true;
                    break;
                case "name" when !sawName:
                    name = ((reader.TokenType == JsonTokenType.String)
                        ? reader.GetString()
                        : throw new JsonException(message: $"{nameof(IntentSource)}.name must be a string.")
                    );
                    sawName = true;
                    break;
                case "$type" or "name":
                    throw new JsonException(message: $"Duplicate {nameof(IntentSource)} member '{property}'.");
                default:
                    throw new JsonException(message: $"Unknown {nameof(IntentSource)} member '{property}'.");
            }
        }

        if (!string.Equals(
            a: discriminator,
            b: "producer",
            comparisonType: StringComparison.Ordinal
        )) {
            throw new JsonException(message: $"{nameof(IntentSource)}.$type must be 'producer'.");
        }

        return IntentSource.Producer(name: (name ?? throw new JsonException(message: $"{nameof(IntentSource)}.name is required.")));
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IntentSource value, JsonSerializerOptions options) {
        if (value.IsLive) {
            writer.WriteStringValue(value: "Live");
        } else if (value.IsIdle) {
            writer.WriteStringValue(value: "Idle");
        } else if (
            value.IsProducer &&
            (value.ProducerName is { } name)
        ) {
            writer.WriteStartObject();
            writer.WriteString(
                propertyName: "$type",
                value: "producer"
            );
            writer.WriteString(
                propertyName: "name",
                value: name
            );
            writer.WriteEndObject();
        } else {
            throw new JsonException(message: $"Unknown {nameof(IntentSource)} value '{value}'.");
        }
    }
}
