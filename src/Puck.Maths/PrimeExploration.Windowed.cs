using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

public static partial class PrimeExploration {
    // Completes one segment with every upper base prime through the square root of the segment's high endpoint.
    // The streamed generator supplies the primes' coordinates in ascending batches, and the stateless marker divides
    // once per batch for every prime's first multiple in the segment. No per-prime state outlives the segment, so the
    // workspace is the segment bitmap, the generator's fixed buffers and one batch of primes and quotients,
    // independent of pi(sqrt(high)).
    private sealed class WindowedUpperMarker(CancellationToken cancellationToken) : IDisposable {
        private readonly uint[] m_primes = ArrayPool<uint>.Shared.Rent(minimumLength: UpperPrimeStream.BatchCapacity);
        private readonly ulong[] m_quotients = ArrayPool<ulong>.Shared.Rent(minimumLength: UpperPrimeStream.BatchCapacity);

        internal void Mark(Span<byte> segment, ulong blockLow, ulong low, ulong high) {
            using var stream = new UpperPrimeStream(cancellationToken: cancellationToken, limit: ((uint)high.SquareRoot()));
            int count;

            while ((count = stream.Fill()) != 0) {
                MarkBases(blockLow: blockLow, coordinates: stream.Coordinates.AsSpan(length: count, start: 0), high: high, low: low,
                    primes: m_primes, quotients: m_quotients, segment: segment);
            }
        }

        public void Dispose() {
            ArrayPool<ulong>.Shared.Return(array: m_quotients);
            ArrayPool<uint>.Shared.Return(array: m_primes);
        }
    }

