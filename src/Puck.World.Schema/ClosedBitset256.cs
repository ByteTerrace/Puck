using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A fixed 256-bit membership set. Bit zero is the least significant bit of word zero.
/// Canonical JSON is one 64-digit hexadecimal string, most significant word first.</summary>
/// <param name="Word0">Bits 0 through 63.</param>
/// <param name="Word1">Bits 64 through 127.</param>
/// <param name="Word2">Bits 128 through 191.</param>
/// <param name="Word3">Bits 192 through 255.</param>
[JsonConverter(typeof(ClosedBitset256JsonConverter))]
public readonly record struct ClosedBitset256(ulong Word0 = 0, ulong Word1 = 0, ulong Word2 = 0, ulong Word3 = 0) {
    /// <summary>Gets the number of set bits.</summary>
    public int Count => BitOperations.PopCount(Word0) + BitOperations.PopCount(Word1) + BitOperations.PopCount(Word2) + BitOperations.PopCount(Word3);

    /// <summary>Gets whether every bit is clear.</summary>
    public bool IsEmpty => (Word0 | Word1 | Word2 | Word3) == 0;

    /// <summary>Tests membership; indices outside 0 through 255 are absent.</summary>
    /// <param name="index">The bit index.</param>
    /// <returns>Whether the indexed bit is set.</returns>
    public bool Contains(int index) => (uint)index < 256 && ((Word(index / 64) >> (index % 64)) & 1) != 0;

    /// <summary>Returns this set with one bit set.</summary>
    /// <param name="index">The bit index, 0 through 255.</param>
    /// <returns>The updated set.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the set.</exception>
    public ClosedBitset256 Add(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, 256);
        var bit = 1UL << (index % 64);
        return (index / 64) switch {
            0 => this with { Word0 = Word0 | bit },
            1 => this with { Word1 = Word1 | bit },
            2 => this with { Word2 = Word2 | bit },
            _ => this with { Word3 = Word3 | bit },
        };
    }

    /// <summary>Tests whether all set bits are below a declared element count.</summary>
    /// <param name="count">The admitted element count, 0 through 256.</param>
    /// <returns>Whether the set fits the declared count.</returns>
    public bool Fits(int count) {
        if ((uint)count > 256) {
            return false;
        }
        for (var word = 0; word < 4; word++) {
            var admitted = Math.Clamp(count - word * 64, 0, 64);
            if (admitted < 64 && (Word(word) >> admitted) != 0) {
                return false;
            }
        }
        return true;
    }

    private ulong Word(int index) => index switch { 0 => Word0, 1 => Word1, 2 => Word2, _ => Word3 };

    /// <summary>Returns the canonical 64-digit uppercase hexadecimal representation.</summary>
    /// <returns>The bitset text.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Word3:X16}{Word2:X16}{Word1:X16}{Word0:X16}");

    /// <summary>Parses exactly 64 hexadecimal digits.</summary>
    /// <param name="text">The hexadecimal representation.</param>
    /// <param name="value">The parsed set, or the empty set on failure.</param>
    /// <returns>Whether every digit is valid.</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out ClosedBitset256 value) {
        value = default;
        if (text.Length != 64 ||
            !ulong.TryParse(text[..16], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var w3) ||
            !ulong.TryParse(text.Slice(16, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var w2) ||
            !ulong.TryParse(text.Slice(32, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var w1) ||
            !ulong.TryParse(text.Slice(48, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var w0)) {
            return false;
        }
        value = new(Word0: w0, Word1: w1, Word2: w2, Word3: w3);
        return true;
    }
}

/// <summary>The strict hexadecimal wire representation of a 256-bit membership set.</summary>
public sealed class ClosedBitset256JsonConverter : JsonConverter<ClosedBitset256> {
    /// <inheritdoc/>
    public override ClosedBitset256 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String || !ClosedBitset256.TryParse(reader.GetString(), out var value)) {
            throw new JsonException("A 256-bit set must be a 64-digit hexadecimal string.");
        }
        return value;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ClosedBitset256 value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
