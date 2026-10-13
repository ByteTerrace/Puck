using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

public static partial class PrimeKernels {
    /// <summary>The number of odd primes trial division tests ahead of Baillie–PSW: every odd prime from three through <see cref="TrialDivisionCeiling"/>.</summary>
    /// <remarks>
    /// <para>
    /// A prime <c>p</c> is worth testing while the round it spares, taken with probability about <c>1 / p</c>, costs
    /// more than the test: <c>p &lt; R / c</c> for a round of <c>R</c> cycles and a test of <c>c</c>. A vector of
    /// <c>k</c> primes ending near <c>p</c> catches about <c>k / p</c> of the values that reach it, so the vector rungs
    /// keep testing while <c>p &lt; k R / c_k</c>. The base-two round of a word above <c>2^32</c> costs from about
    /// 250 cycles near <c>2^33</c> to about 750 at the top of the carrier, and a 256-bit vector of eight folded tests
    /// costs three to four cycles; that puts the break-even prime between about 500 and 1,500. One hundred and
    /// twenty-eight primes, through 727, is the vector-friendly count inside that band: eight 512-bit vectors or
    /// sixteen 256-bit ones, the narrow rungs' shape too, whose 32-bit rounds cost about 250 cycles.
    /// </para>
    /// <para>
    /// The depth never changes an answer: a divisor below a word above <c>2^32</c> makes it composite, and every
    /// survivor is decided exactly.
    /// </para>
    /// </remarks>
    public const int TrialDivisionPrimeCount = 128;
    /// <summary>The largest prime trial division tests, 727, the <see cref="TrialDivisionPrimeCount"/>-th odd prime.</summary>
    public const uint TrialDivisionCeiling = 727U;

    /// <summary>The bias that balances each 16-bit digit of a word into a signed one, <c>d - 32768</c>.</summary>
    private const ulong TrialDivisionDigitBias = 0x8000_8000_8000_8000UL;
    /// <summary>The bound below which every balanced digit sum <c>Σ a_i w_i</c> lies in magnitude: four digits of at most <c>2^15</c> times weights of at most <c>363</c>.</summary>
    private const long TrialDivisionFoldLift = (1L << 26);

    /// <summary>Holds the divisibility tests of the odd primes from three through <see cref="TrialDivisionCeiling"/>, ascending, cache aligned for whole-vector loads.</summary>
    /// <remarks>
    /// <para>
    /// A value is divisible by the prime exactly when its product with the prime's inverse modulo <c>2^64</c> does not
    /// exceed the ceiling <c>floor((2^64 - 1) / p)</c>: the multiples of <c>p</c> map onto <c>[0, ceiling]</c> one to one
    /// and every other value lands above it. The wide table holds those inverses and ceilings for the scalar tests.
    /// The narrow table holds the same test modulo <c>2^32</c>, the inverses and ceilings <c>floor((2^32 - 1) / p)</c>,
    /// which decide a 32-bit value with one native multiply per lane on every rung, and three more runs that fold a
    /// 64-bit value onto a 32-bit one first.
    /// </para>
    /// <para>
    /// The fold writes the word as four 16-bit digits <c>d_i</c> and balances each to <c>a_i = d_i - 32768</c>, a signed
    /// 16-bit lane, by one exclusive-or. With <c>w_i</c> the residue of <c>2^(16 i)</c> modulo <c>p</c> of least
    /// magnitude, <c>|w_i| &lt;= 363</c> through 727, the word is congruent to <c>Σ a_i w_i + 32768 Σ w_i</c>; the sum lies
    /// within <c>4 2^15 363</c> of zero, below <c>2^26</c>, so the offset <c>c = (32768 Σ w_i mod p) + p ceil(2^26 / p)</c>
    /// makes <c>S = Σ a_i w_i + c</c> a nonnegative 32-bit value congruent to the word, and <c>p</c> divides the word
    /// exactly when it divides <c>S</c>, which the narrow test decides. Two adjacent weights share a 32-bit lane, so a
    /// vector of 32-bit lanes forms <c>S</c> for one prime per lane with two multiply-add-adjacent instructions, two
    /// additions and the narrow test's one multiply: twice the lanes of a 64-bit product, at a third of its multiplies
    /// on AVX2.
    /// </para>
    /// <para>
    /// Every entry is derived from <see cref="BasePrimes"/> and <see cref="UnsignedNumberFunctions.ModularInverse{T}(T)"/>,
    /// so no run can fall out of step with the primes. The runs live on the pinned heap, whose arrays never move, and
    /// the class has no static constructor, so the runtime may initialize it while it compiles a caller and the caller
    /// then reads the start as a constant.
    /// </para>
    /// </remarks>
    private static unsafe class TrialDivision {
        internal const int Ceilings = TrialDivisionPrimeCount;
        internal const int FoldHighWeights = (3 * TrialDivisionPrimeCount);
        internal const int FoldLowWeights = (2 * TrialDivisionPrimeCount);
        internal const int FoldOffsets = (4 * TrialDivisionPrimeCount);
        internal const int NarrowCeilings = TrialDivisionPrimeCount;

