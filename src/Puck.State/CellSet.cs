using System.Numerics;

namespace Puck.State;

/// <summary>A topology-width membership set. Sets through 256 positions live entirely in the value; wider sets
/// own only the words past that inline prefix.</summary>
public readonly struct CellSet : IEquatable<CellSet> {
    private readonly ulong m_word0;
    private readonly ulong m_word1;
    private readonly ulong m_word2;
    private readonly ulong m_word3;
    private readonly ulong[]? m_tail;

    internal CellSet(int length, ulong word0, ulong word1, ulong word2, ulong word3, ulong[]? tail) {
        Length = length;
        m_word0 = word0;
        m_word1 = word1;
        m_word2 = word2;
        m_word3 = word3;
        m_tail = tail;
    }

    /// <summary>Gets the number of positions the set addresses.</summary>
    public int Length { get; }
    /// <summary>Gets the number of members.</summary>
    public int Count {
        get {
            var count = (((BitOperations.PopCount(value: m_word0)
                + BitOperations.PopCount(value: m_word1))
                + BitOperations.PopCount(value: m_word2))
                + BitOperations.PopCount(value: m_word3));

            foreach (var word in (m_tail ?? [])) {
                count += BitOperations.PopCount(value: word);
            }
            return count;
        }
    }
    /// <summary>Gets whether every position is clear.</summary>
    public bool IsEmpty {
        get {
            if ((m_word0 | m_word1 | m_word2 | m_word3) != 0UL) {
                return false;
            }
            foreach (var word in (m_tail ?? [])) {
                if (word != 0UL) {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Tests membership; positions outside this set's declared width are absent.</summary>
    /// <param name="index">The position.</param>
    public bool Contains(int index) => ((((uint)index) < ((uint)Length)) && ((Word(index: (index / 64)) & (1UL << (index % 64))) != 0UL));
    /// <summary>Tests whether this set can be read as a set over <paramref name="count"/> positions without losing
    /// a member.</summary>
    /// <param name="count">The proposed width.</param>
    public bool Fits(int count) {
        if (count < 0) {
            return false;
        }
        if (count > Length) {
            return true;
        }
        for (var word = WordCount(length: count); (word < WordCount(length: Length)); word++) {
            if (Word(index: word) != 0UL) {
                return false;
            }
        }
        var admitted = (count % 64);

        return ((admitted == 0) || ((Word(index: (count / 64)) >> admitted) == 0UL));
    }
    /// <summary>Returns this set with one position set.</summary>
    /// <param name="index">The position, inside this set's declared width.</param>
    public CellSet Add(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            index,
            Length
        );
        var word = (index / 64);
        var bit = (1UL << (index % 64));

        if ((Word(index: word) & bit) != 0UL) {
            return this;
        }
        if (word < 4) {
            return new(
                Length,
                ((word == 0) ? m_word0 | bit : m_word0),
                ((word == 1) ? m_word1 | bit : m_word1),
                ((word == 2) ? m_word2 | bit : m_word2),
                ((word == 3) ? m_word3 | bit : m_word3),
                m_tail
            );
        }
        var tail = ((ulong[])m_tail!.Clone());

        tail[(word - 4)] |= bit;
        return new(
            Length,
            m_word0,
            m_word1,
            m_word2,
            m_word3,
            tail
        );
    }

    internal static CellSet Empty(int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new(
            length,
            0UL,
            0UL,
            0UL,
            0UL,
            ((length > 256)
                ? new ulong[(WordCount(length: length) - 4)]
                : null)
        );
    }
    internal static int WordCount(int length) => ((length / 64) + (((length % 64) == 0) ? 0 : 1));
    internal ulong Word(int index) => index switch {
        0 => m_word0,
        1 => m_word1,
        2 => m_word2,
        3 => m_word3,
        _ => m_tail![(index - 4)],
    };

    /// <inheritdoc/>
    public bool Equals(CellSet other) {
        if (Length != other.Length) {
            return false;
        }
        for (var word = 0; (word < WordCount(length: Length)); word++) {
            if (Word(index: word) != other.Word(index: word)) {
                return false;
            }
        }
        return true;
    }
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is CellSet other) && Equals(other: other));
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        hash.Add(value: Length);
        for (var word = 0; (word < WordCount(length: Length)); word++) {
            hash.Add(value: Word(index: word));
        }
        return hash.ToHashCode();
    }

    /// <summary>Compares two sets structurally.</summary>
    public static bool operator ==(CellSet left, CellSet right) => left.Equals(other: right);
    /// <summary>Compares two sets structurally.</summary>
    public static bool operator !=(CellSet left, CellSet right) => !left.Equals(other: right);
}
