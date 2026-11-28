using System.Numerics;

namespace Puck.Maths;

/// <summary>Membership sets held as machine words: a <see cref="UInt128"/> mask for a set of at most 128 members, and a
/// span of <see cref="ulong"/> words for a wider one. Bit <c>i</c> is member <c>i</c>, least significant first, so a
/// mask's words are the same bits a GPU reads as four <c>uint</c> words (<see cref="Word32"/>). Iteration visits set bits
/// in ascending order by trailing-zero counts, so it costs one step a member, never one a position.</summary>
public static class BitWords {
    /// <summary>The members a <see cref="UInt128"/> mask holds.</summary>
    public const int MaskBits = 128;

    /// <summary>Returns the mask with only one member set.</summary>
    /// <param name="index">The member, 0 through 127.</param>
    /// <returns>The single-member mask.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0 through 127.</exception>
    public static UInt128 Bit(int index) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: (uint)index, other: (uint)MaskBits, paramName: nameof(index));

        return (UInt128.One << index);
    }
    /// <summary>Returns whether a member is set; a member outside 0 through 127 is absent.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="index">The member.</param>
    /// <returns>Whether the member is set.</returns>
    public static bool Contains(UInt128 mask, int index) => ((((uint)index) < MaskBits) && (((mask >> index) & UInt128.One) != UInt128.Zero));
    /// <summary>Returns the number of members set.</summary>
    /// <param name="mask">The mask.</param>
    /// <returns>The member count, 0 through 128.</returns>
    public static int Count(UInt128 mask) => (int)UInt128.PopCount(value: mask);
    /// <summary>Returns one past the highest member set: the shortest prefix of positions that holds every member, which
    /// bounds a loop or a dispatch over the set; zero for the empty mask.</summary>
    /// <param name="mask">The mask.</param>
    /// <returns>The prefix length, 0 through 128.</returns>
    public static int Span(UInt128 mask) => (MaskBits - (int)UInt128.LeadingZeroCount(value: mask));
    /// <summary>Returns one 32-bit word of the mask, the word a GPU reads as component <paramref name="word"/> of a
    /// <c>uint4</c>.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="word">The word, 0 through 3.</param>
    /// <returns>Bits <c>32 x word</c> through <c>32 x word + 31</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="word"/> is outside 0 through 3.</exception>
    public static uint Word32(UInt128 mask, int word) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: (uint)word, other: 4u, paramName: nameof(word));

        return (uint)(mask >> (32 * word));
    }
    /// <summary>Returns an enumerator over the members a mask sets, in ascending order.</summary>
    /// <param name="mask">The mask.</param>
    /// <returns>The enumerator, usable in a <see langword="foreach"/>.</returns>
    public static MaskMembers Members(UInt128 mask) => new(mask: mask);
    /// <summary>Returns an enumerator over the members a span of words sets, in ascending order.</summary>
    /// <param name="words">The words; bit <c>i</c> of word <c>w</c> is member <c>64 x w + i</c>.</param>
    /// <returns>The enumerator, usable in a <see langword="foreach"/>.</returns>
    public static WordMembers Members(ReadOnlySpan<ulong> words) => new(words: words);
    /// <summary>Returns the number of words that hold a set of <paramref name="members"/> positions.</summary>
    /// <param name="members">The positions, zero or more.</param>
    /// <returns>The word count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="members"/> is negative.</exception>
    public static int WordsFor(int members) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: members);

        return (int)((((uint)members) + 63u) >> 6);
    }
    /// <summary>Returns whether a member of a span of words is set; a member past the span is absent.</summary>
    /// <param name="words">The words.</param>
    /// <param name="index">The member.</param>
    /// <returns>Whether the member is set.</returns>
    public static bool Contains(ReadOnlySpan<ulong> words, int index) => ((((uint)index) < (((uint)words.Length) << 6)) && (((words[index >> 6] >> (index & 63)) & 1UL) != 0UL));
    /// <summary>Sets a member of a span of words.</summary>
    /// <param name="words">The words.</param>
    /// <param name="index">The member, inside the span.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> lies past the span.</exception>
    public static void Add(Span<ulong> words, int index) => words[index >> 6] |= (1UL << (index & 63));
    /// <summary>Clears a member of a span of words.</summary>
    /// <param name="words">The words.</param>
    /// <param name="index">The member, inside the span.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> lies past the span.</exception>
    public static void Remove(Span<ulong> words, int index) => words[index >> 6] &= ~(1UL << (index & 63));
    /// <summary>Writes a span of words with every member moved a signed number of positions: member <c>i</c> of
    /// <paramref name="source"/> lands on member <c>i + distance</c> of <paramref name="destination"/>. A member moved
    /// below zero or past the destination's last word is dropped, and a position no member lands on is clear. Each
    /// destination word is formed from at most two source words, so the cost is one pass over the destination.</summary>
    /// <param name="source">The words to move; a word past its end reads as zero.</param>
    /// <param name="distance">The signed distance in positions, positive toward higher members.</param>
    /// <param name="destination">The moved words; must not overlap <paramref name="source"/>.</param>
    public static void Shift(ReadOnlySpan<ulong> source, int distance, Span<ulong> destination) {
        // floor(distance / 64) and the remainder in 0..63, so one formula moves either way: destination word w takes
        // source word w - q shifted up by r and the bits word w - q - 1 carries over.
        var quotient = (distance >> 6);
        var remainder = (distance & 63);

        for (var word = 0; (word < destination.Length); word++) {
            var from = (word - quotient);
            var low = ((((uint)from) < ((uint)source.Length)) ? source[from] : 0UL);
            var carry = ((((uint)(from - 1)) < ((uint)source.Length)) ? source[(from - 1)] : 0UL);

            destination[word] = ((remainder == 0)
                ? low
                : ((low << remainder) | (carry >> (64 - remainder))));
        }
    }
    /// <summary>Writes the members of one run of positions of a span of words, renumbered from zero: member
    /// <c>start + i</c> of <paramref name="words"/> lands on member <c>i</c> of <paramref name="destination"/> for every
    /// <c>i</c> below <paramref name="count"/>, and every later destination bit is clear.</summary>
    /// <param name="words">The words to read; a position past their end reads as clear.</param>
    /// <param name="start">The first position of the run, zero or more.</param>
    /// <param name="count">How many positions the run holds, zero or more.</param>
    /// <param name="destination">The run's members, at least <see cref="WordsFor"/>(<paramref name="count"/>) words;
    /// must not overlap <paramref name="words"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> or <paramref name="count"/> is
    /// negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than the run's words.</exception>
    public static void Extract(ReadOnlySpan<ulong> words, int start, int count, Span<ulong> destination) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: start);

        var length = WordsFor(members: count);

        if (destination.Length < length) {
            throw new ArgumentException(message: $"a run of {count} positions takes {length} words", paramName: nameof(destination));
        }

        Shift(destination: destination, distance: -start, source: words);
        if ((count & 63) != 0) {
            destination[(length - 1)] &= (count & 63).LowMask<ulong>();
        }
        destination[length..].Clear();
    }
    /// <summary>Complements a set of <paramref name="members"/> positions in place: every position below
    /// <paramref name="members"/> flips, and every bit past the last position is cleared.</summary>
    /// <param name="words">The words, at least <see cref="WordsFor"/>(<paramref name="members"/>) long; only that many
    /// are written.</param>
    /// <param name="members">The positions the set addresses, zero or more.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="members"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="words"/> is shorter than the set's words.</exception>
    public static void Complement(Span<ulong> words, int members) {
        var length = WordsFor(members: members);

        if (words.Length < length) {
            throw new ArgumentException(message: $"a set of {members} positions takes {length} words", paramName: nameof(words));
        }
        for (var word = 0; (word < length); word++) {
            words[word] = ~words[word];
        }
        if ((members & 63) != 0) {
            words[(length - 1)] &= (members & 63).LowMask<ulong>();
        }
    }
    /// <summary>Returns the number of members a span of words sets.</summary>
    /// <param name="words">The words.</param>
    /// <returns>The member count.</returns>
    public static int Count(ReadOnlySpan<ulong> words) {
        var count = 0;

        foreach (var word in words) {
            count += BitOperations.PopCount(value: word);
        }
        return count;
    }

    /// <summary>The members of a <see cref="UInt128"/> mask, lowest first: each step clears the lowest set bit.</summary>
    public ref struct MaskMembers {
        private UInt128 m_remaining;

        internal MaskMembers(UInt128 mask) {
            m_remaining = mask;
            Current = -1;
        }

        /// <summary>Gets the member the enumerator stands on.</summary>
        public int Current { get; private set; }

        /// <summary>Returns this enumerator, so a mask's members read in a <see langword="foreach"/>.</summary>
        /// <returns>This enumerator.</returns>
        public readonly MaskMembers GetEnumerator() => this;
        /// <summary>Advances to the next member.</summary>
        /// <returns>Whether a member remained.</returns>
        public bool MoveNext() {
            if (m_remaining == UInt128.Zero) {
                return false;
            }
            Current = (int)UInt128.TrailingZeroCount(value: m_remaining);
            m_remaining &= (m_remaining - UInt128.One);

            return true;
        }
    }
    /// <summary>The members of a span of words, lowest first: each step clears the lowest set bit of the word it reads.</summary>
    public ref struct WordMembers {
        private readonly ReadOnlySpan<ulong> m_words;
        private int m_word;
        private ulong m_remaining;

        internal WordMembers(ReadOnlySpan<ulong> words) {
            m_words = words;
            m_word = 0;
            m_remaining = (words.IsEmpty ? 0UL : words[0]);
            Current = -1;
        }

        /// <summary>Gets the member the enumerator stands on.</summary>
        public int Current { get; private set; }

        /// <summary>Returns this enumerator, so a span's members read in a <see langword="foreach"/>.</summary>
        /// <returns>This enumerator.</returns>
        public readonly WordMembers GetEnumerator() => this;
        /// <summary>Advances to the next member.</summary>
        /// <returns>Whether a member remained.</returns>
        public bool MoveNext() {
            while (m_remaining == 0UL) {
                if (++m_word >= m_words.Length) {
                    return false;
                }
                m_remaining = m_words[m_word];
            }
            Current = ((m_word << 6) + BitOperations.TrailingZeroCount(value: m_remaining));
            m_remaining &= (m_remaining - 1UL);

            return true;
        }
    }
}