    // The stateless marker: clears, in the segment of wheel bytes from blockLow that covers [low, high], every multiple
    // of each streamed prime from max(low, p^2). A coordinate (p/30)<<3 | k, with k the numeric index of p's residue,
    // gives both the prime and its clear-mask row. The first multiplier at or above max(low, p^2) is
    // max(floor((low-1)/p)+1, p), and one quotient rung computes floor((low-1)/p) for the whole batch: every streamed
    // prime exceeds 2^16, so each quotient is below the rungs' limit 2^48. One table rounds the multiplier up to a unit
    // modulo 30, and one wide product rejects a prime whose first wheel multiple lies past high; most primes above the
    // segment width have none.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MarkBases(ReadOnlySpan<uint> coordinates, Span<uint> primes, Span<ulong> quotients, Span<byte> segment,
        ulong blockLow, ulong low, ulong high) {
        var length = coordinates.Length;

        Debug.Assert(((low >= 7UL) && (primes.Length >= length) && (quotients.Length >= length)));
        DecodeUpperCoordinates(coordinates: coordinates, primes: primes);
        PrimeDivisionKernels.DivideGourdonQuotients(dividend: (low - 1UL), divisors: primes[..length], quotients: quotients);

        ref var coordinate = ref MemoryMarshal.GetReference(span: coordinates);
        ref var basePrime = ref MemoryMarshal.GetReference(span: primes);
        ref var quotient = ref MemoryMarshal.GetReference(span: quotients);
        ref var roundUp = ref MemoryMarshal.GetReference(span: PrimeWheel30.UnitRoundUps);
        ref var channel = ref MemoryMarshal.GetReference(span: PrimeWheel30.NumericChannels);
        ref var clearMasks = ref MemoryMarshal.GetReference(span: PrimeSieveKernels.ClearMasks);
        ref var first = ref MemoryMarshal.GetReference(span: segment);

        for (var index = 0; (index < length); ++index) {
            var prime = ((ulong)Unsafe.Add(elementOffset: index, source: ref basePrime));

            Debug.Assert(((prime > PrimeSieveKernels.PreSievePrimeLimit) && ((prime * prime) <= high)));
            var multiplier = Math.Max(val1: (Unsafe.Add(elementOffset: index, source: ref quotient) + 1UL), val2: prime);
            var round = Unsafe.Add(elementOffset: ((nint)(multiplier % PrimeWheel30.Modulus)), source: ref roundUp);

            multiplier += ((uint)(round >> 3));
            var multiple = (multiplier * prime);

            if ((ScaledResidueRing64.MultiplyHigh(left: multiplier, right: prime) != 0UL) || (multiple > high)) { continue; }
            var row = (Unsafe.Add(elementOffset: ((nint)(Unsafe.Add(elementOffset: index, source: ref coordinate) & (PrimeWheel30.ChannelCount - 1))), source: ref channel) * PrimeWheel30.ChannelCount);

            MarkWheelSteps(blockLow: blockLow, clearMasks: ref Unsafe.Add(elementOffset: row, source: ref clearMasks), high: high,
                multiple: multiple, prime: prime, segment: ref first, source: round & (PrimeWheel30.ChannelCount - 1));
        }
    }
    // Writes primes[i] = (coordinates[i] >> 3) * 30 + NumericResidues[coordinates[i] & 7].
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecodeUpperCoordinates(ReadOnlySpan<uint> coordinates, Span<uint> primes) {
        var length = coordinates.Length;
        ref var coordinate = ref MemoryMarshal.GetReference(span: coordinates);
        ref var prime = ref MemoryMarshal.GetReference(span: primes);
        ref var residue = ref MemoryMarshal.GetReference(span: PrimeWheel30.NumericResidues);
        var index = 0;

        if (Avx2.IsSupported) {
            var residues = Avx2.ConvertToVector256Int32(value: Vector128.CreateScalarUnsafe(value: Unsafe.ReadUnaligned<ulong>(source: ref residue)).AsByte()).AsUInt32();
            var channels = Vector256.Create(value: ((uint)(PrimeWheel30.ChannelCount - 1)));

            for (; ((index + Vector256<uint>.Count) <= length); index += Vector256<uint>.Count) {
                var value = Vector256.LoadUnsafe(elementOffset: ((nuint)index), source: ref coordinate);

                (((value >>> 3) * ((uint)PrimeWheel30.Modulus)) + Avx2.PermuteVar8x32(control: value & channels, left: residues))
                    .StoreUnsafe(destination: ref prime, elementOffset: ((nuint)index));
            }
        }
        for (; (index < length); ++index) {
            var value = Unsafe.Add(elementOffset: index, source: ref coordinate);

            Unsafe.Add(elementOffset: index, source: ref prime) = (((value >> 3) * PrimeWheel30.Modulus) + Unsafe.Add(elementOffset: ((nint)(value & (PrimeWheel30.ChannelCount - 1))), source: ref residue));
        }
    }
    // Clears multiple and every later wheel multiple of prime through high in the segment's bytes from blockLow, with
    // clearMasks the prime's eight-entry PrimeSieveKernels.ClearMasks row. The multiple lies in the segment and its multiplier has the
    // unit at phase source, so every visited byte lies inside the segment; source starts in 0..7 and stays there under
    // the masked increment, proving both eight-entry table offsets. The next multiple is a multiple of
    // prime, so it exceeds high exactly when it exceeds the last multiple through high, and (high - multiple) < advance
    // decides that without overflow.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkWheelSteps(ref byte segment, ulong blockLow, ulong prime, ulong multiple, ulong high, ref byte clearMasks, int source) {
        ref var firstGap = ref MemoryMarshal.GetReference(span: PrimeWheel30.Gaps);

        while (true) {
            var index = ((multiple / PrimeWheel30.Modulus) - blockLow);

            Unsafe.Add(elementOffset: ((nint)index), source: ref segment) &= Unsafe.Add(elementOffset: source, source: ref clearMasks);
            var advance = (prime * Unsafe.Add(elementOffset: source, source: ref firstGap));

            if ((high - multiple) < advance) { break; }
            multiple += advance;
            source = (source + 1) & (PrimeWheel30.ChannelCount - 1);
        }
    }
}
