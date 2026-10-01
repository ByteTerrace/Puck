using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>How a key shapes time on its way to the next key: the ease of the segment that starts at it.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldEase>))]
public enum WorldEase {
    /// <summary>Time runs evenly from this key to the next.</summary>
    Linear = 0,

    /// <summary>Time starts and ends slowly: <c>t² (3 − 2t)</c>, so the value leaves and arrives at rest.</summary>
    Smooth = 1,

    /// <summary>The value holds this key's until the next key, where it changes at once.</summary>
    Step = 2,
}
/// <summary>One key of a keyed value: where on its clock it sits, the value it holds there, and how time eases from it
/// to the next key.</summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <param name="At">Where on the clock the key sits, in the clock's span units (a time of day on a clock whose span
/// is <c>24h</c>), in <c>[0, span)</c>.</param>
/// <param name="Value">The value the key holds.</param>
/// <param name="Ease">How time eases from this key to the next.</param>
public readonly record struct WorldKey<T>(double At, T Value, WorldEase Ease);
/// <summary>
/// A value keyed on a clock: the clock it reads, by name in the <c>timeline</c> section, and its keys, ascending in
/// <see cref="WorldKey{T}.At"/>. The value between two keys is the two keys' values blended by the field's type at the
/// eased fraction of the way between them, and the last key wraps into the first, since every clock is periodic. A
/// track compares by value, so two documents that author the same keys are equal.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed class WorldKeyTrack<T> : IEquatable<WorldKeyTrack<T>>, IWorldKeyTrack {
    /// <summary>Initializes a new instance of the <see cref="WorldKeyTrack{T}"/> class.</summary>
    /// <param name="clock">The clock's name.</param>
    /// <param name="keys">The keys, in authored order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is <see langword="null"/>.</exception>
    public WorldKeyTrack(string clock, ImmutableArray<WorldKey<T>> keys) {
        ArgumentNullException.ThrowIfNull(argument: clock);

        Clock = clock;
        Keys = (keys.IsDefault
            ? []
            : keys
        );
    }

    /// <summary>Gets the name of the clock the track reads.</summary>
    public string Clock { get; }
    /// <summary>Gets the keys, in authored order.</summary>
    public ImmutableArray<WorldKey<T>> Keys { get; }
    /// <inheritdoc/>
    public int Count => Keys.Length;

    /// <inheritdoc/>
    public double AtOf(int index) => Keys[index].At;
    /// <inheritdoc/>
    public bool Equals(WorldKeyTrack<T>? other) {
        if (other is null) {
            return false;
        }

        if (ReferenceEquals(
            objA: this,
            objB: other
        )) {
            return true;
        }

        return (
            string.Equals(
            a: Clock,
            b: other.Clock,
            comparisonType: StringComparison.Ordinal
        ) &&
            Keys.AsSpan().SequenceEqual(other: other.Keys.AsSpan())
        );
    }
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as WorldKeyTrack<T>));
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        hash.Add(value: Clock, comparer: StringComparer.Ordinal);

        foreach (var key in Keys) {
            hash.Add(value: key);
        }

        return hash.ToHashCode();
    }
    /// <inheritdoc/>
    public override string ToString() => $"keys(clock: {Clock}, {Keys.Length} keys)";
}

/// <summary>Reads one key's value of a keyed track.</summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <param name="reader">The reader, positioned on the value's first token.</param>
/// <returns>The value.</returns>
public delegate T WorldKeyValueReader<T>(ref Utf8JsonReader reader);

/// <summary>The one wire form of a keyed value, which every bindable's converter reads and writes beside its literal
/// and binding arms: <c>{ "clock": "&lt;name&gt;", "keys": [ { "at": n, "value": v, "ease": "Smooth" } ] }</c>,
/// <c>ease</c> optional and <see cref="WorldEase.Linear"/> when absent. Any other member is refused by name.</summary>
public static class WorldKeyTrackJson {
    /// <summary>The refusal every keyed arm shares.</summary>
    public const string Grammar = "{ clock: <name>, keys [ { at: <time>, value: <value>, ease: Linear|Smooth|Step } ] }";

    /// <summary>Reads a keyed track from the reader positioned on its object's start.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="reader">The reader, on a <see cref="JsonTokenType.StartObject"/>.</param>
    /// <param name="readValue">Reads one key's value.</param>
    /// <param name="kind">The bindable's name, for refusals.</param>
    /// <returns>The track.</returns>
    /// <exception cref="JsonException">The object is not a keyed track.</exception>
    public static WorldKeyTrack<T> Read<T>(ref Utf8JsonReader reader, WorldKeyValueReader<T> readValue, string kind) {
        ArgumentNullException.ThrowIfNull(argument: readValue);

        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException(message: $"A keyed {kind} must be an object {Grammar}.");
        }

        string? clock = null;
        ImmutableArray<WorldKey<T>>.Builder? keys = null;

