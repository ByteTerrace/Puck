using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

public static partial class PrimeLeafKernels {
    // Reads prefix counts of one leaf-sieve bitmap for nondecreasing offsets below its width. The hard-leaf loops keep
    // one cursor per prime as a local; a batch copies it into registers and writes it back. The cursor holds the
    // bitmap count before its current word, and, separately, the count before its current counter block. It reaches
    // the counters and the word prefix masks at the sieve store's fixed offsets from the bitmap.
    /// <summary>Reads prefix counts of a <see cref="CombinatorialLeafSieve"/> bitmap for nondecreasing offsets.</summary>
    /// <remarks>Obtain one from <see cref="CombinatorialLeafSieve.StartPrefix"/>. A cursor goes stale at the sieve's next
    /// Reset, Initialize or crossing, which it cannot detect. Each query's offset must lie below the window's width
    /// and must not fall into an earlier bitmap word than the previous query's.</remarks>
    public ref struct CombinatorialPrefixCursor {
        // floor(n/240) is floor(n*559241/2^27) for every n below 2^27/112=1198372: with n=240w+r and
        // 559241*240=2^27+112, the product over 2^27 is w+(r+n*112/2^27)/240, and r<=239 keeps the fraction below one.
        // A cursor reads offsets below the leaf-sieve width, at most CombinatorialPrefixWidthLimit.
        private const nuint WordReciprocal = 559_241;
        private const int WordReciprocalShift = 27;

        private readonly ref ulong m_words;
        private readonly nuint m_wordLength;

        // Word and block indices are native-width so that address arithmetic needs no widening.
        private nuint m_word;
        private uint m_count;
        private nuint m_block;
        private uint m_blockCount;

        internal CombinatorialPrefixCursor(ref ulong words, int wordLength) {
            Debug.Assert((wordLength <= (CombinatorialPrefixWidthLimit / PrimeWheel30.WordIntegers)));
            m_words = ref words;
            m_wordLength = ((nuint)wordLength);
        }

        // The mask offset rides with the index so the read is one addressed load, with nothing to hoist.
        private readonly ulong Mask(nuint residue) =>
            Unsafe.AddByteOffset(byteOffset: (((nint)CombinatorialLeafSieve.MaskOffsetFromWords) + ((nint)(residue * sizeof(ulong)))), source: ref m_words);

        /// <summary>Counts the integers at or below an offset that are still coprime to every prime crossed off.</summary>
        /// <param name="offset">The offset from the window's first integer, below the window's width and not in an earlier
        /// bitmap word than the previous query's.</param>
        /// <returns>The count of surviving integers from the window's start through <paramref name="offset"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> lies past the last bitmap word of the
        /// window, or in an earlier word than the previous query's.</exception>
        public uint Count(nuint offset) {
            RequireOffset(offset: offset);
            return CountCore(offset: offset);
        }
        /// <summary>Counts the integers at or below an offset that are still coprime to every prime crossed off, stepping
        /// past the cursor's word through one window implementation.</summary>
        /// <param name="offset">The offset from the window's first integer, below the window's width and not in an earlier
        /// bitmap word than the previous query's.</param>
        /// <param name="window">The window implementation.</param>
        /// <returns>The count of surviving integers from the window's start through <paramref name="offset"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> lies past the last bitmap word of the
        /// window or in an earlier word than the previous query's, or <paramref name="window"/> is not a defined value.</exception>
        /// <exception cref="PlatformNotSupportedException">The host lacks the instruction set <paramref name="window"/>
        /// requires.</exception>
        /// <remarks>Every window returns the same counts, so the law <c>prime-selection.gourdon-leaf-sieve-rungs-vs-oracle</c>
        /// compares the windows the host supports with a direct sieve.</remarks>
        public uint Count(nuint offset, CombinatorialPrefixWindowKind window) {
            switch (window) {
                case CombinatorialPrefixWindowKind.Avx512BW:
                    PrimeKernelGuard.RequireInstructionSet(instructionSet: "AVX-512BW", supported: Avx512BW.IsSupported);
                    RequireOffset(offset: offset);
                    return CountCore<CombinatorialPrefixWindow512>(offset: offset);
                case CombinatorialPrefixWindowKind.Avx2:
                    PrimeKernelGuard.RequireInstructionSet(instructionSet: "AVX2", supported: Avx2.IsSupported);
                    RequireOffset(offset: offset);
                    return CountCore<CombinatorialPrefixWindow256>(offset: offset);
                case CombinatorialPrefixWindowKind.Scalar:
                    RequireOffset(offset: offset);
                    return CountCore<CombinatorialPrefixWindowScalar>(offset: offset);
                default:
                    throw new ArgumentOutOfRangeException(paramName: nameof(window), actualValue: window, message: "The window is not a defined value.");
            }
        }

        private readonly void RequireOffset(nuint offset) {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: offset, other: (m_wordLength * PrimeWheel30.WordIntegers), paramName: nameof(offset));
            ArgumentOutOfRangeException.ThrowIfLessThan(value: ((offset * WordReciprocal) >> WordReciprocalShift), other: m_word, paramName: nameof(offset));
        }