        private static readonly CacheAlignedArray<ulong> Storage = CreateStorage();
        private static readonly CacheAlignedArray<uint> NarrowStorage = CreateNarrowStorage();

        internal static readonly ulong* Start = ((ulong*)Unsafe.AsPointer(value: ref Storage.Reference));
        internal static readonly uint* NarrowStart = ((uint*)Unsafe.AsPointer(value: ref NarrowStorage.Reference));

        private static CacheAlignedArray<ulong> CreateStorage() {
            var storage = new CacheAlignedArray<ulong>(length: (2 * TrialDivisionPrimeCount));
            var primes = WindowSieve.BasePrimes;
            var words = storage.Span;

            for (var i = 0; (i < TrialDivisionPrimeCount); ++i) {
                var prime = ((ulong)primes[i]);

                words[i] = prime.ModularInverse();
                words[(Ceilings + i)] = (ulong.MaxValue / prime);
            }

            return storage;
        }
        private static CacheAlignedArray<uint> CreateNarrowStorage() {
            var storage = new CacheAlignedArray<uint>(length: (5 * TrialDivisionPrimeCount));
            var primes = WindowSieve.BasePrimes;
            var words = storage.Span;
            Span<int> weights = stackalloc int[4];

            for (var i = 0; (i < TrialDivisionPrimeCount); ++i) {
                var prime = primes[i];
                var residue = 1L;

                words[i] = prime.ModularInverse();
                words[(NarrowCeilings + i)] = (uint.MaxValue / prime);

                for (var digit = 0; (digit < weights.Length); ++digit) {
                    weights[digit] = ((int)((residue > (prime >>> 1)) ? (residue - prime) : residue));
                    residue = ((residue << 16) % prime);
                }

                var offset = ((((32_768L * ((weights[0] + weights[1]) + (weights[2] + weights[3]))) % prime) + prime) % prime);

                words[(FoldLowWeights + i)] = ((uint)((ushort)weights[0])) | (((uint)((ushort)weights[1])) << 16);
                words[(FoldHighWeights + i)] = ((uint)((ushort)weights[2])) | (((uint)((ushort)weights[3])) << 16);
                words[(FoldOffsets + i)] = ((uint)(offset + (prime * (((TrialDivisionFoldLift + prime) - 1L) / prime))));
            }

            return storage;
        }
    }

    /// <summary>Returns the inverse modulo <c>2^64</c> and the divisibility ceiling of the odd prime at an index of the trial-division table.</summary>
    /// <param name="index">The index, from zero for three up to <see cref="TrialDivisionPrimeCount"/>, exclusive.</param>
    /// <returns>The prime's inverse and its ceiling.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe (ulong Inverse, ulong Ceiling) TrialDivisor(int index) =>
        (TrialDivision.Start[index], TrialDivision.Start[(TrialDivision.Ceilings + index)]);