        while (reader.Read() && (reader.TokenType != JsonTokenType.EndObject)) {
            var member = reader.GetString();

            _ = reader.Read();

            switch (member) {
                case "clock":
                    if (reader.TokenType != JsonTokenType.String) {
                        throw new JsonException(message: $"A keyed {kind}'s clock must be a clock's name.");
                    }

                    clock = reader.GetString();

                    break;
                case "keys":
                    if (reader.TokenType != JsonTokenType.StartArray) {
                        throw new JsonException(message: $"A keyed {kind}'s keys must be an array.");
                    }

                    keys = ImmutableArray.CreateBuilder<WorldKey<T>>();

                    while (reader.Read() && (reader.TokenType != JsonTokenType.EndArray)) {
                        keys.Add(item: ReadKey(
                            kind: kind,
                            readValue: readValue,
                            reader: ref reader
                        ));
                    }

                    break;
                default:
                    throw new JsonException(message: $"A keyed {kind} has no member '{member}'; it is {Grammar}.");
            }
        }

        if (clock is null) {
            throw new JsonException(message: $"A keyed {kind} must name its clock.");
        }

        if (keys is null) {
            throw new JsonException(message: $"A keyed {kind} must carry its keys.");
        }

        return new WorldKeyTrack<T>(
            clock: clock,
            keys: keys.ToImmutable()
        );
    }
    /// <summary>Writes a keyed track.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="writer">The writer.</param>
    /// <param name="track">The track.</param>
    /// <param name="writeValue">Writes one key's value.</param>
    public static void Write<T>(Utf8JsonWriter writer, WorldKeyTrack<T> track, Action<Utf8JsonWriter, T> writeValue) {
        ArgumentNullException.ThrowIfNull(argument: writer);
        ArgumentNullException.ThrowIfNull(argument: track);
        ArgumentNullException.ThrowIfNull(argument: writeValue);

        writer.WriteStartObject();
        writer.WriteString(
            propertyName: "clock",
            value: track.Clock
        );
        writer.WriteStartArray(propertyName: "keys");

        foreach (var key in track.Keys) {
            writer.WriteStartObject();
            writer.WriteNumber(
                propertyName: "at",
                value: key.At
            );
            writer.WritePropertyName(propertyName: "value");
            writeValue(writer, key.Value);

            if (key.Ease != WorldEase.Linear) {
                writer.WriteString(
                    propertyName: "ease",
                    value: key.Ease.ToString()
                );
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
    /// <summary>Builds the schema node of a keyed track whose key values take a given schema.</summary>
    /// <param name="value">The schema of one key's value.</param>
    /// <returns>The keyed arm's schema.</returns>
    public static JsonObject Schema(JsonNode value) => new() {
        ["type"] = "object",
        ["properties"] = new JsonObject {
            ["clock"] = new JsonObject { ["type"] = "string" },
            ["keys"] = new JsonObject {
                ["type"] = "array",
                ["items"] = new JsonObject {
                    ["type"] = "object",
                    ["properties"] = new JsonObject {
                        ["at"] = new JsonObject { ["type"] = "number" },
                        ["value"] = value,
                        ["ease"] = new JsonObject {
                            ["type"] = "string",
                            ["enum"] = new JsonArray(
                                nameof(WorldEase.Linear),
                                nameof(WorldEase.Smooth),
                                nameof(WorldEase.Step)
                            ),
                        },
                    },
                    ["required"] = new JsonArray(
                        "at",
                        "value"
                    ),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray(
            "clock",
            "keys"
        ),
        ["additionalProperties"] = false,
    };

    private static WorldKey<T> ReadKey<T>(ref Utf8JsonReader reader, WorldKeyValueReader<T> readValue, string kind) {
        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException(message: $"A keyed {kind}'s key must be an object {{ at, value, ease }}.");
        }

        double? at = null;
        var ease = WorldEase.Linear;
        var hasValue = false;
        T value = default!;

        while (reader.Read() && (reader.TokenType != JsonTokenType.EndObject)) {
            var member = reader.GetString();

            _ = reader.Read();

            switch (member) {
                case "at":
                    if (reader.TokenType != JsonTokenType.Number) {
                        throw new JsonException(message: $"A keyed {kind}'s key 'at' must be a number.");
                    }

                    at = reader.GetDouble();

                    break;
                case "value":
                    value = readValue(reader: ref reader);
                    hasValue = true;

                    break;
                case "ease":
                    ease = (reader.GetString() switch {
                        nameof(WorldEase.Linear) => WorldEase.Linear,
                        nameof(WorldEase.Smooth) => WorldEase.Smooth,
                        nameof(WorldEase.Step) => WorldEase.Step,
                        var other => throw new JsonException(message: $"A keyed {kind}'s key ease '{other}' is not Linear, Smooth or Step."),
                    });

                    break;
                default:
                    throw new JsonException(message: $"A keyed {kind}'s key has no member '{member}'; it is {{ at, value, ease }}.");
            }
        }

        if (at is not { } time) {
            throw new JsonException(message: $"A keyed {kind}'s key must say where it sits ('at').");
        }

        if (!hasValue) {
            throw new JsonException(message: $"A keyed {kind}'s key must carry a value.");
        }

        return new WorldKey<T>(
            At: time,
            Ease: ease,
            Value: value
        );
    }
}
