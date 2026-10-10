using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Puck.Maths;

public static partial class PrimeExploration {
    private interface IPatternWrite { static abstract bool Initialize { get; } }
    private readonly struct InitializePattern : IPatternWrite { public static bool Initialize => true; }
    private readonly struct IntersectPattern : IPatternWrite { public static bool Initialize => false; }

    // The presieve removes every prime from 7 through this limit, the largest prime in SmallPrimeGroups. The Jacobsthal
    // function of the primorial 2·3·5·…·163 is 492 (OEIS A048670), so the patterns' intersection, which is exactly the
    // integers coprime to that primorial, never leaves more than 491 consecutive non-candidates: at most two consecutive
    // all-zero 64-bit words, and a candidate in any 17 consecutive wheel bytes. Restoring the primes themselves in the
    // first blocks only adds candidates.
    public const uint PreSievePrimeLimit = 163;

    // Pairing large primes with smaller ones keeps the combined periods compact. The grouping is inspired by
    // primesieve's PreSieve.cpp; every byte here is generated from divisibility, rather than copied upstream data.
    private static readonly (int First, int Second, int Third)[] SmallPrimeGroupTable = [
        (7, 23, 37), (11, 19, 31), (13, 17, 29), (41, 163, 1),
        (43, 157, 1), (47, 151, 1), (53, 149, 1), (59, 139, 1),
        (61, 137, 1), (67, 131, 1), (71, 127, 1), (73, 113, 1),
        (79, 109, 1), (83, 107, 1), (89, 103, 1), (97, 101, 1),
    ];

    /// <summary>Gets the presieve's prime groups, each a triple whose product is one pattern's period.</summary>
    /// <remarks>A third prime of one pads a pair. Together the groups hold each prime from 7 through
    /// <see cref="PreSievePrimeLimit"/> exactly once. The view is read-only: no caller can change a group.</remarks>
    public static ReadOnlySpan<(int First, int Second, int Third)> SmallPrimeGroups => SmallPrimeGroupTable;

    /// <summary>Holds one period of each <see cref="SmallPrimeGroups"/> pattern, built on first use.</summary>
    public static class SmallPrimePatterns {
        private static readonly byte[][] PeriodTable = CreateSmallPrimePatterns();

        /// <summary>Gets the number of patterns, one for each group in <see cref="SmallPrimeGroups"/>.</summary>
        public static int Count => PeriodTable.Length;

        /// <summary>Gets one period of a group's pattern.</summary>
        /// <param name="group">The index into <see cref="SmallPrimeGroups"/>, below <see cref="Count"/>.</param>
        /// <returns>
        /// The pattern as a read-only view, <c>First · Second · Third</c> bytes long; bit <c>b</c> of byte <c>i</c> is set
        /// exactly when <c>30 · i + </c><see cref="PrimeWheel30.NumericResidues"/><c>[b]</c> is coprime to the group's primes.
        /// </returns>
        public static ReadOnlySpan<byte> Period(int group) => PeriodTable[group];
    }

    private static byte[][] CreateSmallPrimePatterns() {
        var groups = SmallPrimeGroupTable;
        var patterns = new byte[groups.Length][];
        var residues = PrimeWheel30.NumericResidues;

        for (var group = 0; (group < groups.Length); ++group) {
            var (first, second, third) = groups[group];
            var pattern = new byte[((first * second) * third)];

            pattern.AsSpan().Fill(value: byte.MaxValue);
            foreach (var prime in ((ReadOnlySpan<int>)[first, second, third])) {
                if (prime == 1) { continue; }
                for (var bit = 0; (bit < PrimeWheel30.ChannelCount); ++bit) {
                    // Since gcd(30,p)=1, exactly one byte index modulo p has 30*index+residue divisible by p.
                    var start = 0;

                    while ((((PrimeWheel30.Modulus * start) + residues[bit]) % prime) != 0) { ++start; }
                    var clear = ((byte)~(1 << bit));

                    for (var index = start; (index < pattern.Length); index += prime) { pattern[index] &= clear; }
                }
            }
            patterns[group] = pattern;
        }
        return patterns;
    }