    /// <summary>Returns a reference to the first word of a run of the trial-division table.</summary>
    /// <param name="offset">The run's offset, in words.</param>
    /// <returns>The reference.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ref ulong TrialDivisionRun(int offset) => ref TrialDivision.Start[offset];

    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a value, testing eight primes per 512-bit vector.</summary>
    /// <param name="value">The value; every prime divides zero, and a prime divides itself.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>
    /// Each vector folds the value onto one 32-bit value per prime and tests sixteen of them, stopping at the first
    /// vector holding a divisor; the smallest primes come first, where most composites stop. Without AVX-512BW's
    /// multiply-add-adjacent instruction the rung multiplies the value by eight 64-bit inverses per vector instead,
    /// in software on a host without 512-bit vectors. The law <c>prime-field.trial-division-rungs-vs-remainder</c>
    /// compares every rung with remainders.
    /// </remarks>
    public static bool HasTrialFactor512(ulong value) => HasTrialFactor512Core(value: value);
    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a value, testing four primes per 256-bit vector.</summary>
    /// <param name="value">The value; every prime divides zero, and a prime divides itself.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>The 256-bit rung of <see cref="HasTrialFactor512(ulong)"/>, with the same contract: eight folded tests per vector with AVX2, four 64-bit products per vector in software without it.</remarks>
    public static bool HasTrialFactor256(ulong value) => HasTrialFactor256Core(value: value);
    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a value, testing two primes per 128-bit vector.</summary>
    /// <param name="value">The value; every prime divides zero, and a prime divides itself.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>The 128-bit rung of <see cref="HasTrialFactor512(ulong)"/>, with the same contract: four folded tests per vector with SSE4.1, two 64-bit products per vector without it.</remarks>
    public static bool HasTrialFactor128(ulong value) => HasTrialFactor128Core(value: value);
    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a value, testing one prime at a time.</summary>
    /// <param name="value">The value; every prime divides zero, and a prime divides itself.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>The scalar rung of <see cref="HasTrialFactor512(ulong)"/>, with the same contract.</remarks>
    public static bool HasTrialFactorScalar(ulong value) => HasTrialFactorScalarCore(value: value);

    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a value, on the widest accelerated rung.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>Instruction-set dispatch folds when the caller is compiled; every rung returns the same answer.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasTrialFactorCore(ulong value) {
        if (Vector512.IsHardwareAccelerated) { return HasTrialFactor512Core(value: value); }
        if (Vector256.IsHardwareAccelerated) { return HasTrialFactor256Core(value: value); }
        if (Vector128.IsHardwareAccelerated) { return HasTrialFactor128Core(value: value); }

        return HasTrialFactorScalarCore(value: value);
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasTrialFactor512Core(ulong value) {
        if (Avx512BW.IsSupported) {
            ref var inverses = ref NarrowTrialDivisionRun(offset: 0);
            ref var ceilings = ref NarrowTrialDivisionRun(offset: TrialDivision.NarrowCeilings);
            ref var lowWeights = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldLowWeights);
            ref var highWeights = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldHighWeights);
            ref var offsets = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldOffsets);
            var digits = value ^ TrialDivisionDigitBias;
            var low = Vector512.Create(value: ((uint)digits)).AsInt16();
            var high = Vector512.Create(value: ((uint)(digits >>> 32))).AsInt16();

