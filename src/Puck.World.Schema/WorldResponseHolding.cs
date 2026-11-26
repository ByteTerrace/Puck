using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// Which of a placement's response entries held at the last response sweep (<see cref="WorldPlacement.Holding"/>): a set
/// of entry indices held as machine words (<see cref="BitWords"/>), entry <c>i</c> at bit <c>i</c>, so a row declares as
/// many entries as <see cref="WorldResponseCapacity.MaxEntries"/> admits rather than as many as one integer has bits. A
/// display that can show any creation of a catalog is one row with an entry per creation.
/// <para>Immutable, and compared by the entries it holds: two holdings are equal exactly when the same entries hold,
/// whatever words carried them, since the words are trimmed of trailing zero words and the empty set holds no array.
/// Its document form is the ascending list of held entries (<c>holding [3]</c>), omitted when none holds.</para>
/// </summary>
[JsonConverter(typeof(WorldResponseHoldingJsonConverter))]
public readonly struct WorldResponseHolding : IEquatable<WorldResponseHolding> {
    // Trimmed of trailing zero words; null when no entry holds, so the default value is the empty set.
    private readonly ulong[]? m_words;

    private WorldResponseHolding(ulong[]? words) => m_words = words;

    /// <summary>Gets the holding in which no entry holds: the row shows its authored prototype.</summary>
    public static WorldResponseHolding None => default;

    /// <summary>Gets the number of entries that hold.</summary>
    public int Count => BitWords.Count(words: m_words);
    /// <summary>Gets the lowest held entry, the one the row shows, or <c>-1</c> when none holds.</summary>
    public int First {
        get {
            ReadOnlySpan<ulong> words = m_words;

            for (var word = 0; (word < words.Length); word++) {
                if (words[word] != 0UL) {
                    return ((word << 6) + BitOperations.TrailingZeroCount(value: words[word]));
                }
            }

            return -1;
        }
    }
    /// <summary>Gets whether no entry holds.</summary>
    public bool IsEmpty => (m_words is null);
    /// <summary>Gets one past the highest held entry; zero when none holds.</summary>
    public int Span => ((m_words is { } words)
        ? (((words.Length - 1) << 6) + (64 - BitOperations.LeadingZeroCount(value: words[^1])))
        : 0);

    /// <summary>Returns the words that carry the set, bit <c>i</c> of word <c>w</c> being entry <c>64 x w + i</c>.</summary>
    /// <returns>The words; empty when no entry holds.</returns>
    public ReadOnlySpan<ulong> AsWords() => m_words;
    /// <summary>Returns the held entries, in ascending order, so a <see langword="foreach"/> over the holding visits
    /// each held entry once.</summary>
    /// <returns>The enumerator.</returns>
    public BitWords.WordMembers GetEnumerator() => BitWords.Members(words: m_words);

    /// <summary>Returns the holding of a set of words, copied and trimmed.</summary>
    /// <param name="words">The words; bit <c>i</c> of word <c>w</c> is entry <c>64 x w + i</c>.</param>
    /// <returns>The holding.</returns>
    public static WorldResponseHolding Of(ReadOnlySpan<ulong> words) {
        var length = words.Length;

        while ((length > 0) && (words[(length - 1)] == 0UL)) {
            length--;
        }

        return ((length == 0) ? None : new WorldResponseHolding(words: words[..length].ToArray()));
    }
    /// <summary>Returns the holding in which exactly one entry holds.</summary>
    /// <param name="entry">The entry, zero or more.</param>
    /// <returns>The holding.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entry"/> is negative.</exception>
    public static WorldResponseHolding Single(int entry) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: entry);

        var words = new ulong[(BitWords.WordsFor(members: (entry + 1)))];

        BitWords.Add(index: entry, words: words);

        return new WorldResponseHolding(words: words);
    }
    /// <summary>Returns whether every held entry lies below a row's entry count.</summary>
    /// <param name="count">The row's entry count.</param>
    /// <returns><see langword="true"/> when no held entry is at or past <paramref name="count"/>.</returns>
    public bool Fits(int count) => (Span <= count);
    /// <summary>Returns whether an entry holds; an entry outside the set does not.</summary>
    /// <param name="entry">The entry.</param>
    /// <returns><see langword="true"/> when <paramref name="entry"/> holds.</returns>
    public bool Holds(int entry) => BitWords.Contains(index: entry, words: m_words);
    /// <summary>Returns this holding without one entry.</summary>
    /// <param name="entry">The entry to drop.</param>
    /// <returns>The holding; this one when <paramref name="entry"/> does not hold.</returns>
    public WorldResponseHolding Without(int entry) {
        if (!Holds(entry: entry)) {
            return this;
        }

        var words = m_words!.ToArray();

        BitWords.Remove(index: entry, words: words);

        return Of(words: words);
    }

    /// <inheritdoc/>
    public bool Equals(WorldResponseHolding other) => AsWords().SequenceEqual(other: other.AsWords());
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is WorldResponseHolding other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        foreach (var word in AsWords()) {
            hash.Add(value: word);
        }

        return hash.ToHashCode();
    }
    /// <summary>Returns the held entries as the document spells them: <c>[0, 3]</c>, or <c>[]</c> when none holds.</summary>
    /// <returns>The text.</returns>
    public override string ToString() {
        var builder = new StringBuilder(value: "[");
        var first = true;

        foreach (var entry in this) {
            _ = (first ? builder : builder.Append(value: ", ")).Append(provider: CultureInfo.InvariantCulture, handler: $"{entry}");
            first = false;
        }

        return builder.Append(value: ']').ToString();
    }

    /// <summary>Determines whether two holdings hold the same entries.</summary>
    /// <param name="left">The first holding.</param>
    /// <param name="right">The second holding.</param>
    /// <returns><see langword="true"/> when they hold the same entries.</returns>
    public static bool operator ==(WorldResponseHolding left, WorldResponseHolding right) => left.Equals(other: right);
    /// <summary>Determines whether two holdings hold different entries.</summary>
    /// <param name="left">The first holding.</param>
    /// <param name="right">The second holding.</param>
    /// <returns><see langword="true"/> when they hold different entries.</returns>
    public static bool operator !=(WorldResponseHolding left, WorldResponseHolding right) => !left.Equals(other: right);
}
/// <summary>The document form of a <see cref="WorldResponseHolding"/>: the ascending list of held entries, each a
/// non-negative integer, without repeats.</summary>
public sealed class WorldResponseHoldingJsonConverter : JsonConverter<WorldResponseHolding>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) => new() {
        ["type"] = "array",
        ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
    };
    /// <inheritdoc/>
    public override WorldResponseHolding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "A response holding is the ascending list of the entries that hold.");
        }

        var words = new List<ulong>();
        var last = -1;

        while (reader.Read() && (reader.TokenType != JsonTokenType.EndArray)) {
            if ((reader.TokenType != JsonTokenType.Number) || !reader.TryGetInt32(value: out var entry) || (entry <= last) || (entry >= WorldResponseCapacity.MaxEntries)) {
                throw new JsonException(message: $"A response holding lists each held entry once, ascending, in 0..{(WorldResponseCapacity.MaxEntries - 1)}.");
            }

            while (words.Count <= (entry >> 6)) {
                words.Add(item: 0UL);
            }

            words[(entry >> 6)] |= (1UL << (entry & 63));
            last = entry;
        }

        return WorldResponseHolding.Of(words: [.. words]);
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, WorldResponseHolding value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteStartArray();

        foreach (var entry in value) {
            writer.WriteNumberValue(value: entry);
        }

        writer.WriteEndArray();
    }
}