        // Instruction-set dispatch folds when the caller is compiled; every window returns the same counts.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint CountCore(nuint offset) {
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported) { return CountCore<CombinatorialPrefixWindow512>(offset: offset); }
            if (Vector256.IsHardwareAccelerated && Avx2.IsSupported) { return CountCore<CombinatorialPrefixWindow256>(offset: offset); }
            return CountCore<CombinatorialPrefixWindowScalar>(offset: offset);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint CountCore<TWindow>(nuint offset)
            where TWindow : struct, ICombinatorialPrefixWindow {
            var word = ((offset * WordReciprocal) >> WordReciprocalShift);
            var distance = (word - m_word);

            Debug.Assert(((word >= m_word) && (word < m_wordLength)));
            // Most queries stay on the cursor's word or move to the next one. Both add the cursor word's count masked
            // by the distance, without a branch on it, since that branch would follow the data.
            if (distance <= 1) {
                m_count += ((uint)BitOperations.PopCount(value: Unsafe.Add(elementOffset: m_word, source: ref m_words))) & ((uint)(0 - distance));
                m_word = word;
            } else {
                // A query two or more words ahead counts from the start of its block: the blocks the cursor passes,
                // then the words before it in its block, each through a fixed window rather than a loop whose trip
                // count follows the data.
                var block = m_block;

                // The query's block is the cursor's next, so the window's end lives in that field.
                m_block = (word >> CombinatorialCounterShift);

                var (blocks, words) = TWindow.Count(block: block, word: word, words: ref m_words);
                var blockCount = (m_blockCount + blocks);

                m_blockCount = blockCount;
                m_count = (blockCount + words);
                m_word = word;
            }
            return (m_count + ((uint)BitOperations.PopCount(value: Unsafe.Add(elementOffset: word, source: ref m_words)
                & Mask(residue: (offset - (word * PrimeWheel30.WordIntegers))))));
        }
    }
    /// <summary>Names the instruction set a <see cref="CombinatorialPrefixCursor"/> uses to step past its current word.</summary>
    public enum CombinatorialPrefixWindowKind {
        /// <summary>The AVX-512BW window; requires AVX-512BW.</summary>
        Avx512BW,
        /// <summary>The AVX2 window; requires AVX2.</summary>
        Avx2,
        /// <summary>The scalar window, for every host.</summary>
        Scalar,
    }

    // The fixed windows of a prefix cursor's step past its current word, one per instruction set; every one returns the
    // same counts. They read raw references into the sieve store, so only the cursor, which holds a reference to that
    // store's first word, reaches them.
    // Count returns the counts of the blocks from block up to the one holding word, each the sum of its slots modulo
    // 2^16, and the set bits of that block's words before word; words is the first bitmap word, from which the
    // counters and the window masks lie at the sieve store's fixed offsets. A window reads the
    // CombinatorialCounterWindow blocks ending at word's block and the eight words ending at word, so the counters
    // and the bitmap each extend one window back.
    internal interface ICombinatorialPrefixWindow {
        static abstract (uint Blocks, uint Words) Count(ref ulong words, nuint block, nuint word);
    }
    // The sieve store's tables and counters, each read at a constant offset from the bitmap plus an index.
    internal static class CombinatorialPrefixWindow {
        // The counter slots of the first of the CombinatorialCounterWindow blocks ending at block.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref ushort WindowCounters(ref ulong words, nuint block) =>
            ref Unsafe.As<byte, ushort>(source: ref Unsafe.AddByteOffset(source: ref Unsafe.As<ulong, byte>(source: ref words),
                byteOffset: (((nint)(block * sizeof(uint))) + (CombinatorialLeafSieve.CounterOffsetFromWords - (CombinatorialCounterWindow * sizeof(uint))))));
        // The counter slots of block.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref ushort Counters(ref ulong words, nuint block) =>
            ref Unsafe.As<byte, ushort>(source: ref Unsafe.AddByteOffset(source: ref Unsafe.As<ulong, byte>(source: ref words),
                byteOffset: (((nint)(block * sizeof(uint))) + CombinatorialLeafSieve.CounterOffsetFromWords)));
        // The window mask selecting the last bytes bytes of a 64-byte window.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref byte WindowMask(ref ulong words, nuint bytes) =>
            ref Unsafe.AddByteOffset(source: ref Unsafe.As<ulong, byte>(source: ref words), byteOffset: (((nint)bytes) + CombinatorialLeafSieve.WindowMaskOffsetFromWords));
        // The eight bitmap words ending at word.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ref ulong WindowWords(ref ulong words, nuint word) =>
            ref Unsafe.Add(elementOffset: (((nint)word) - (1 << CombinatorialCounterShift)), source: ref words);
    }
    // The AVX-512BW windows: one vector of the sixteen blocks' counter slots ending at the query's block,
    // multiply-added by the store's window-mask words at the window's length, so each 32-bit lane holds a selected
    // block's count negated, and one vector of the eight words ending at the query's word, masked by the same table's
    // bytes and counted through nibble lookups. One reduction serves both sums: the block counts, whole windows
    // included, sum to a prefix count below the width limit 2^20, and the word count, at most 512, rides from bit 20
    // of the even lanes, subtracting the negated block counts.
    internal readonly struct CombinatorialPrefixWindow512 : ICombinatorialPrefixWindow {
        private const int WordCountShift = 20;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (uint Blocks, uint Words) Count(ref ulong words, nuint block, nuint word) {
            // The masked window holds the last blocks modulo sixteen; a walk longer than one window, a prime's first
            // query or a rare long jump, adds the whole windows before it.
            var end = (word >> CombinatorialCounterShift);
            var remaining = (end - block) & (CombinatorialCounterWindow - 1);
            var stop = (end - remaining);
            var sum = Avx512BW.MultiplyAddAdjacent(left: Vector512.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowCounters(block: end, words: ref words)).AsInt16(),
                right: Vector512.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowMask(bytes: (remaining * (CombinatorialCounterSlots * sizeof(ushort))), words: ref words)).AsInt16());

            for (var at = block; (at < stop); at += CombinatorialCounterWindow) {
                sum += Avx512BW.MultiplyAddAdjacent(left: Vector512.LoadUnsafe(source: ref CombinatorialPrefixWindow.Counters(block: at, words: ref words)).AsInt16(),
                    right: Vector512<short>.AllBitsSet);
            }
            var value = Vector512.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowWords(word: word, words: ref words)).AsByte()
                & Vector512.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowMask(bytes: ((word & ((1 << CombinatorialCounterShift) - 1)) * sizeof(ulong)), words: ref words));
            var table = Vector512.Create(values: CombinatorialNibbleBitCounts);
            var nibble = Vector512.Create(value: ((byte)0x0F));
            // A byte lane gains at most four per lookup, eight in all, and a SAD lane at most 64.
            var counts = (Avx512BW.Shuffle(mask: value & nibble, value: table)
                + Avx512BW.Shuffle(mask: Avx512BW.ShiftRightLogical(count: 4, value: value.AsUInt16()).AsByte() & nibble, value: table));
            var total = Vector512.Sum(vector: ((Avx512BW.SumAbsoluteDifferences(left: counts, right: Vector512<byte>.Zero).AsUInt64() << WordCountShift).AsUInt32() - sum.AsUInt32()));

            return (total & ((1U << WordCountShift) - 1U), (total >> WordCountShift));
        }
    }
    // The AVX2 windows, same contract and reduction: two vectors of counter slots, eight blocks each, and two vectors
    // of words, masked the same way.
    internal readonly struct CombinatorialPrefixWindow256 : ICombinatorialPrefixWindow {
        private const int WordCountShift = 20;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (uint Blocks, uint Words) Count(ref ulong words, nuint block, nuint word) {
            // Each half addresses the store from the bitmap's base with its own displacement, so no address is held
            // across the half's loads.
            const nuint HalfBytes = 32;
            const nuint HalfBlocks = (HalfBytes / sizeof(uint));
            const nuint HalfWords = (HalfBytes / sizeof(ulong));
            var end = (word >> CombinatorialCounterShift);
            var remaining = (end - block) & (CombinatorialCounterWindow - 1);
            var stop = (end - remaining);
            var selected = (remaining * (CombinatorialCounterSlots * sizeof(ushort)));
            var sum = (Avx2.MultiplyAddAdjacent(left: Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowCounters(block: end, words: ref words)).AsInt16(),
                right: Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowMask(bytes: selected, words: ref words)).AsInt16())
                + Avx2.MultiplyAddAdjacent(left: Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowCounters(block: (end + HalfBlocks), words: ref words)).AsInt16(),
                right: Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowMask(bytes: (selected + HalfBytes), words: ref words)).AsInt16()));

            for (var at = block; (at < stop); at += CombinatorialCounterWindow) {
                sum += (Avx2.MultiplyAddAdjacent(left: Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.Counters(block: at, words: ref words)).AsInt16(), right: Vector256<short>.AllBitsSet)
                    + Avx2.MultiplyAddAdjacent(left: Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.Counters(block: (at + HalfBlocks), words: ref words)).AsInt16(), right: Vector256<short>.AllBitsSet));
            }
            var length = ((word & ((1 << CombinatorialCounterShift) - 1)) * sizeof(ulong));
            var low = Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowWords(word: word, words: ref words)).AsByte()
                & Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowMask(bytes: length, words: ref words));
            var high = Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowWords(word: (word + HalfWords), words: ref words)).AsByte()
                & Vector256.LoadUnsafe(source: ref CombinatorialPrefixWindow.WindowMask(bytes: (length + HalfBytes), words: ref words));
            var table = Vector256.Create(values: CombinatorialNibbleBitCounts[..Vector256<byte>.Count]);
            var nibble = Vector256.Create(value: ((byte)0x0F));
            // A byte lane gains at most sixteen, and a SAD lane at most 128.
            var counts = ((Avx2.Shuffle(mask: low & nibble, value: table)
                + Avx2.Shuffle(mask: Avx2.ShiftRightLogical(count: 4, value: low.AsUInt16()).AsByte() & nibble, value: table))
                + (Avx2.Shuffle(mask: high & nibble, value: table)
                + Avx2.Shuffle(mask: Avx2.ShiftRightLogical(count: 4, value: high.AsUInt16()).AsByte() & nibble, value: table)));
            var total = Vector256.Sum(vector: ((Avx2.SumAbsoluteDifferences(left: counts, right: Vector256<byte>.Zero).AsUInt64() << WordCountShift).AsUInt32() - sum.AsUInt32()));

            return (total & ((1U << WordCountShift) - 1U), (total >> WordCountShift));
        }
    }
    // The scalar windows, same contract, for hosts without either vector width: the words through eight population
    // counts under sign masks, the blocks one by one.
    internal readonly struct CombinatorialPrefixWindowScalar : ICombinatorialPrefixWindow {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (uint Blocks, uint Words) Count(ref ulong words, nuint block, nuint word) {
            const int BlockWords = (1 << CombinatorialCounterShift);
            var end = (word >> CombinatorialCounterShift);
            var length = word & (BlockWords - 1);
            var sum = 0U;
            var count = 0U;

            for (; (block < end); ++block) {
                var slots = Unsafe.As<ushort, uint>(source: ref CombinatorialPrefixWindow.Counters(block: block, words: ref words));

                sum += ((slots & ushort.MaxValue) + (slots >> 16)) & ushort.MaxValue;
            }
            // 7-length-index is negative exactly for the last length of the eight words ending at word, and its sign
            // keeps their counts.
            for (var index = ((nuint)0); (index < BlockWords); ++index) {
                count += ((uint)BitOperations.PopCount(value: Unsafe.Add(elementOffset: index, source: ref CombinatorialPrefixWindow.WindowWords(word: word, words: ref words))))
                    & ((uint)(((((nint)(BlockWords - 1)) - ((nint)length)) - ((nint)index)) >> 63));
            }
            return (sum, count);
        }
    }
}
