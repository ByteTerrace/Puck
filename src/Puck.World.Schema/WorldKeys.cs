using Puck.Maths;
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
/// <summary>How a keyed scalar's track joins the value beneath it: the track it follows in a chain
/// (<see cref="WorldKeyTrack{T}.With"/>), or a section's keys on the field it fills (<see cref="WorldRenderKeys"/>). Each
/// combine has an identity, so a track with nothing beneath it is its own value.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldKeyCombine>))]
public enum WorldKeyCombine {
    /// <summary>The two values add: a lift over a base.</summary>
    Add = 0,

    /// <summary>The two values multiply: a fade of a base.</summary>
    Multiply = 1,

    /// <summary>The greater of the two: a floor that either clock may raise.</summary>
    Maximum = 2,

    /// <summary>The lesser of the two: a ceiling that either clock may lower.</summary>
    Minimum = 3,
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
/// track compares by value, so two documents that author the same keys are equal. A scalar's track may carry a second
/// track on another clock (<see cref="With"/>) that joins it by that track's <see cref="Combine"/>, so one value follows
/// two clocks at once (an exposure floor that follows both the day and the viewer's height).
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed class WorldKeyTrack<T> : IEquatable<WorldKeyTrack<T>>, IWorldKeyTrack {
    /// <summary>Initializes a new instance of the <see cref="WorldKeyTrack{T}"/> class.</summary>
    /// <param name="clock">The clock's name.</param>
    /// <param name="keys">The keys, in authored order.</param>
    /// <param name="combine">How this track joins the value beneath it, or <see langword="null"/> for none authored.</param>
    /// <param name="with">The track that joins this one, on its own clock, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is <see langword="null"/>.</exception>
    public WorldKeyTrack(string clock, ImmutableArray<WorldKey<T>> keys, WorldKeyCombine? combine = null, WorldKeyTrack<T>? with = null) {
        ArgumentNullException.ThrowIfNull(argument: clock);

        Clock = clock;
        Combine = combine;
        With = with;
        Keys = (keys.IsDefault
            ? []
            : keys
        );
    }

    /// <summary>Gets the name of the clock the track reads.</summary>
    public string Clock { get; }
    /// <summary>Gets the keys, in authored order.</summary>
    public ImmutableArray<WorldKey<T>> Keys { get; }
    /// <summary>Gets how this track joins the value beneath it, or <see langword="null"/> when none is authored, which
    /// joins as <see cref="WorldKeyCombine.Add"/>.</summary>
    public WorldKeyCombine? Combine { get; }
    /// <summary>Gets the track that joins this one by its own <see cref="Combine"/>, on its own clock, or
    /// <see langword="null"/>.</summary>
    public WorldKeyTrack<T>? With { get; }
    /// <inheritdoc/>
    public int Count => Keys.Length;
    /// <inheritdoc/>
    IWorldKeyTrack? IWorldKeyTrack.With => With;

    /// <summary>Returns this track joined over another: <paramref name="beneath"/> and its chain first, then this
    /// track and its own chain, each link by its combine.</summary>
    /// <param name="beneath">The track this one joins.</param>
    /// <returns>The chained track.</returns>
    public WorldKeyTrack<T> Over(WorldKeyTrack<T> beneath) {
        ArgumentNullException.ThrowIfNull(argument: beneath);

        return new WorldKeyTrack<T>(
            clock: beneath.Clock,
            combine: beneath.Combine,
            keys: beneath.Keys,
            with: ((beneath.With is { } next) ? Over(beneath: next) : this)
        );
    }

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
            Keys.AsSpan().SequenceEqual(other: other.Keys.AsSpan()) &&
            (Combine == other.Combine) &&
            Equals(objA: With, objB: other.With)
        );
    }
    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(other: (obj as WorldKeyTrack<T>));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Clock, SequenceHash.Of(Keys.AsSpan()), Combine, With);
    /// <inheritdoc/>
    public override string ToString() => ((With is { } with)
        ? $"keys(clock: {Clock}, {Keys.Length} keys, with {with})"
        : $"keys(clock: {Clock}, {Keys.Length} keys)");
}

/// <summary>Reads one key's value of a keyed track.</summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <param name="reader">The reader, positioned on the value's first token.</param>
/// <returns>The value.</returns>
public delegate T WorldKeyValueReader<T>(ref Utf8JsonReader reader);

