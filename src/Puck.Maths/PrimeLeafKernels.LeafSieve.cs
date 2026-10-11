using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

public static partial class PrimeLeafKernels {
    // The sieve retains one bit for each U(30) candidate. Unlike a prime enumeration
    // sieve, crossing off p starts at p, not p*p: phi excludes the sieving primes too.
    // Block counters and the total hold exact bit counts between crossings: a small prime
    // masks the bitmap periodically and recounts each block, and a larger prime decrements a
    // counter only when a bit was set. Within each prime, leaf quotients are increasing, so a
    // prefix cursor reuses both block and word prefixes.
    /// <summary>Keeps the thirty-wheel bitmap of one window of integers coprime to the primes crossed off so far, with
    /// the prefix counts Gourdon's hard-leaf sum queries.</summary>
    /// <remarks>
    /// <para>An instance is not thread-safe. Call <see cref="Reset"/> once for a prime list, then
    /// <see cref="Initialize"/> for each window, then <see cref="CrossOff(uint, int)"/> for each prime from the ninth,
    /// 23, in ascending index order, once per window. The first window starts at zero; each later one starts just
    /// after the preceding window's last bitmap word, after every listed prime has been crossed. A word covers 240
    /// integers, so a partial final word leaves a gap before the next window. A window is at most 2^20 integers wide.
    /// The shells check that sequence and each crossing's divisor; the caller supplies an immutable ascending prime list.</para>
    /// <para>The laws <c>prime-selection.gourdon-leaf-sieve-vs-oracle</c> and
    /// <c>prime-selection.gourdon-leaf-sieve-rungs-vs-oracle</c> compare every prefix count with a direct sieve.</para>
    /// </remarks>
    public sealed class CombinatorialLeafSieve {
        // A counter block's bytes, 2^CounterByteShift: wheel byte b counts in block b>>CounterByteShift.
        private const int CounterByteShift = (CombinatorialCounterShift + 3);
        // A periodic stride's bytes, CombinatorialPeriodicStrideBlocks counter blocks.
        private const int PeriodicStrideBytes = (CombinatorialPeriodicStrideBlocks << CounterByteShift);
        // The sieve's store holds the word prefix masks, the window masks, the counters and the bitmap at fixed byte
        // offsets, sized for the width limit, so a prefix cursor reaches all four from the bitmap's address: one
        // register in the hard-leaf loops, and each table read one addressed load. Each region starts on a 64-byte
        // boundary of the cache-line aligned store, so periodic masks, recounts and prefix windows read and write
        // whole vectors. A prefix cursor's windows end at the query, the eight words ending at its word and the
        // sixteen blocks ending at its block, so the counters and the bitmap each follow one window of lead the sieve
        // never writes, read only under a zero mask. The store is pinned, so the crossing kernels take raw pointers
        // into it.
        private const int StoreMaskOffset = 0;
        // 64 bytes of zeros, then 64 of ones: the 64 bytes n in select the last n bytes of a window ending at a
        // query, for n up to 64: the CombinatorialCounterWindow blocks of counter slots before its block, or the words
        // of its block before its word. As 16-bit lanes the ones are -1, so a multiply-add of counter slots by their
        // mask sums the selected blocks' slots, negated.
        private const int StoreWindowMaskOffset = (StoreMaskOffset + (PrimeWheel30.WordIntegers * sizeof(ulong)));
        private const int StoreBitmapOffset = (((((StoreCounterOffset + (StoreBlockLimit * sizeof(uint))) + (1 << CounterByteShift)) + 63) / 64) * 64);
        private const int StoreBlockLimit = ((((int)((CombinatorialPrefixWidthLimit + (PrimeWheel30.WordIntegers - 1)) / PrimeWheel30.WordIntegers)) + ((1 << CombinatorialCounterShift) - 1)) >> CombinatorialCounterShift);
        private const int StoreCounterOffset = ((StoreWindowMaskOffset + (2 * 64)) + (CombinatorialCounterWindow * sizeof(uint)));
        private const int StoreLength = ((StoreBitmapOffset + (StoreBlockLimit << CounterByteShift)) / sizeof(ulong));

        private readonly byte[] m_pattern = new byte[(PeriodicSpanLimit(lanes: Vector512<byte>.Count) + CombinatorialPeriodicOverhang)];
        private readonly CacheAlignedArray<ulong> m_store = new(length: StoreLength);
        private uint[] m_next = [];
        private byte[] m_phase = [];
        private uint[] m_primes = [];

        private int m_primeCount;
        private int m_wordLength;
        private uint m_total;
        // Only the public shells track the checked traversal; production calls the cores directly.
        private ulong m_nextLow;
        private int m_nextCrossing;

        /// <summary>Allocates the sieve's store, sized for the widest window.</summary>
        public CombinatorialLeafSieve() {
            Debug.Assert(((((StoreWindowMaskOffset % 64) == 0) && ((StoreCounterOffset % 64) == 0)) && ((StoreBitmapOffset % 64) == 0)));

            var bytes = MemoryMarshal.AsBytes(span: m_store.Span);

            CombinatorialTables.PrefixMasks.CopyTo(destination: m_store.Span.Slice(length: PrimeWheel30.WordIntegers, start: (StoreMaskOffset / sizeof(ulong))));
            bytes.Slice(length: 64, start: (StoreWindowMaskOffset + 64)).Fill(value: byte.MaxValue);
        }

        /// <summary>Gets the number of integers in the current window still coprime to every prime crossed off.</summary>
        public uint TotalCount => m_total;

        // The first bitmap word and the first block's counters.
        private ref ulong Words => ref Unsafe.AddByteOffset(byteOffset: ((nuint)StoreBitmapOffset), source: ref m_store.Reference);
        private ref ushort Counters => ref Unsafe.As<ulong, ushort>(source: ref Unsafe.AddByteOffset(byteOffset: ((nuint)StoreCounterOffset), source: ref m_store.Reference));