    public static void FilterSmallPrimes(Span<byte> segment, ulong blockLow) {
        var count = SmallPrimePatterns.Count;

        for (var group = 0; (group < count); group += 4) {
            var first = SmallPrimePatterns.Period(group: group);
            var second = SmallPrimePatterns.Period(group: (group + 1));
            var third = SmallPrimePatterns.Period(group: (group + 2));
            var fourth = SmallPrimePatterns.Period(group: (group + 3));
            var a = ((int)(blockLow % ((ulong)first.Length)));
            var b = ((int)(blockLow % ((ulong)second.Length)));
            var c = ((int)(blockLow % ((ulong)third.Length)));
            var d = ((int)(blockLow % ((ulong)fourth.Length)));
            var offset = 0;

            while (offset < segment.Length) {
                var length = Math.Min(val1: (segment.Length - offset), val2: Math.Min(val1: Math.Min(val1: (first.Length - a), val2: (second.Length - b)), val2: Math.Min(val1: (third.Length - c), val2: (fourth.Length - d))));
                var destination = segment.Slice(length: length, start: offset);
                var one = first.Slice(length: length, start: a);
                var two = second.Slice(length: length, start: b);
                var three = third.Slice(length: length, start: c);
                var four = fourth.Slice(length: length, start: d);

                if (group == 0) { FilterPatternGroup<InitializePattern>(destination: destination, first: one, fourth: four, second: two, third: three); } else { FilterPatternGroup<IntersectPattern>(destination: destination, first: one, fourth: four, second: two, third: three); }
                offset += length;
                a += length; if (a == first.Length) { a = 0; }
                b += length; if (b == second.Length) { b = 0; }
                c += length; if (c == third.Length) { c = 0; }
                d += length; if (d == fourth.Length) { d = 0; }
            }
        }
        // Periodic divisibility also removes p itself. Only the first six blocks can contain these exceptions.
        if (blockLow > (PreSievePrimeLimit / PrimeWheel30.Modulus)) { return; }
        foreach (var prime in PrimeKernels.BasePrimes) {
            if (prime <= 5) { continue; }
            if (prime > PreSievePrimeLimit) { break; }
            var block = (((ulong)prime) / PrimeWheel30.Modulus);

            if ((block < blockLow) || ((block - blockLow) >= ((ulong)segment.Length))) { continue; }
            _ = PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)(prime % PrimeWheel30.Modulus)));

            segment[((int)(block - blockLow))] |= ((byte)(1 << PrimeWheel30.NumericIndex(channel: channel)));
        }
    }

    private static void FilterPatternGroup<TWrite>(Span<byte> destination, ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second, ReadOnlySpan<byte> third, ReadOnlySpan<byte> fourth) where TWrite : struct, IPatternWrite {
        if (Vector512.IsHardwareAccelerated) {
            FilterPatternBatch<VectorLanes512, Vector512<byte>, TWrite>(destination: destination, first: first, fourth: fourth, second: second, third: third);
        } else if (Vector256.IsHardwareAccelerated) {
            FilterPatternBatch<VectorLanes256, Vector256<byte>, TWrite>(destination: destination, first: first, fourth: fourth, second: second, third: third);
        } else if (Vector128.IsHardwareAccelerated) {
            FilterPatternBatch<VectorLanes128, Vector128<byte>, TWrite>(destination: destination, first: first, fourth: fourth, second: second, third: third);
        } else { FilterPatternTail<TWrite>(destination: destination, first: first, fourth: fourth, index: 0, second: second, third: third); }
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void FilterPatternBatch<TLanes, TBytes, TWrite>(Span<byte> destination, ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second, ReadOnlySpan<byte> third, ReadOnlySpan<byte> fourth)
        where TLanes : struct, IByteVectorLanes<TBytes> where TBytes : struct where TWrite : struct, IPatternWrite {
        ref var output = ref MemoryMarshal.GetReference(span: destination);
        ref var a = ref MemoryMarshal.GetReference(span: first);
        ref var b = ref MemoryMarshal.GetReference(span: second);
        ref var c = ref MemoryMarshal.GetReference(span: third);
        ref var d = ref MemoryMarshal.GetReference(span: fourth);
        var index = 0;

        // All five spans have the same proven length. Every load/store is a whole vector inside that length.
        for (; (index <= (destination.Length - TLanes.ByteCount)); index += TLanes.ByteCount) {
            ApplyPatternVector<TLanes, TBytes, TWrite>(a: in a, b: in b, c: in c, d: in d, offset: ((nuint)index), output: ref output);
        }
        if ((index < destination.Length) && (destination.Length >= TLanes.ByteCount)) {
            // Overlap the last complete vector: repeating an AND or the same initialization is idempotent.
            // This stays inside every span and avoids a scalar tail at each periodic-table boundary.
            ApplyPatternVector<TLanes, TBytes, TWrite>(ref output, in a, in b, in c, in d, ((nuint)(destination.Length - TLanes.ByteCount)));
            return;
        }
        FilterPatternTail<TWrite>(destination: destination, first: first, fourth: fourth, index: index, second: second, third: third);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyPatternVector<TLanes, TBytes, TWrite>(ref byte output, in byte a, in byte b,
        in byte c, in byte d, nuint offset)
        where TLanes : struct, IByteVectorLanes<TBytes> where TBytes : struct where TWrite : struct, IPatternWrite {
        var mask = TLanes.And(left: TLanes.And(left: TLanes.Load(elementOffset: offset, source: in a), right: TLanes.Load(elementOffset: offset, source: in b)),
            right: TLanes.And(left: TLanes.Load(elementOffset: offset, source: in c), right: TLanes.Load(elementOffset: offset, source: in d)));

        TLanes.Store((TWrite.Initialize ? mask : TLanes.And(left: TLanes.Load(elementOffset: offset, source: in output), right: mask)), ref output, offset);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterPatternTail<TWrite>(Span<byte> destination, ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second, ReadOnlySpan<byte> third, ReadOnlySpan<byte> fourth, int index) where TWrite : struct, IPatternWrite {
        ref var output = ref MemoryMarshal.GetReference(span: destination);
        ref var a = ref MemoryMarshal.GetReference(span: first);
        ref var b = ref MemoryMarshal.GetReference(span: second);
        ref var c = ref MemoryMarshal.GetReference(span: third);
        ref var d = ref MemoryMarshal.GetReference(span: fourth);

        // FilterSmallPrimes gives equal-length spans; FilterPatternBatch starts at its first unvisited byte.
        for (; (index < destination.Length); ++index) {
            var mask = ((byte)(Unsafe.Add(elementOffset: index, source: ref a) & Unsafe.Add(elementOffset: index, source: ref b) & Unsafe.Add(elementOffset: index, source: ref c) & Unsafe.Add(elementOffset: index, source: ref d)));

            Unsafe.Add(elementOffset: index, source: ref output) = (TWrite.Initialize ? mask : ((byte)(Unsafe.Add(elementOffset: index, source: ref output) & mask)));
        }
    }
}