            for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector512<uint>.Count) {
                var folded = ((Avx512BW.MultiplyAddAdjacent(left: low, right: Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref lowWeights).AsInt16())
                    + Avx512BW.MultiplyAddAdjacent(left: high, right: Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref highWeights).AsInt16())).AsUInt32()
                    + Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref offsets));

                if (Vector512.LessThanOrEqualAny(
                    left: (folded * Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref inverses)),
                    right: Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref ceilings)
                )) { return true; }
            }

            return false;
        }

        ref var wideInverses = ref TrialDivisionRun(offset: 0);
        ref var wideCeilings = ref TrialDivisionRun(offset: TrialDivision.Ceilings);
        var broadcast = Vector512.Create(value: value);

        for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector512<ulong>.Count) {
            if (Vector512.LessThanOrEqualAny(
                left: (broadcast * Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref wideInverses)),
                right: Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref wideCeilings)
            )) { return true; }
        }

        return false;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasTrialFactor256Core(ulong value) {
        if (Avx2.IsSupported) {
            ref var inverses = ref NarrowTrialDivisionRun(offset: 0);
            ref var ceilings = ref NarrowTrialDivisionRun(offset: TrialDivision.NarrowCeilings);
            ref var lowWeights = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldLowWeights);
            ref var highWeights = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldHighWeights);
            ref var offsets = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldOffsets);
            var digits = value ^ TrialDivisionDigitBias;
            var low = Vector256.Create(value: ((uint)digits)).AsInt16();
            var high = Vector256.Create(value: ((uint)(digits >>> 32))).AsInt16();

            for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector256<uint>.Count) {
                var folded = ((Avx2.MultiplyAddAdjacent(left: low, right: Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref lowWeights).AsInt16())
                    + Avx2.MultiplyAddAdjacent(left: high, right: Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref highWeights).AsInt16())).AsUInt32()
                    + Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref offsets));

                if (Vector256.LessThanOrEqualAny(
                    left: (folded * Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref inverses)),
                    right: Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref ceilings)
                )) { return true; }
            }

            return false;
        }

        ref var wideInverses = ref TrialDivisionRun(offset: 0);
        ref var wideCeilings = ref TrialDivisionRun(offset: TrialDivision.Ceilings);
        var broadcast = Vector256.Create(value: value);

        for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector256<ulong>.Count) {
            if (Vector256.LessThanOrEqualAny(
                left: (broadcast * Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref wideInverses)),
                right: Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref wideCeilings)
            )) { return true; }
        }

        return false;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasTrialFactor128Core(ulong value) {
        if (Sse41.IsSupported) {
            ref var inverses = ref NarrowTrialDivisionRun(offset: 0);
            ref var ceilings = ref NarrowTrialDivisionRun(offset: TrialDivision.NarrowCeilings);
            ref var lowWeights = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldLowWeights);
            ref var highWeights = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldHighWeights);
            ref var offsets = ref NarrowTrialDivisionRun(offset: TrialDivision.FoldOffsets);
            var digits = value ^ TrialDivisionDigitBias;
            var low = Vector128.Create(value: ((uint)digits)).AsInt16();
            var high = Vector128.Create(value: ((uint)(digits >>> 32))).AsInt16();

            for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector128<uint>.Count) {
                var folded = ((Sse2.MultiplyAddAdjacent(left: low, right: Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref lowWeights).AsInt16())
                    + Sse2.MultiplyAddAdjacent(left: high, right: Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref highWeights).AsInt16())).AsUInt32()
                    + Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref offsets));

                if (Vector128.LessThanOrEqualAny(
                    left: (folded * Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref inverses)),
                    right: Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref ceilings)
                )) { return true; }
            }

            return false;
        }

        ref var wideInverses = ref TrialDivisionRun(offset: 0);
        ref var wideCeilings = ref TrialDivisionRun(offset: TrialDivision.Ceilings);
        var broadcast = Vector128.Create(value: value);

        for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector128<ulong>.Count) {
            if (Vector128.LessThanOrEqualAny(
                left: (broadcast * Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref wideInverses)),
                right: Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref wideCeilings)
            )) { return true; }
        }

        return false;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasTrialFactorScalarCore(ulong value) {
        ref var inverses = ref TrialDivisionRun(offset: 0);
        ref var ceilings = ref TrialDivisionRun(offset: TrialDivision.Ceilings);

        for (var offset = 0; (offset < TrialDivisionPrimeCount); ++offset) {
            if (unchecked((value * Unsafe.Add(elementOffset: offset, source: ref inverses))) <= Unsafe.Add(elementOffset: offset, source: ref ceilings)) { return true; }
        }

        return false;
    }

    /// <summary>Returns a reference to the first word of a run of the narrow trial-division table.</summary>
    /// <param name="offset">The run's offset, in words.</param>
    /// <returns>The reference.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe ref uint NarrowTrialDivisionRun(int offset) => ref TrialDivision.NarrowStart[offset];

    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a 32-bit value, testing sixteen primes per 512-bit vector.</summary>
    /// <param name="value">The value; every prime divides zero, and a prime divides itself.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>
    /// The 32-bit form of <see cref="HasTrialFactor512(ulong)"/>: a value is a multiple of <c>p</c> exactly when its
    /// product with <c>p</c>'s inverse modulo <c>2^32</c> does not exceed <c>floor((2^32 - 1) / p)</c>. The rung needs no
    /// instruction set beyond the host's accelerated vectors: a host without 512-bit vectors runs it in software. The
    /// law <c>prime-field.trial-division-rungs-vs-remainder</c> compares every rung with remainders.
    /// </remarks>
    public static bool HasNarrowTrialFactor512(uint value) => HasNarrowTrialFactor512Core(value: value);
    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a 32-bit value, testing eight primes per 256-bit vector.</summary>
    /// <param name="value">The value; every prime divides zero, and a prime divides itself.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>The 256-bit rung of <see cref="HasNarrowTrialFactor512(uint)"/>, with the same contract.</remarks>
    public static bool HasNarrowTrialFactor256(uint value) => HasNarrowTrialFactor256Core(value: value);
    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a 32-bit value, testing four primes per 128-bit vector.</summary>
    /// <param name="value">The value; every prime divides zero, and a prime divides itself.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>The 128-bit rung of <see cref="HasNarrowTrialFactor512(uint)"/>, with the same contract.</remarks>
    public static bool HasNarrowTrialFactor128(uint value) => HasNarrowTrialFactor128Core(value: value);
    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a 32-bit value, testing one prime at a time.</summary>
    /// <param name="value">The value; every prime divides zero, and a prime divides itself.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>The scalar rung of <see cref="HasNarrowTrialFactor512(uint)"/>, with the same contract.</remarks>
    public static bool HasNarrowTrialFactorScalar(uint value) => HasNarrowTrialFactorScalarCore(value: value);

    /// <summary>Returns whether an odd prime from three through <see cref="TrialDivisionCeiling"/> divides a 32-bit value, on the widest accelerated rung.</summary>
    /// <param name="value">The value.</param>
    /// <returns>Whether one of the <see cref="TrialDivisionPrimeCount"/> odd primes divides <paramref name="value"/>.</returns>
    /// <remarks>Instruction-set dispatch folds when the caller is compiled; every rung returns the same answer.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool HasNarrowTrialFactorCore(uint value) {
        if (Vector512.IsHardwareAccelerated) { return HasNarrowTrialFactor512Core(value: value); }
        if (Vector256.IsHardwareAccelerated) { return HasNarrowTrialFactor256Core(value: value); }
        if (Vector128.IsHardwareAccelerated) { return HasNarrowTrialFactor128Core(value: value); }

        return HasNarrowTrialFactorScalarCore(value: value);
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasNarrowTrialFactor512Core(uint value) {
        ref var inverses = ref NarrowTrialDivisionRun(offset: 0);
        ref var ceilings = ref NarrowTrialDivisionRun(offset: TrialDivision.NarrowCeilings);
        var broadcast = Vector512.Create(value: value);

        for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector512<uint>.Count) {
            if (Vector512.LessThanOrEqualAny(
                left: (broadcast * Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref inverses)),
                right: Vector512.LoadUnsafe(elementOffset: ((nuint)offset), source: ref ceilings)
            )) { return true; }
        }

        return false;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasNarrowTrialFactor256Core(uint value) {
        ref var inverses = ref NarrowTrialDivisionRun(offset: 0);
        ref var ceilings = ref NarrowTrialDivisionRun(offset: TrialDivision.NarrowCeilings);
        var broadcast = Vector256.Create(value: value);

        for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector256<uint>.Count) {
            if (Vector256.LessThanOrEqualAny(
                left: (broadcast * Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref inverses)),
                right: Vector256.LoadUnsafe(elementOffset: ((nuint)offset), source: ref ceilings)
            )) { return true; }
        }

        return false;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasNarrowTrialFactor128Core(uint value) {
        ref var inverses = ref NarrowTrialDivisionRun(offset: 0);
        ref var ceilings = ref NarrowTrialDivisionRun(offset: TrialDivision.NarrowCeilings);
        var broadcast = Vector128.Create(value: value);

        for (var offset = 0; (offset < TrialDivisionPrimeCount); offset += Vector128<uint>.Count) {
            if (Vector128.LessThanOrEqualAny(
                left: (broadcast * Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref inverses)),
                right: Vector128.LoadUnsafe(elementOffset: ((nuint)offset), source: ref ceilings)
            )) { return true; }
        }

        return false;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasNarrowTrialFactorScalarCore(uint value) {
        ref var inverses = ref NarrowTrialDivisionRun(offset: 0);
        ref var ceilings = ref NarrowTrialDivisionRun(offset: TrialDivision.NarrowCeilings);

        for (var offset = 0; (offset < TrialDivisionPrimeCount); ++offset) {
            if (unchecked((value * Unsafe.Add(elementOffset: offset, source: ref inverses))) <= Unsafe.Add(elementOffset: offset, source: ref ceilings)) { return true; }
        }

        return false;
    }
}