        /// <summary>Starts every prime of a list at its first multiple, the prime itself.</summary>
        /// <param name="width">The largest window width, in integers; at most 2^20.</param>
        /// <param name="primes">The primes in ascending order, one-based: entry zero is unused and entries one through
        /// eight are 2 through 19. The sieve keeps the reference, so the list must not change while the sieve uses it.</param>
        /// <param name="primeCount">The number of primes in <paramref name="primes"/>, so that
        /// <c>primes[primeCount]</c> is the last.</param>
        /// <exception cref="ArgumentNullException"><paramref name="primes"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> exceeds 2^20, or
        /// <paramref name="primeCount"/> is negative or not below the length of <paramref name="primes"/>.</exception>
        public void Reset(uint width, uint[] primes, int primeCount) {
            ArgumentNullException.ThrowIfNull(argument: primes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: width, other: CombinatorialPrefixWidthLimit, paramName: nameof(width));
            ArgumentOutOfRangeException.ThrowIfNegative(value: primeCount, paramName: nameof(primeCount));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: primeCount, other: primes.Length, paramName: nameof(primeCount));
            ResetCore(primeCount: primeCount, primes: primes, width: width);
            m_nextLow = 0;
            m_nextCrossing = 0;
            m_wordLength = 0;
            m_total = 0;
        }

        internal void ResetCore(uint width, uint[] primes, int primeCount) {
            Debug.Assert((width <= CombinatorialPrefixWidthLimit));
            m_primes = primes;
            m_primeCount = primeCount;
            if (m_next.Length <= primeCount) {
                m_next = new uint[(primeCount + 1)];
                m_phase = new byte[(primeCount + 1)];
            }
            // A request can correct its estimate in either direction. Reset active states
            // to the first multiple p, even when retained arrays extend past this count.
            for (var index = (CombinatorialTinyPrimeCount + 1); (index <= primeCount); ++index) {
                m_next[index] = (primes[index] / PrimeWheel30.Modulus);
                _ = PrimeWheel30.TryChannel(residue: ((byte)(primes[index] % PrimeWheel30.Modulus)), channel: out var channel);
                m_phase[index] = ((byte)(channel * PrimeWheel30.ChannelCount));
            }
        }

