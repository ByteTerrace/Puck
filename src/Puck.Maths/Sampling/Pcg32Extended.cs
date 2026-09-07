using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>
/// O'Neill's extended PCG32 generator (pcg-cpp's <c>pcg_extended</c> in its <c>kdd</c> form — the shape
/// <c>pcg32_k1024</c> and its smaller siblings instantiate): a base <see cref="Pcg32XshRr"/> plus an extension
/// array of <c>k</c> 32-bit words. Each draw is the base draw XOR the word the base state's low bits select, so the
/// generator is k-dimensionally equidistributed rather than merely 1-dimensionally so, at the cost of one extra XOR
/// per draw. Pure integer arithmetic; identical construction arguments produce identical draws on every machine.
/// </summary>
/// <remarks>
/// The extension array advances on its own: whenever the base state's low sixteen bits are all zero (once every
/// 65536 draws, independent of <c>k</c>), every word in the table takes one invertible step of its own — a
/// pcg-cpp-derived permutation this type calls the RXS-M-XS step — with a carry rippling from each word into the
/// next exactly as a mixed-radix counter carries, and no allocation on any draw or advance.
/// <para>
/// The struct owns its extension array: <see langword="default"/> is degenerate (a null array; use
/// <see cref="Create(ulong, ulong, int)"/>), and a plain value copy — passing this type by value, or assigning it —
/// shares the same array with the original, so mutating the copy's extension through <see cref="SetExtension(int, uint)"/>
/// or <see cref="SetExtension(ReadOnlySpan{uint})"/> also mutates the original's. Call <see cref="Clone"/> for an
/// independent copy.
/// </para>
/// <para>
/// <see cref="Advance(ulong)"/> treats its argument as a literal forward draw count, exactly as
/// <see cref="Pcg32XshRr.Advance(ulong)"/> does, but the two do not share the same "go backward" shortcut: the base
/// generator alone has a clean 2⁶⁴ period, so <c>Advance(2⁶⁴ − n)</c> cheaply undoes <c>n</c> forward draws for it.
/// This type's combined period is <c>2⁶⁴ · (2³²)ᵏ</c> — the extension table does not return to its own starting
/// values after "only" a full lap of the base — so a huge count is still computed correctly and in the same
/// logarithmic time, but it is not a cheap inverse of a small forward advance the way it is for the bare LCG.
/// </para>
/// </remarks>
public struct Pcg32Extended : IDrawGenerator {
    private const string KError = "k must be a power of two in [2, 1024]";
    private const string ExtensionIndexError = "index must be within the extension array";

    // AdvancePow2 is fixed at 16 across every published k-variant (pcg32_k2 through pcg32_k16384 alike): the
    // extension table ticks once every 2^16 draws regardless of how many words it holds.
    private const int AdvancePow2 = 16;
    private const ulong TickMask = ((1UL << AdvancePow2) - 1UL);

    // The RXS-M-XS mixin's 32-bit constants (pcg-cpp's default_multiplier<uint32_t>, default_increment<uint32_t>,
    // mcg_multiplier<uint32_t> and mcg_unmultiplier<uint32_t>): the "inside out" step every extension word takes.
    private const uint ExtMultiplier = 747796405U;
    private const uint ExtIncrementBase = 2891336453U;
    private const uint McgMultiplier = 277803737U;
    private const uint McgUnmultiplier = 2897767785U;

    private Pcg32XshRr m_base;
    private readonly uint[] m_extension;
    // The table length less one: the length is a power of two, so this masks a state's low bits straight into a
    // valid index, and holding it here keeps the draw from re-deriving it from the array header.
    private readonly ulong m_indexMask;

    private Pcg32Extended(Pcg32XshRr baseGenerator, uint[] extension) {
        m_base = baseGenerator;
        m_extension = extension;
        m_indexMask = ((ulong)(extension.Length - 1));
    }