/// <summary>The one wire form of a keyed value, which every bindable's converter reads and writes beside its literal
/// and binding arms: <c>{ "clock": "&lt;name&gt;", "keys": [ { "at": n, "value": v, "ease": "Smooth" } ] }</c>,
/// <c>ease</c> optional and <see cref="WorldEase.Linear"/> when absent. A track that combines (a scalar's) also reads
/// <c>combine</c> (<see cref="WorldKeyCombine"/>) and <c>with</c>, a second track of the same form on its own clock.
/// Any other member is refused by name.</summary>
public static class WorldKeyTrackJson {
    /// <summary>The refusal every keyed arm shares.</summary>
    public const string Grammar = "{ clock: <name>, keys [ { at: <time>, value: <value>, ease: Linear|Smooth|Step } ] }";

    /// <summary>Reads a keyed track from the reader positioned on its object's start.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="reader">The reader, on a <see cref="JsonTokenType.StartObject"/>.</param>
    /// <param name="readValue">Reads one key's value.</param>
    /// <param name="kind">The bindable's name, for refusals.</param>
    /// <param name="combines">Whether the track may carry <c>combine</c> and <c>with</c>.</param>
    /// <returns>The track.</returns>
    /// <exception cref="JsonException">The object is not a keyed track.</exception>
    public static WorldKeyTrack<T> Read<T>(ref Utf8JsonReader reader, WorldKeyValueReader<T> readValue, string kind, bool combines = false) {
        ArgumentNullException.ThrowIfNull(argument: readValue);

        if (reader.TokenType != JsonTokenType.StartObject) {
            throw new JsonException(message: $"A keyed {kind} must be an object {Grammar}.");
        }

        string? clock = null;
        WorldKeyCombine? combine = null;
        ImmutableArray<WorldKey<T>>.Builder? keys = null;
        WorldKeyTrack<T>? with = null;

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
                case "combine" when combines:
                    combine = ((Enum.TryParse(ignoreCase: true, result: out WorldKeyCombine parsedCombine, value: reader.GetString()) && Enum.IsDefined(value: parsedCombine) && !int.TryParse(s: reader.GetString(), result: out _))
                        ? parsedCombine
                        : throw new JsonException(message: $"A keyed {kind}'s combine '{reader.GetString()}' is not Add, Multiply, Maximum or Minimum."));

                    break;
                case "with" when combines:
                    if (reader.TokenType != JsonTokenType.Null) {
                        with = Read(
                            combines: true,
                            kind: kind,
                            readValue: readValue,
                            reader: ref reader
                        );
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
            combine: combine,
            keys: keys.ToImmutable(),
            with: with
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

        if (track.Combine is { } combine) {
            writer.WriteString(
                propertyName: "combine",
                value: combine.ToString()
            );
        }

        if (track.With is { } with) {
            writer.WritePropertyName(propertyName: "with");
            Write(
                track: with,
                writeValue: writeValue,
                writer: writer
            );
        }

        writer.WriteEndObject();
    }
    /// <summary>Builds the schema node of a keyed track whose key values take a given schema.</summary>
    /// <param name="value">The schema of one key's value.</param>
    /// <param name="combines">Whether the track may carry <c>combine</c> and <c>with</c>.</param>
    /// <returns>The keyed arm's schema.</returns>
    public static JsonObject Schema(JsonNode value, bool combines = false) {
        var schema = TrackSchema(value: value);

        if (combines) {
            var combine = new JsonObject {
                ["type"] = "string",
                ["enum"] = new JsonArray(
                    nameof(WorldKeyCombine.Add),
                    nameof(WorldKeyCombine.Multiply),
                    nameof(WorldKeyCombine.Maximum),
                    nameof(WorldKeyCombine.Minimum)
                ),
            };
            var with = TrackSchema(value: value.DeepClone());

            ((JsonObject)with["properties"]!)["combine"] = combine.DeepClone();
            ((JsonObject)schema["properties"]!)["combine"] = combine;
            ((JsonObject)schema["properties"]!)["with"] = with;
        }

        return schema;
    }
    private static JsonObject TrackSchema(JsonNode value) => new() {
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
                    // An ease reads in any case, as every enumeration of the document does, and never as a number.
                    ease = ((Enum.TryParse(ignoreCase: true, result: out WorldEase parsed, value: reader.GetString()) && Enum.IsDefined(value: parsed) && !int.TryParse(s: reader.GetString(), result: out _))
                        ? parsed
                        : throw new JsonException(message: $"A keyed {kind}'s key ease '{reader.GetString()}' is not Linear, Smooth or Step."));

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