        /// <summary>Starts a window with the multiples of 2 through 19 already removed.</summary>
        /// <param name="low">The first integer of the window: zero after Reset, otherwise just after the previous
        /// window's last 240-integer bitmap word.</param>
        /// <param name="width">The number of integers in the window; positive and at most 2^20.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is zero, exceeds 2^20, or makes the
        /// window extend beyond <see cref="ulong.MaxValue"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="low"/> is not the next window origin, Reset has not
        /// been called, or the preceding window still has primes to cross.</exception>
        public void Initialize(ulong low, uint width) {
            ArgumentOutOfRangeException.ThrowIfZero(value: width, paramName: nameof(width));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: width, other: CombinatorialPrefixWidthLimit, paramName: nameof(width));
            if ((m_primes.Length == 0) || (low != m_nextLow) || ((low % PrimeWheel30.Modulus) != 0)
                || ((m_nextCrossing != 0) && (m_nextCrossing <= m_primeCount))) {
                throw new ArgumentException(message: "Reset first, finish every crossing, and start at the next bitmap word.", paramName: nameof(low));
            }
            if (low > (ulong.MaxValue - (width - 1UL))) {
                throw new ArgumentOutOfRangeException(paramName: nameof(width), actualValue: width, message: "The window extends beyond ulong.MaxValue.");
            }
            InitializeCore(low: low, width: width);
            m_nextLow = (low + (((ulong)m_wordLength) * PrimeWheel30.WordIntegers));
            m_nextCrossing = (CombinatorialTinyPrimeCount + 1);
        }

        internal void InitializeCore(ulong low, uint width) {
            m_wordLength = ((int)(((width + PrimeWheel30.WordIntegers) - 1U) / PrimeWheel30.WordIntegers));

            var words = m_store.Span.Slice(length: m_wordLength, start: (StoreBitmapOffset / sizeof(ulong)));
            var bytes = MemoryMarshal.AsBytes(span: words);
            var pattern = MemoryMarshal.AsBytes(span: CombinatorialTables.TinyWords.AsSpan())[..((int)(CombinatorialTinyPeriod / PrimeWheel30.Modulus))];
            var source = ((int)((low / PrimeWheel30.Modulus) % ((uint)pattern.Length)));
            var copied = 0;

            while (copied < bytes.Length) {
                var length = Math.Min(val1: (pattern.Length - source), val2: (bytes.Length - copied));

                pattern.Slice(length: length, start: source).CopyTo(destination: bytes[copied..]);
                copied += length;
                source = 0;
            }
            words[(m_wordLength - 1)] &= CombinatorialTables.PrefixMasks[((int)((width - 1U) % PrimeWheel30.WordIntegers))];
            Recount();
        }

        // Recomputes every counter block that holds bitmap words, and the total, from the bitmap.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void Recount() {
            ref var words = ref Words;
            ref var counter = ref Counters;
            var blockWords = ((nuint)(1 << CombinatorialCounterShift));
            var wordLength = ((nuint)m_wordLength);
            var total = 0U;

            for (var start = ((nuint)0); (start < wordLength); start += blockWords) {
                var count = CountWords(length: nuint.Min(x: blockWords, y: (wordLength - start)), words: ref Unsafe.Add(elementOffset: start, source: ref words));

                StoreCountedSlots(count: count, counter: ref counter);
                total += count;
                counter = ref Unsafe.Add(elementOffset: CombinatorialCounterSlots, source: ref counter);
            }
            m_total = total;
        }
        // Writes a block's count, below 2^16, into its slots: the count in slot 0 and zero in slot 1, one 32-bit store.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StoreCountedSlots(ref ushort counter, uint count) {
            Unsafe.As<ushort, uint>(source: ref counter) = count;
        }

        // A periodic stride kernel masks one stride, two counter blocks of 64 bytes, with the masks from masks onward,
        // writes each block's count into its two slots with one 64-bit store and returns the four dwords it stored from:
        // the first block's two parts, then the second's, which sum to the stride's count. Each vector looks its byte
        // counts up while it is in a register, so a byte lane gains at most eight per vector and a block's 64 bytes'
        // worth in all, below 256 at every width; SAD then gathers the bytes into 64-bit sums. The slots may split a
        // count any way, since only their sum is the block's count; each part here is below 2^15. A kernel holds its
        // tables as fields, which reach the stride loop as arguments, so they stay in registers rather than being
        // rematerialized as constants every stride.
        private interface IPeriodicStrideKernel {
            Vector128<int> Apply(ref byte stride, ref byte masks, ref ushort counter);
        }
        // The 512-bit kernel on AVX-512 VBMI: one vector per block. A two-table byte permute counts each byte's low
        // seven bits and its sign bit adds one under a mask. Each block's eight SAD sums are at most 64, so one more
        // permute gathers their low bytes, the first block's then the second's, and one SAD adds each block's eight
        // into one part; the other part is zero.
        private readonly struct PeriodicStrideKernel512 : IPeriodicStrideKernel {
            private readonly Vector512<byte> m_lower;
            private readonly Vector512<byte> m_upper;
            private readonly Vector512<byte> m_gather;
            private readonly Vector512<byte> m_one;

            public PeriodicStrideKernel512() {
                m_lower = Vector512.Create(values: CombinatorialSevenBitCounts[..Vector512<byte>.Count]);
                m_upper = Vector512.Create(values: CombinatorialSevenBitCounts[Vector512<byte>.Count..]);
                m_gather = Vector512.Create(values: CombinatorialSadGather);
                m_one = Vector512<byte>.One;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<int> Apply(ref byte stride, ref byte masks, ref ushort counter) {
                var first = Avx512BW.SumAbsoluteDifferences(left: Count(at: 0, block: ref stride, masks: ref masks), right: Vector512<byte>.Zero);
                var second = Avx512BW.SumAbsoluteDifferences(left: Count(at: ((nuint)Vector512<byte>.Count), block: ref stride, masks: ref masks),
                    right: Vector512<byte>.Zero);

                return StorePeriodicSlots(counter: ref counter, parts: Sse2.SumAbsoluteDifferences(left: Avx512Vbmi.PermuteVar64x8x2(indices: m_gather,
                    lower: first.AsByte(), upper: second.AsByte()).GetLower().GetLower(), right: Vector128<byte>.Zero).AsInt32());
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private Vector512<byte> Count(ref byte block, ref byte masks, nuint at) {
                var value = Vector512.LoadUnsafe(elementOffset: at, source: ref block) & Vector512.LoadUnsafe(elementOffset: at, source: ref masks);
                var counts = Avx512Vbmi.PermuteVar64x8x2(indices: value, lower: m_lower, upper: m_upper);

                value.StoreUnsafe(destination: ref block, elementOffset: at);
                return Vector512.ConditionalSelect(condition: Vector512.LessThan(left: value.AsSByte(), right: Vector512<sbyte>.Zero).AsByte(),
                    left: (counts + m_one), right: counts);
            }
        }
        // The 256-bit kernel on AVX2: two vectors per block, whose nibble counts add, at most sixteen per lane, before
        // one SAD per block. One pack puts each 128-bit lane's two sums of both blocks side by side, and the lanes add.
        private readonly struct PeriodicStrideKernel256 : IPeriodicStrideKernel {
            private readonly Vector256<byte> m_table;
            private readonly Vector256<byte> m_nibble;

            public PeriodicStrideKernel256() {
                m_table = Vector256.Create(values: CombinatorialNibbleBitCounts[..Vector256<byte>.Count]);
                m_nibble = Vector256.Create(value: ((byte)0x0F));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<int> Apply(ref byte stride, ref byte masks, ref ushort counter) {
                const nuint Lanes = 32;
                var first = Avx2.SumAbsoluteDifferences(left: (Count(at: 0, block: ref stride, masks: ref masks) + Count(at: Lanes, block: ref stride, masks: ref masks)),
                    right: Vector256<byte>.Zero).AsInt32();
                var second = Avx2.SumAbsoluteDifferences(left: (Count(at: (2 * Lanes), block: ref stride, masks: ref masks)
                    + Count(at: (3 * Lanes), block: ref stride, masks: ref masks)), right: Vector256<byte>.Zero).AsInt32();
                var parts = Avx2.PackSignedSaturate(left: first, right: second).AsInt32();

                return StorePeriodicSlots(counter: ref counter, parts: (parts.GetLower() + parts.GetUpper()));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private Vector256<byte> Count(ref byte block, ref byte masks, nuint at) {
                var value = Vector256.LoadUnsafe(elementOffset: at, source: ref block) & Vector256.LoadUnsafe(elementOffset: at, source: ref masks);

                value.StoreUnsafe(destination: ref block, elementOffset: at);
                return (Avx2.Shuffle(mask: value & m_nibble, value: m_table)
                    + Avx2.Shuffle(mask: Vector256.ShiftRightLogical(vector: value.AsUInt16(), shiftCount: 4).AsByte() & m_nibble, value: m_table));
            }
        }
        // The 128-bit kernel on SSSE3: four vectors per block, whose byte counts add, at most 32 per lane, before one SAD
        // per block; one pack puts both blocks' two sums side by side.
        private readonly struct PeriodicStrideKernel128 : IPeriodicStrideKernel {
            private readonly Vector128<byte> m_table;
            private readonly Vector128<byte> m_nibble;

            public PeriodicStrideKernel128() {
                m_table = Vector128.Create(values: CombinatorialNibbleBitCounts[..Vector128<byte>.Count]);
                m_nibble = Vector128.Create(value: ((byte)0x0F));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<int> Apply(ref byte stride, ref byte masks, ref ushort counter) {
                const nuint Lanes = 16;
                var first = ((Count(at: 0, block: ref stride, masks: ref masks) + Count(at: Lanes, block: ref stride, masks: ref masks))
                    + (Count(at: (2 * Lanes), block: ref stride, masks: ref masks) + Count(at: (3 * Lanes), block: ref stride, masks: ref masks)));
                var second = ((Count(at: (4 * Lanes), block: ref stride, masks: ref masks) + Count(at: (5 * Lanes), block: ref stride, masks: ref masks))
                    + (Count(at: (6 * Lanes), block: ref stride, masks: ref masks) + Count(at: (7 * Lanes), block: ref stride, masks: ref masks)));

                return StorePeriodicSlots(counter: ref counter, parts: Sse2.PackSignedSaturate(
                    left: Sse2.SumAbsoluteDifferences(left: first, right: Vector128<byte>.Zero).AsInt32(),
                    right: Sse2.SumAbsoluteDifferences(left: second, right: Vector128<byte>.Zero).AsInt32()).AsInt32());
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private Vector128<byte> Count(ref byte block, ref byte masks, nuint at) {
                var value = Vector128.LoadUnsafe(elementOffset: at, source: ref block) & Vector128.LoadUnsafe(elementOffset: at, source: ref masks);

                value.StoreUnsafe(destination: ref block, elementOffset: at);
                return (Ssse3.Shuffle(mask: value & m_nibble, value: m_table)
                    + Ssse3.Shuffle(mask: Vector128.ShiftRightLogical(vector: value.AsUInt16(), shiftCount: 4).AsByte() & m_nibble, value: m_table));
            }
        }

        // Writes parts, the first block's two parts then the second's, each below 2^15, into the two blocks' slots with
        // one 64-bit store, and returns parts.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> StorePeriodicSlots(ref ushort counter, Vector128<int> parts) {
            Unsafe.WriteUnaligned(destination: ref Unsafe.As<ushort, byte>(source: ref counter),
                value: Sse2.PackSignedSaturate(left: parts, right: parts).AsUInt64().ToScalar());
            return parts;
        }
        // Returns the set bits of length words: four words per step into two chains, then one word at a time.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint CountWords(ref ulong words, nuint length) {
            ref var word = ref words;
            ref readonly var end = ref Unsafe.Add(elementOffset: length, source: ref words);
            ref readonly var quadEnd = ref Unsafe.Add(elementOffset: length & ~((nuint)3), source: ref words);
            var even = 0U;
            var odd = 0U;

            while (Unsafe.IsAddressLessThan(left: in word, right: in quadEnd)) {
                even += ((uint)(BitOperations.PopCount(value: word) + BitOperations.PopCount(value: Unsafe.Add(elementOffset: 2, source: ref word))));
                odd += ((uint)(BitOperations.PopCount(value: Unsafe.Add(elementOffset: 1, source: ref word))
                    + BitOperations.PopCount(value: Unsafe.Add(elementOffset: 3, source: ref word))));
                word = ref Unsafe.Add(elementOffset: 4, source: ref word);
            }
            while (Unsafe.IsAddressLessThan(left: in word, right: in end)) {
                even += ((uint)BitOperations.PopCount(value: word));
                word = ref Unsafe.Add(elementOffset: 1, source: ref word);
            }
            return (even + odd);
        }

        /// <summary>Starts nondecreasing prefix queries over the current bitmap.</summary>
        /// <returns>A cursor that is stale after the next Reset, Initialize or crossing.</returns>
        public CombinatorialPrefixCursor StartPrefix() =>
            new(wordLength: m_wordLength, words: ref Words);
        /// <summary>Removes the multiples of one prime, from the prime itself, through the end of the window, with the
        /// widest vectors the host accelerates.</summary>
        /// <param name="prime">The prime, equal to the entry at <paramref name="index"/> of the list given to <see cref="Reset"/>.</param>
        /// <param name="index">The prime's one-based position in the list given to <see cref="Reset"/>; at least nine and
        /// at most the list's prime count.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is below nine or above the prime count
        /// given to <see cref="Reset"/>, which has not been called when that count is zero.</exception>
        /// <exception cref="ArgumentException"><paramref name="prime"/> is not the list's entry, is not a unit modulo
        /// thirty greater than 19, or does not exceed the preceding entry; or <paramref name="index"/> is not the next
        /// crossing of an initialized window.</exception>
        public void CrossOff(uint prime, int index) {
            RequireCrossing(index: index, prime: prime);
            CrossOffCore(index: index, prime: prime);
            ++m_nextCrossing;
        }

        // Crosses off the prime at index with the widest vectors the host accelerates; x64 and Arm64 accelerate
        // Vector128 at least. Dispatch folds when the caller is compiled.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CrossOffCore(uint prime, int index) {
            if (Vector512.IsHardwareAccelerated) {
                CrossOffCore<VectorLanes512, Vector512<byte>>(index: index, prime: prime);
            } else if (Vector256.IsHardwareAccelerated) {
                CrossOffCore<VectorLanes256, Vector256<byte>>(index: index, prime: prime);
            } else {
                CrossOffCore<VectorLanes128, Vector128<byte>>(index: index, prime: prime);
            }
        }

        /// <summary>Removes the multiples of one prime through the end of the window, applying periodic masks through
        /// the widest stride kernel the host runs that is no wider than <typeparamref name="TLanes"/>.</summary>
        /// <typeparam name="TLanes">The byte-vector width.</typeparam>
        /// <typeparam name="TBytes">The vector type of <typeparamref name="TLanes"/>.</typeparam>
        /// <param name="prime">The prime, equal to the entry at <paramref name="index"/> of the list given to <see cref="Reset"/>.</param>
        /// <param name="index">The prime's one-based position in the list given to <see cref="Reset"/>; at least nine and
        /// at most the list's prime count.</param>
        /// <exception cref="ArgumentOutOfRangeException">The conditions of <see cref="CrossOff(uint, int)"/>.</exception>
        /// <exception cref="ArgumentException">The conditions of <see cref="CrossOff(uint, int)"/>.</exception>
        /// <remarks>Every width clears the same bits and leaves the same counts. A width the host cannot accelerate
        /// takes the narrower kernel the host supports, or masks and counts by words when it has none, so no width
        /// needs an instruction set.</remarks>
        public void CrossOff<TLanes, TBytes>(uint prime, int index)
            where TLanes : struct, IByteVectorLanes<TBytes>
            where TBytes : struct {
            RequireCrossing(index: index, prime: prime);
            CrossOffCore<TLanes, TBytes>(index: index, prime: prime);
            ++m_nextCrossing;
        }

        private void RequireCrossing(uint prime, int index) {
            ArgumentOutOfRangeException.ThrowIfLessThan(value: index, other: (CombinatorialTinyPrimeCount + 1), paramName: nameof(index));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value: index, other: m_primeCount, paramName: nameof(index));
            if ((prime != m_primes[index]) || (prime <= 19U) || (prime <= m_primes[(index - 1)])
                || !PrimeWheel30.TryChannel(channel: out _, residue: ((byte)(prime % PrimeWheel30.Modulus)))) {
                throw new ArgumentException(message: "The prime must match an ascending wheel divisor in the Reset list.", paramName: nameof(prime));
            }
            if (index != m_nextCrossing) {
                throw new ArgumentException(message: "Initialize a window and cross every listed prime once in index order.", paramName: nameof(index));
            }
        }

        // Crosses off the prime at index through the end of the bitmap and leaves exact counters and total. Periodic
        // masks apply through the widest stride kernel the host runs up to TLanes; every width clears the same bits and
        // leaves the same block counts, cursor and phase, and the laws run each width on identical inputs.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal unsafe void CrossOffCore<TLanes, TBytes>(uint prime, int index)
            where TLanes : struct, IByteVectorLanes<TBytes>
            where TBytes : struct {
            var state = m_phase[index];
            var cursor = ((nuint)m_next[index]);
            var phase = state & (PrimeWheel30.ChannelCount - 1);
            var byteLength = ((nuint)(m_wordLength * sizeof(ulong)));

            if (cursor < byteLength) {
                var quotient = ((nuint)(prime / PrimeWheel30.Modulus));

                if (prime <= PeriodicSpanLimit<TLanes, TBytes>()) {
                    m_total = CrossPeriodic<TLanes, TBytes>(byteLength: byteLength, channel: (state >> 3), cursor: ref cursor, phase: ref phase, prime: prime);
                } else {
                    // Little-endian words: wheel byte b holds bits 8*(b%8)..8*(b%8)+7 of word b/8. The store is
                    // pinned, so the pointers hold for the call.
                    var bytes = ((byte*)Unsafe.AsPointer(value: ref Words));
                    var counters = ((ushort*)Unsafe.AsPointer(value: ref Counters));

                    m_total -= (state >> 3) switch {
                        0 => CrossOffCounted<CombinatorialChannel0>(byteLength: byteLength, bytes: bytes, counters: counters, cursor: ref cursor, phase: ref phase, quotient: quotient),
                        1 => CrossOffCounted<CombinatorialChannel1>(byteLength: byteLength, bytes: bytes, counters: counters, cursor: ref cursor, phase: ref phase, quotient: quotient),
                        2 => CrossOffCounted<CombinatorialChannel2>(byteLength: byteLength, bytes: bytes, counters: counters, cursor: ref cursor, phase: ref phase, quotient: quotient),
                        3 => CrossOffCounted<CombinatorialChannel3>(byteLength: byteLength, bytes: bytes, counters: counters, cursor: ref cursor, phase: ref phase, quotient: quotient),
                        4 => CrossOffCounted<CombinatorialChannel4>(byteLength: byteLength, bytes: bytes, counters: counters, cursor: ref cursor, phase: ref phase, quotient: quotient),
                        5 => CrossOffCounted<CombinatorialChannel5>(byteLength: byteLength, bytes: bytes, counters: counters, cursor: ref cursor, phase: ref phase, quotient: quotient),
                        6 => CrossOffCounted<CombinatorialChannel6>(byteLength: byteLength, bytes: bytes, counters: counters, cursor: ref cursor, phase: ref phase, quotient: quotient),
                        _ => CrossOffCounted<CombinatorialChannel7>(byteLength: byteLength, bytes: bytes, counters: counters, cursor: ref cursor, phase: ref phase, quotient: quotient),
                    };
                }
            }
            m_next[index] = ((uint)(cursor - byteLength));
            m_phase[index] = ((byte)((state & ~(PrimeWheel30.ChannelCount - 1)) | phase));
        }

        // The largest prime, in bytes per wheel cycle, that a width of lanes bytes crosses off with periodic masks. A
        // periodic stride of S=128 bytes, two counter blocks, holds 8S/p crossings of p. The periodic pass spends
        // S/lanes vector steps on it plus about two more to fold both blocks' counts into their slots and wrap the
        // pattern offset: a step is eight instructions at the 512-bit width and nine at the 256-bit, the fold and wrap
        // about twenty. A counted crossing costs about two steps: about fifteen instructions around a counter
        // read-modify-write that the next crossing of its slot waits on. Periodic masks therefore win while
        // 16S/p>=S/lanes+2, so the limit is 16S*lanes/(S+2*lanes): 512 bytes for 512-bit vectors and 341 for 256-bit.
        // Charging a crossing three steps, 768 and 510, measured dearer than two on the 512-bit kernel. The 128-bit
        // width, 204 bytes, and a width that counts by words take the same formula as correct fallbacks, with no tuning
        // claim.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint PeriodicSpanLimit(int lanes) {
            return ((uint)(((16 * PeriodicStrideBytes) * lanes) / (PeriodicStrideBytes + (2 * lanes))));
        }
        // The limit at the width of the kernel the host runs for TLanes, or at TLanes's width when it counts by words.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint PeriodicSpanLimit<TLanes, TBytes>()
            where TLanes : struct, IByteVectorLanes<TBytes>
            where TBytes : struct {
            var lanes = PeriodicKernelLanes<TLanes, TBytes>();

            return PeriodicSpanLimit(lanes: ((lanes != 0) ? lanes : TLanes.ByteCount));
        }
        // Clears every crossing of a prime p within the periodic limit, of the given channel, from wheel byte
        // cursor<byteLength at the given phase through the end of the bitmap, recounts every counter block, returns
        // the bitmap's total, and moves cursor and phase to the first crossing at or after byteLength. Crossing k lies
        // offsets[k] bytes after the first byte of its cycle and cycles repeat every p bytes, so byte b takes the mask
        // of b modulo p. The pattern holds the masks of the bitmap's first p+128 bytes, from which the periodic stride
        // at byte b reads its 128 bytes at b modulo p. The cycle holding cursor starts at most p bytes before the
        // bitmap; its crossings before cursor precede the bitmap, so they fall outside it.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private uint CrossPeriodic<TLanes, TBytes>(uint prime, int channel, nuint byteLength, ref nuint cursor, ref int phase)
            where TLanes : struct, IByteVectorLanes<TBytes>
            where TBytes : struct {
            Span<uint> offsets = stackalloc uint[PrimeWheel30.ChannelCount];
            Span<byte> masks = stackalloc byte[PrimeWheel30.ChannelCount];
            var steps = CombinatorialTables.Steps.Slice(length: PrimeWheel30.ChannelCount, start: (channel * PrimeWheel30.ChannelCount));
            var quotient = (prime / PrimeWheel30.Modulus);
            var offset = 0U;

            for (var index = 0; (index < PrimeWheel30.ChannelCount); ++index) {
                offsets[index] = offset;
                masks[index] = ((byte)~(1 << steps[index].Bit));
                offset += ((quotient * steps[index].Gap) + steps[index].Carry);
            }
            // One cycle advances p bytes.
            Debug.Assert((offset == prime));

            var start = (((long)cursor) - offsets[phase]);
            var pattern = m_pattern.AsSpan(length: ((int)(prime + CombinatorialPeriodicOverhang)), start: 0);

            pattern.Fill(value: byte.MaxValue);
            for (var cycle = start; (cycle < pattern.Length); cycle += prime) {
                for (var index = 0; (index < PrimeWheel30.ChannelCount); ++index) {
                    var position = (cycle + offsets[index]);

                    if (((ulong)position) < ((ulong)pattern.Length)) { pattern[((int)position)] &= masks[index]; }
                }
            }
            ref var bytes = ref Unsafe.As<ulong, byte>(source: ref Words);
            ref var counter = ref Counters;
            ref var masked = ref MemoryMarshal.GetArrayDataReference(array: m_pattern);
            var total = ApplyPeriodicMasks<TLanes, TBytes>(byteLength: byteLength, bytes: ref bytes, counter: ref counter,
                pattern: ref masked, period: prime);
            // The last cycle start at or before byteLength holds the next crossing, or the cycle after it does.
            var last = (start + (((((long)byteLength) - start) / prime) * prime));

            phase = 0;
            cursor = ((nuint)(last + prime));
            for (var index = 0; (index < PrimeWheel30.ChannelCount); ++index) {
                if ((last + offsets[index]) >= ((long)byteLength)) {
                    phase = index;
                    cursor = ((nuint)(last + offsets[index]));
                    break;
                }
            }
            return total;
        }
        // ANDs bitmap byte b with pattern[b modulo period] for every b<byteLength and recounts each counter block once
        // its masks are applied, while the block is in cache, then returns the bitmap's total. The pattern holds
        // period+CombinatorialPeriodicOverhang bytes, one periodic stride of two counter blocks past the period, so
        // every byte of a stride reads its mask at the stride's offset plus its position and the offset wraps once a
        // stride. Every full stride takes the in-register kernel PeriodicKernelLanes chooses; the final partial stride,
        // or every stride on a host without a kernel, is masked and counted by words.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static uint ApplyPeriodicMasks<TLanes, TBytes>(ref byte bytes, ref ushort counter, ref byte pattern, nuint byteLength,
            nuint period)
            where TLanes : struct, IByteVectorLanes<TBytes>
            where TBytes : struct {
            var strideBytes = ((nuint)PeriodicStrideBytes);
            var kernelLanes = PeriodicKernelLanes<TLanes, TBytes>();
            var start = ((kernelLanes != 0) ? ((byteLength / strideBytes) * strideBytes) : 0);
            var offset = ((nuint)0);
            var total = ((kernelLanes == Vector512<byte>.Count)
                ? ApplyPeriodicStrides(bytes: ref bytes, counter: ref counter, kernel: new PeriodicStrideKernel512(), length: start, offset: ref offset,
                    pattern: ref pattern, period: period)
                : ((kernelLanes == Vector256<byte>.Count)
                ? ApplyPeriodicStrides(bytes: ref bytes, counter: ref counter, kernel: new PeriodicStrideKernel256(), length: start, offset: ref offset,
                    pattern: ref pattern, period: period)
                : ((kernelLanes == Vector128<byte>.Count)
                ? ApplyPeriodicStrides(bytes: ref bytes, counter: ref counter, kernel: new PeriodicStrideKernel128(), length: start, offset: ref offset,
                    pattern: ref pattern, period: period) : 0U)));

            if (start < byteLength) {
                total += ApplyPeriodicWords<TLanes, TBytes>(bytes: ref Unsafe.Add(elementOffset: start, source: ref bytes),
                    counter: ref Unsafe.Add(elementOffset: ((start >> CounterByteShift) * CombinatorialCounterSlots), source: ref counter), offset: offset,
                    pattern: ref pattern, period: period, byteLength: (byteLength - start));
            }
            return total;
        }
        // Applies the kernel to each stride of the bitmap's first length bytes, a multiple of the stride, from the
        // masks at offset, leaves offset at the next stride's masks and returns the strides' total. Out of line, so
        // the kernel's tables arrive as arguments and stay in registers.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static uint ApplyPeriodicStrides<TKernel>(TKernel kernel, ref byte bytes, ref ushort counter, ref byte pattern, nuint length, nuint period,
            ref nuint offset)
            where TKernel : struct, IPeriodicStrideKernel {
            var strideBytes = ((nuint)PeriodicStrideBytes);
            var strideAdvance = (strideBytes % period);
            var at = offset;
            // The stored parts, summed once at the end.
            var counts = Vector128<int>.Zero;
            ref var stride = ref bytes;
            ref readonly var end = ref Unsafe.Add(elementOffset: length, source: ref bytes);

            while (Unsafe.IsAddressLessThan(left: in stride, right: in end)) {
                counts += kernel.Apply(counter: ref counter, masks: ref Unsafe.Add(elementOffset: at, source: ref pattern), stride: ref stride);
                at = WrapPeriodicOffset(offset: (at + strideAdvance), period: period);
                stride = ref Unsafe.Add(elementOffset: strideBytes, source: ref stride);
                counter = ref Unsafe.Add(elementOffset: (CombinatorialPeriodicStrideBlocks * CombinatorialCounterSlots), source: ref counter);
            }
            offset = at;
            return ((uint)Vector128.Sum(vector: counts));
        }
        // The word form of ApplyPeriodicMasks from the stride whose masks start at offset: whole vectors, then single
        // words, masked, and each block counted by words. Out of line, so the in-register stride loop keeps its
        // registers.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static uint ApplyPeriodicWords<TLanes, TBytes>(ref byte bytes, ref ushort counter, ref byte pattern, nuint offset, nuint byteLength,
            nuint period)
            where TLanes : struct, IByteVectorLanes<TBytes>
            where TBytes : struct {
            var lanes = ((nuint)TLanes.ByteCount);
            var strideBytes = ((nuint)PeriodicStrideBytes);
            var blockBytes = (((nuint)1) << CounterByteShift);
            var strideAdvance = (strideBytes % period);
            var total = 0U;

            for (var start = ((nuint)0); (start < byteLength); start += strideBytes) {
                ref var stride = ref Unsafe.Add(elementOffset: start, source: ref bytes);
                ref var masks = ref Unsafe.Add(elementOffset: offset, source: ref pattern);
                var length = nuint.Min(x: strideBytes, y: (byteLength - start));
                var at = ((nuint)0);

                for (; ((at + lanes) <= length); at += lanes) {
                    TLanes.Store(destination: ref stride, elementOffset: at, value: TLanes.And(left: TLanes.Load(elementOffset: at, source: in stride),
                        right: TLanes.Load(elementOffset: at, source: in masks)));
                }
                for (; (at < length); at += sizeof(ulong)) {
                    Unsafe.As<byte, ulong>(source: ref Unsafe.Add(elementOffset: at, source: ref stride))
                        &= Unsafe.ReadUnaligned<ulong>(source: in Unsafe.Add(elementOffset: at, source: ref masks));
                }
                for (var block = ((nuint)0); (block < length); block += blockBytes) {
                    var count = CountWords(length: (nuint.Min(x: blockBytes, y: (length - block)) / sizeof(ulong)),
                        words: ref Unsafe.As<byte, ulong>(source: ref Unsafe.Add(elementOffset: block, source: ref stride)));

                    StoreCountedSlots(count: count, counter: ref Unsafe.Add(elementOffset: ((block >> CounterByteShift) * CombinatorialCounterSlots), source: ref counter));
                    total += count;
                }
                offset = WrapPeriodicOffset(offset: (offset + strideAdvance), period: period);
                counter = ref Unsafe.Add(elementOffset: (CombinatorialPeriodicStrideBlocks * CombinatorialCounterSlots), source: ref counter);
            }
            return total;
        }
        // The vector bytes of the in-register periodic kernel the host runs for TLanes, or zero when the pass masks and
        // counts by words: the widest kernel no wider than TLanes whose instructions the host has. The 512-bit kernel
        // needs AVX-512 VBMI's byte permutes, so a 512-bit host without them takes the 256-bit kernel.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PeriodicKernelLanes<TLanes, TBytes>()
            where TLanes : struct, IByteVectorLanes<TBytes>
            where TBytes : struct =>
            (((TLanes.ByteCount >= Vector512<byte>.Count) && Avx512Vbmi.IsSupported) ? Vector512<byte>.Count
                : (((TLanes.ByteCount >= Vector256<byte>.Count) && Avx2.IsSupported) ? Vector256<byte>.Count
                : (Ssse3.IsSupported ? Vector128<byte>.Count : 0)));
        // Reduces offset<2*period below period without a branch: the wrap follows the period, not a predictable
        // trend. The excess offset-period is negative exactly when no wrap is due, and its sign restores period.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint WrapPeriodicOffset(nuint offset, nuint period) {
            var excess = (offset - period);

            return (excess + (period & ((nuint)(((nint)excess) >> 63))));
        }
        // Crosses off one prime of a fixed channel from wheel byte cursor<byteLength at the given phase through the end
        // of the bitmap, and returns the number of bits it cleared. Each crossing decrements one slot of its block's
        // counter, crossing k of a wheel cycle slot k%2, so crossings that share a slot are two apart and a slot's
        // read-modify-writes chain at most once every two crossings, whatever the prime. Full cycles use folded
        // per-channel bits and carries. Each crossing also prefetches its own byte one cycle on, span bytes further: a
        // cycle's crossings touch as many cache lines, which the rest of the sieve and the counters evict between a
        // prime's cycles, and a cycle outlasts a second-level cache hit.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe uint CrossOffCounted<TChannel>(byte* bytes, ushort* counters, nuint byteLength,
            nuint quotient, ref nuint cursor, ref int phase)
            where TChannel : struct, ICombinatorialChannel {
            var residue = PrimeWheel30.Residues[TChannel.Channel];
            var span = ((quotient * PrimeWheel30.Modulus) + residue);
            // The byte advances q*gap+carry per wheel step. A gap is two, four or six, so three multiples of q cover
            // the cycle, and a step with a constant phase folds its carry into one address computation. The target
            // bits and carries of the channel's eight phases fold to constants.
            var two = (quotient * 2);
            var four = (quotient * 4);
            var six = (quotient * 6);
            var bit0 = PrimeWheel30.TargetBit(phase: 0, residue: residue);
            var bit1 = PrimeWheel30.TargetBit(phase: 1, residue: residue);
            var bit2 = PrimeWheel30.TargetBit(phase: 2, residue: residue);
            var bit3 = PrimeWheel30.TargetBit(phase: 3, residue: residue);
            var bit4 = PrimeWheel30.TargetBit(phase: 4, residue: residue);
            var bit5 = PrimeWheel30.TargetBit(phase: 5, residue: residue);
            var bit6 = PrimeWheel30.TargetBit(phase: 6, residue: residue);
            var bit7 = PrimeWheel30.TargetBit(phase: 7, residue: residue);
            var at = cursor;
            var current = phase;
            var removed = 0U;

            for (; ((current != 0) && (at < byteLength)); current = (current + 1) & (PrimeWheel30.ChannelCount - 1)) {
                var step = CombinatorialTables.Steps[((TChannel.Channel * PrimeWheel30.ChannelCount) + current)];

                removed += CrossCounted(at: at, bit: step.Bit, bytes: bytes, counters: counters, slot: current & (CombinatorialCounterSlots - 1), span: span);
                at += ((quotient * step.Gap) + step.Carry);
            }
            while ((at + span) <= byteLength) {
                removed += CrossCounted(at: at, bit: bit0, bytes: bytes, counters: counters, slot: 0, span: span);
                at += StepLength(four: four, phase: 0, residue: residue, six: six, two: two);
                removed += CrossCounted(at: at, bit: bit1, bytes: bytes, counters: counters, slot: 1, span: span);
                at += StepLength(four: four, phase: 1, residue: residue, six: six, two: two);
                removed += CrossCounted(at: at, bit: bit2, bytes: bytes, counters: counters, slot: 0, span: span);
                at += StepLength(four: four, phase: 2, residue: residue, six: six, two: two);
                removed += CrossCounted(at: at, bit: bit3, bytes: bytes, counters: counters, slot: 1, span: span);
                at += StepLength(four: four, phase: 3, residue: residue, six: six, two: two);
                removed += CrossCounted(at: at, bit: bit4, bytes: bytes, counters: counters, slot: 0, span: span);
                at += StepLength(four: four, phase: 4, residue: residue, six: six, two: two);
                removed += CrossCounted(at: at, bit: bit5, bytes: bytes, counters: counters, slot: 1, span: span);
                at += StepLength(four: four, phase: 5, residue: residue, six: six, two: two);
                removed += CrossCounted(at: at, bit: bit6, bytes: bytes, counters: counters, slot: 0, span: span);
                at += StepLength(four: four, phase: 6, residue: residue, six: six, two: two);
                removed += CrossCounted(at: at, bit: bit7, bytes: bytes, counters: counters, slot: 1, span: span);
                at += StepLength(four: four, phase: 7, residue: residue, six: six, two: two);
            }
            for (; (at < byteLength); current = (current + 1) & (PrimeWheel30.ChannelCount - 1)) {
                var step = CombinatorialTables.Steps[((TChannel.Channel * PrimeWheel30.ChannelCount) + current)];

                removed += CrossCounted(at: at, bit: step.Bit, bytes: bytes, counters: counters, slot: current & (CombinatorialCounterSlots - 1), span: span);
                at += ((quotient * step.Gap) + step.Carry);
            }
            cursor = at;
            phase = current;
            return removed;
        }
        // The byte advance of one wheel step from phase, q*gap+carry, with the gap's multiple of q chosen as the phase
        // folds.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint StepLength(nuint two, nuint four, nuint six, int residue, int phase) {
            var gap = PrimeWheel30.Gap(phase: phase);

            return (((gap == 6) ? six : ((gap == 4) ? four : two)) + ((nuint)PrimeWheel30.Carry(phase: phase, residue: residue)));
        }
        // Clears bit bit of wheel byte at, decrements the given slot of its block's counter by the bit's old value, which
        // it returns, and prefetches the byte span bytes on; a prefetch past the bitmap has no effect.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe uint CrossCounted(byte* bytes, ushort* counters, nuint at, int bit, int slot, nuint span) {
            var target = (bytes + at);
            uint value = *target;
            var hit = ((ushort)((value >> bit) & 1U));

            *target = ((byte)(value & ~(1U << bit)));
            // The block's slots start sizeof(uint) bytes per block into the counters.
            *((ushort*)((((byte*)counters) + ((at >> CounterByteShift) * sizeof(uint))) + (slot * sizeof(ushort)))) -= hit;
            if (Sse.IsSupported) { Sse.Prefetch0(address: (target + span)); }
            return hit;
        }

        // A prefix cursor's counters, window masks and word prefix masks lie at these byte offsets from the bitmap's
        // first word.
        internal const int CounterOffsetFromWords = (StoreCounterOffset - StoreBitmapOffset);
        internal const int MaskOffsetFromWords = (StoreMaskOffset - StoreBitmapOffset);
        internal const int WindowMaskOffsetFromWords = (StoreWindowMaskOffset - StoreBitmapOffset);
    }

    // One wheel channel per type, so each crossing loop folds its channel's bits and carries.
    private interface ICombinatorialChannel {
        static abstract int Channel { get; }
    }
    private readonly struct CombinatorialChannel0 : ICombinatorialChannel { public static int Channel => 0; }
    private readonly struct CombinatorialChannel1 : ICombinatorialChannel { public static int Channel => 1; }
    private readonly struct CombinatorialChannel2 : ICombinatorialChannel { public static int Channel => 2; }
    private readonly struct CombinatorialChannel3 : ICombinatorialChannel { public static int Channel => 3; }
    private readonly struct CombinatorialChannel4 : ICombinatorialChannel { public static int Channel => 4; }
    private readonly struct CombinatorialChannel5 : ICombinatorialChannel { public static int Channel => 5; }
    private readonly struct CombinatorialChannel6 : ICombinatorialChannel { public static int Channel => 6; }
    private readonly struct CombinatorialChannel7 : ICombinatorialChannel { public static int Channel => 7; }
}