    // A nearly-divisionless bounded draw, built on this type's own NextUInt32 rather than the base generator's —
    // the identical shape Pcg32XshRr.Sample uses over its own raw draw.
    private uint Sample(uint exclusiveHigh) {
        var product = unchecked((((ulong)NextUInt32()) * exclusiveHigh));
        var lowBits = unchecked((uint)product);

        if (lowBits < exclusiveHigh) {
            var threshold = unchecked((((uint)(-((int)exclusiveHigh))) % exclusiveHigh));

            while (lowBits < threshold) {
                product = unchecked((((ulong)NextUInt32()) * exclusiveHigh));
                lowBits = unchecked((uint)product);
            }
        }

        return ((uint)(product >> 32));
    }
    // The RXS-M-XS output permutation over a 32-bit state (pcg-cpp's rxs_m_xs_mixin<uint32_t, uint32_t>::output).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RxsMxsOutput(uint internalValue) {
        var reshift = ((internalValue >> 28) & 0xFU);

        internalValue = unchecked(internalValue ^ (internalValue >> unchecked((int)(4U + reshift))));
        internalValue = unchecked(internalValue * McgMultiplier);

        var result = internalValue;

        result ^= (result >> 22);

        return result;
    }
    // The exact inverse of RxsMxsOutput (pcg-cpp's rxs_m_xs_mixin<uint32_t, uint32_t>::unoutput).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RxsMxsUnoutput(uint internalValue) {
        internalValue = Unxorshift32(
            shift: 22,
            value: internalValue
        );
        internalValue = unchecked(internalValue * McgUnmultiplier);

        var reshift = ((internalValue >> 28) & 0xFU);

        internalValue = Unxorshift32(
            shift: unchecked((int)(4U + reshift)),
            value: internalValue
        );

        return internalValue;
    }
    // Inverts x ^= x >> shift over a 32-bit word (pcg_extras::unxorshift). The forward map is I + S for the shift
    // matrix S, nilpotent over GF(2) with S^n = 0 once n·shift reaches the word, so its inverse is the finite series
    // I + S + S² + …, folded as (I + S)(I + S²)(I + S⁴)… — one xorshift per doubling of the shift.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Unxorshift32(uint value, int shift) {
        for (; (shift < 32); shift <<= 1) {
            value ^= (value >> shift);
        }

        return value;
    }
    // The per-word "inside out" single step (pcg-cpp's inside_out<>::external_step): unoutput, advance the hidden
    // 32-bit state by one word-specific LCG step, output again. Returns whether the output landed on zero, which —
    // because this permutation fixes zero (RxsMxsOutput(0) == 0) — signals a carry into the next word.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ExternalStep(ref uint word, int wordNumber) {
        var state = RxsMxsUnoutput(internalValue: word);

        state = unchecked((state * ExtMultiplier) + ExtIncrementBase + unchecked((uint)(wordNumber * 2)));

        var result = RxsMxsOutput(internalValue: state);

        word = result;

        return (result == 0U);
    }
    // The per-word bulk step (pcg-cpp's inside_out<>::external_advance): the same single-step formula generalized
    // to `delta` applications in one closed-form jump, via the 32-bit affine-skip and discrete-log helpers below.
    private static bool ExternalAdvance(ref uint word, int wordNumber, uint delta) {
        var state = RxsMxsUnoutput(internalValue: word);
        var increment = unchecked(ExtIncrementBase + unchecked((uint)(wordNumber * 2)));
        var distanceToZero = Distance32(
            currentState: state,
            increment: increment,
            multiplier: ExtMultiplier,
            newState: 0U
        );
        var crossesZero = (distanceToZero <= delta);

        state = Advance32(
            delta: delta,
            increment: increment,
            multiplier: ExtMultiplier,
            state: state
        );
        word = RxsMxsOutput(internalValue: state);

        return crossesZero;
    }
    // O'Neill's affine skip (Brown's algorithm) over a 32-bit LCG — the same binary-exponentiation shape
    // Pcg32XshRr.Advance uses over sixty-four bits, narrowed to the extension word's own carrier.
    private static uint Advance32(uint state, uint delta, uint multiplier, uint increment) {
        var accumulatedMultiplier = 1U;
        var accumulatedIncrement = 0U;
        var currentMultiplier = multiplier;
        var currentIncrement = increment;

        while (delta > 0U) {
            if ((delta & 1U) != 0U) {
                accumulatedMultiplier = unchecked(accumulatedMultiplier * currentMultiplier);
                accumulatedIncrement = unchecked((accumulatedIncrement * currentMultiplier) + currentIncrement);
            }

            currentIncrement = unchecked((currentMultiplier + 1U) * currentIncrement);
            currentMultiplier = unchecked(currentMultiplier * currentMultiplier);
            delta >>= 1;
        }

        return unchecked((accumulatedMultiplier * state) + accumulatedIncrement);
    }
    // O'Neill's logarithmic discrete-log over a 32-bit LCG — the exact-match (unmasked) narrowing of the same
    // algorithm Pcg32XshRr.Distance exposes over sixty-four bits.
    private static uint Distance32(uint currentState, uint newState, uint multiplier, uint increment) {
        var theBit = 1U;
        var distance = 0U;
        var currentMultiplier = multiplier;
        var currentIncrement = increment;

        while (currentState != newState) {
            if (((currentState ^ newState) & theBit) != 0U) {
                currentState = unchecked((currentState * currentMultiplier) + currentIncrement);
                distance |= theBit;
            }

            theBit <<= 1;
            currentIncrement = unchecked((currentMultiplier + 1U) * currentIncrement);
            currentMultiplier = unchecked(currentMultiplier * currentMultiplier);
        }

        return distance;
    }
    // The no-arg table tick (pcg-cpp's extended<>::advance_table()): every word takes one mandatory step, plus a
    // second when the previous word's step carried, so the whole table behaves as a mixed-radix counter incremented
    // by exactly one.
    private void AdvanceTableByOne() {
        ref var word = ref MemoryMarshal.GetArrayDataReference(array: m_extension);
        var carry = false;

        for (var index = 0; (index < m_extension.Length); ++index) {
            ref var current = ref Unsafe.Add(source: ref word, elementOffset: index);
            var wordNumber = (index + 1);

            if (carry) {
                carry = ExternalStep(word: ref current, wordNumber: wordNumber);
            }

            var secondCarry = ExternalStep(word: ref current, wordNumber: wordNumber);

            carry = (carry || secondCarry);
        }
    }
    // The bulk table advance (pcg-cpp's extended<>::advance_table(delta, true)): the same mixed-radix increment,
    // generalized to `delta` ticks at once, in O(k) time regardless of how large delta is.
    private void AdvanceTableBy(ulong delta) {
        var carry = 0UL;

        for (var index = 0; (index < m_extension.Length); ++index) {
            var totalDelta = unchecked(carry + delta);
            var truncatedDelta = unchecked((uint)totalDelta);

            carry = (totalDelta >> 32);

            var crossed = ExternalAdvance(
                delta: truncatedDelta,
                word: ref m_extension[index],
                wordNumber: (index + 1)
            );

            carry = unchecked(carry + (crossed ? 1UL : 0UL));
        }
    }

    /// <summary>Skips the generator forward by <paramref name="count"/> draws in logarithmic time: the base by the
    /// affine skip, the extension table by however many tick boundaries that skip crosses.</summary>
    /// <param name="count">The number of single-draw advances to apply.</param>
    /// <remarks>Never loops the individual draws. As with <see cref="Pcg32XshRr.Advance(ulong)"/>, only whole-draw
    /// advances are counted — a bounded draw that internally rejected a sample consumed more than one — but unlike
    /// the base type, a huge <paramref name="count"/> near <c>2⁶⁴</c> is not a cheap way to step this generator
    /// backward; see the type remarks.</remarks>
    public void Advance(ulong count) {
        var ticks = (count >> AdvancePow2);
        var maskedDistance = Pcg32XshRr.Distance(
            currentState: m_base.State,
            increment: m_base.Increment,
            mask: TickMask,
            multiplier: m_base.Multiplier,
            newState: 0UL
        );

        if (maskedDistance < (count & TickMask)) {
            ticks = unchecked(ticks + 1UL);
        }

        if (ticks != 0UL) {
            AdvanceTableBy(delta: ticks);
        }

        m_base.Advance(count: count);
    }
    /// <summary>Creates an independent copy whose extension array shares no storage with this instance.</summary>
    /// <returns>A copy that continues the identical draw sequence but never observes a mutation made through this
    /// instance's <see cref="SetExtension(int, uint)"/> or <see cref="SetExtension(ReadOnlySpan{uint})"/>, or vice
    /// versa.</returns>
    public readonly Pcg32Extended Clone() =>
        new(
            baseGenerator: m_base,
            extension: ((uint[])m_extension.Clone())
        );
    /// <summary>Creates a generator from a seed, a stream id, and an extension table size.</summary>
    /// <param name="state">The seed, forwarded to <see cref="Pcg32XshRr.Create(ulong, ulong)"/>.</param>
    /// <param name="stream">The stream id, forwarded to <see cref="Pcg32XshRr.Create(ulong, ulong)"/>.</param>
    /// <param name="k">The extension table size: a power of two in <c>[2, 1024]</c>, fixed for the life of the
    /// returned instance.</param>
    /// <returns>A ready-to-draw generator, its extension table filled from the base generator's own first <c>k + 2</c>
    /// draws (pcg-cpp's self-seeding recipe): an XOR difference of two opening draws masks every table entry, so a
    /// caller who never seeds the table explicitly still gets a well-mixed one.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stream"/> exceeds
    /// <see cref="Pcg32XshRr.MaxStream"/>, or <paramref name="k"/> is not a power of two in <c>[2, 1024]</c>.</exception>
    public static Pcg32Extended Create(ulong state, ulong stream, int k) {
        if ((k < 2) || (k > 1024) || ((k & (k - 1)) != 0)) {
            throw new ArgumentOutOfRangeException(
                actualValue: k,
                message: KError,
                paramName: nameof(k)
            );
        }

        var baseGenerator = Pcg32XshRr.Create(
            state: state,
            stream: stream
        );
        var extension = new uint[k];
        var lhs = baseGenerator.NextUInt32();
        var rhs = baseGenerator.NextUInt32();
        var xorDifference = unchecked(lhs - rhs);

        for (var index = 0; (index < k); ++index) {
            extension[index] = unchecked(baseGenerator.NextUInt32() ^ xorDifference);
        }

        return new(
            baseGenerator: baseGenerator,
            extension: extension
        );
    }
    /// <summary>Creates a generator from a seed, a stream id, and an AUTHORED extension table — the document-data
    /// counterpart of <see cref="Create(ulong, ulong, int)"/>'s self-seeding.</summary>
    /// <param name="state">The seed, forwarded to <see cref="Pcg32XshRr.Create(ulong, ulong)"/>.</param>
    /// <param name="stream">The stream id, forwarded to <see cref="Pcg32XshRr.Create(ulong, ulong)"/>.</param>
    /// <param name="table">The extension table's own words, copied into a private array; its length is <c>k</c>, a
    /// power of two in <c>[2, 1024]</c>.</param>
    /// <returns>A ready-to-draw generator whose base starts at the SAME state <see cref="Create(ulong, ulong, int)"/>'s
    /// base would (no draws are consumed forming the table), and whose extension table is <paramref name="table"/>
    /// verbatim rather than self-seeded from the base's own draws.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stream"/> exceeds
    /// <see cref="Pcg32XshRr.MaxStream"/>, or <paramref name="table"/>'s length is not a power of two in
    /// <c>[2, 1024]</c>.</exception>
    public static Pcg32Extended CreateWithTable(ulong state, ulong stream, ReadOnlySpan<uint> table) {
        var k = table.Length;

        if ((k < 2) || (k > 1024) || ((k & (k - 1)) != 0)) {
            throw new ArgumentOutOfRangeException(
                actualValue: k,
                message: KError,
                paramName: nameof(table)
            );
        }

        return new(
            baseGenerator: Pcg32XshRr.Create(
                state: state,
                stream: stream
            ),
            extension: table.ToArray()
        );
    }
    /// <summary>Reads one extension word directly.</summary>
    /// <param name="index">The word's index, in <c>[0, k)</c>.</param>
    /// <returns>The raw word currently stored at <paramref name="index"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, k)</c>.</exception>
    public readonly uint GetExtension(int index) {
        if ((index < 0) || (index >= m_extension.Length)) {
            throw new ArgumentOutOfRangeException(
                actualValue: index,
                message: ExtensionIndexError,
                paramName: nameof(index)
            );
        }

        return m_extension[index];
    }
    /// <summary>Draws the next 32 uniformly random bits.</summary>
    /// <returns>The base draw XOR'd with the extension word the base state's low bits select.</returns>
    /// <remarks>Reads (and, once every 65536 draws, advances) the extension table using the base state as it stood
    /// before this draw, then draws the base — the same order pcg-cpp's <c>extended&lt;&gt;::operator()</c> uses,
    /// though the two are independent: the base draw never depends on the table.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextUInt32() {
        var preDrawState = m_base.State;

        if ((preDrawState & TickMask) == 0UL) {
            AdvanceTableByOne();
        }

        // The table length is a power of two, so the masked index is always inside it: the bounds check is elided
        // by construction, not by the JIT's proof.
        var extensionWord = Unsafe.Add(
            elementOffset: ((nint)(preDrawState & m_indexMask)),
            source: ref MemoryMarshal.GetArrayDataReference(array: m_extension)
        );
        var baseDraw = m_base.NextUInt32();

        return unchecked(baseDraw ^ extensionWord);
    }
    public uint NextUInt32(uint minimum, uint maximum) {
        if (maximum < minimum) {
            (minimum, maximum) = (maximum, minimum);
        }

        var range = (maximum - minimum);

        return ((range != uint.MaxValue)
            ? unchecked((Sample(exclusiveHigh: (range + 1U)) + minimum))
            : NextUInt32()
        );
    }
    /// <summary>Draws a uniformly random fraction in <c>[0, 1)</c> at UQ0.16 resolution.</summary>
    /// <returns>A uniformly distributed <see cref="UnitFraction16"/> (the draw's top sixteen bits).</returns>
    public UnitFraction16 NextUnitFraction16() =>
        new(Value: ((ushort)(NextUInt32() >> 16)));
    /// <summary>Draws a uniformly random fraction in <c>[0, 1)</c> at UQ0.32 resolution.</summary>
    /// <returns>A uniformly distributed <see cref="UnitFraction32"/>.</returns>
    public UnitFraction32 NextUnitFraction32() =>
        new(Value: NextUInt32());
    /// <summary>Overwrites one extension word directly.</summary>
    /// <param name="index">The word's index, in <c>[0, k)</c>.</param>
    /// <param name="word">The raw word to store.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, k)</c>.</exception>
    /// <remarks>Mutates the same array a value copy of this instance shares; see the type remarks. To make a chosen
    /// upcoming draw come out to a wanted value, XOR the wanted value with the base draw that will occur when this
    /// word's index is next selected — the base draw depends only on <see cref="Pcg32XshRr"/>'s own state, so it can
    /// be computed ahead of time from a byte-for-bit copy of the base generator's raw bits.</remarks>
    public readonly void SetExtension(int index, uint word) {
        if ((index < 0) || (index >= m_extension.Length)) {
            throw new ArgumentOutOfRangeException(
                actualValue: index,
                message: ExtensionIndexError,
                paramName: nameof(index)
            );
        }

        m_extension[index] = word;
    }
    /// <summary>Overwrites the whole extension table.</summary>
    /// <param name="words">The replacement words; its length must equal the table size <c>k</c> this instance was
    /// created with.</param>
    /// <exception cref="ArgumentException"><paramref name="words"/>'s length does not equal <c>k</c>.</exception>
    /// <remarks>Mutates the same array a value copy of this instance shares; see the type remarks.</remarks>
    public readonly void SetExtension(ReadOnlySpan<uint> words) {
        if (words.Length != m_extension.Length) {
            throw new ArgumentException(
                message: $"expected {m_extension.Length} words, received {words.Length}",
                paramName: nameof(words)
            );
        }

        words.CopyTo(destination: m_extension);
    }

    /// <summary>Gets the extension table's current contents, most-recently-set values included.</summary>
    public readonly ReadOnlySpan<uint> Extension => m_extension;
    /// <summary>Gets the base generator's raw stream increment; see <see cref="Pcg32XshRr.Increment"/>.</summary>
    public readonly ulong Increment => m_base.Increment;
    /// <summary>Gets the base generator's raw state multiplier; see <see cref="Pcg32XshRr.Multiplier"/>.</summary>
    public readonly ulong Multiplier => m_base.Multiplier;
    /// <summary>Gets the base generator's raw state; see <see cref="Pcg32XshRr.State"/>.</summary>
    public readonly ulong State => m_base.State;
}
