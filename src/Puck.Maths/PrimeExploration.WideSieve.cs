using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExploration {
    private const int WideBucketBits = 15;
    private const int WideBucketBytes = (1 << WideBucketBits);
    private const uint WideBucketMask = (WideBucketBytes - 1);
    private const int WidePageBytes = 8192;
    private const int WideInitialSlabPages = 16;
    private const int WideMaximumSlabPages = 2048;
    private const int WidePhaseBits = 23;
    private const uint WideMediumLimit = (3 * WideBucketBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct WideState {
        internal uint Indices;
        internal uint Quotient;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WideNativePage {
        internal WideState* End;
        internal WideNativePage* Next;
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct WideStep(byte mask, byte factor, byte carry, uint next) {
        internal readonly byte Mask = mask;
        internal readonly byte Factor = factor;
        internal readonly byte Carry = carry;
        internal readonly uint Next = next;
    }
    private static class WideWheelTables {
        internal static readonly byte[] Residues = CreateWideResidues();
        internal static readonly WideStep[] NumericSteps = CreateWideSteps(layout: PrimeByteLayout.Numeric);
        internal static readonly WideStep[] AlgebraicSteps = CreateWideSteps(layout: PrimeByteLayout.Algebraic);
        internal static readonly byte[] FirstSources = CreateWideFirstSources();
    }

    private static byte[] CreateWideResidues() {
        var residues = new byte[48];
        var index = 0;

        for (var value = 1; (value < 210); ++value) {
            if (((value % 2) != 0) && ((value % 3) != 0) && ((value % 5) != 0) && ((value % 7) != 0)) {
                residues[index++] = ((byte)value);
            }
        }
        return residues;
    }
    private static byte[] CreateWideFirstSources() {
        var sources = new byte[210];
        var residues = WideWheelTables.Residues;

        for (var residue = 0; (residue < sources.Length); ++residue) {
            byte source = 0;

            while (residues[source] < residue) { ++source; }
            sources[residue] = source;
        }
        return sources;
    }
    private static WideStep[] CreateWideSteps(PrimeByteLayout layout) {
        var steps = new WideStep[384];
        var residues = WideWheelTables.Residues;

        for (var channel = 0; (channel < 8); ++channel) {
            var primeResidue = PrimeWheel30.Residues[channel];

            for (var source = 0; (source < 48); ++source) {
                var current = residues[source];
                var nextSource = ((source == 47) ? 0 : (source + 1));
                var next = ((source == 47) ? 211 : residues[nextSource]);
                var target = ((byte)((primeResidue * current) % 30));

                _ = PrimeWheel30.TryChannel(channel: out var targetChannel, residue: target);
                var bit = ((layout == PrimeByteLayout.Numeric) ? PrimeWheel30.NumericIndex(channel: targetChannel) : targetChannel);
                // For p=30q+r, moving multiplier a to b advances q*(b-a) bytes plus these integer lifts.
                var carry = (((primeResidue * next) / 30) - ((primeResidue * current) / 30));

                steps[((channel * 48) + source)] = new WideStep(carry: ((byte)carry), factor: ((byte)(next - current)),
                    mask: ((byte)~(1 << bit)), next: ((uint)((channel * 48) + nextSource)));
            }
        }
        return steps;
    }

    // Each eight-byte state stores the prime quotient and a word containing its nine-bit wheel phase
    // above a byte offset. The 210-wheel skips multiples of seven already removed by the small-prime marker.
    // Absolute bucket positions belong to the scheduler, so even the final ulong block needs no full-width multiple.
    private sealed unsafe class WideSieve : IDisposable {
        private readonly CancellationToken m_cancellationToken;
        private readonly UpperPrimeStream m_primes;
        private readonly WideMedium? m_medium;
        private readonly ulong m_integerOrigin;
        private readonly ulong m_high;
        private readonly ulong m_origin;
        private readonly WideStep[] m_steps;
        private readonly nuint[] m_buckets;
        private readonly int m_bucketHorizon;

        private readonly List<nuint> m_slabs = [];

        private WideNativePage* m_freePages;
        private ulong m_currentOrdinal;

        private int m_nextSlabPages = WideInitialSlabPages;

        private int m_bucketIndex;
        private int m_primeIndex;
        private int m_primeCount;

        internal WideSieve(ulong low, ulong high, uint limit, PrimeByteLayout layout, CancellationToken cancellationToken) {
            m_cancellationToken = cancellationToken;
            m_high = high;
            m_origin = (low / 30);
            m_integerOrigin = (m_origin * 30);
            m_steps = ((layout == PrimeByteLayout.Numeric) ? WideWheelTables.NumericSteps : WideWheelTables.AlgebraicSteps);
            m_bucketHorizon = WideBucketHorizon(limit: limit);
            // Two horizons let the base reference slide without changing relative hot-loop indexing.
            m_buckets = new nuint[(2 * m_bucketHorizon)];
            m_primes = new UpperPrimeStream(cancellationToken: cancellationToken, limit: limit);
            try { m_medium = CreateMedium(high: high, layout: layout); } catch { m_primes.Dispose(); throw; }
        }

        internal void Mark(Span<byte> segment, ulong blockLow, ulong high) {
            m_medium?.Mark(blockLow: blockLow, high: high, segment: segment);
            var offset = (blockLow - m_origin);
            var consumed = 0;

            while (consumed < segment.Length) {
                m_cancellationToken.ThrowIfCancellationRequested();
                var ordinal = (offset >> WideBucketBits);

                if (ordinal != m_currentOrdinal) {
                    // Contiguous portions drain the preceding bucket before advancing one position.
                    // Compact only once per horizon; a hop is at most horizon-2, so the doubled array
                    // always contains the full future window, including immediately before compaction.
                    if (++m_bucketIndex == m_bucketHorizon) {
                        var tail = m_buckets.AsSpan(start: m_bucketHorizon);

                        tail.CopyTo(destination: m_buckets);
                        tail.Clear();
                        m_bucketIndex = 0;
                    }
                    m_currentOrdinal = ordinal;
                }
                var bucketOffset = ((int)(offset & WideBucketMask));
                var length = Math.Min(val1: (WideBucketBytes - bucketOffset), val2: (segment.Length - consumed));
                var wideHigh = ((((UInt128)((m_origin + offset) + ((uint)length))) * 30) - 1);
                var baseLimit = ((uint)((wideHigh > high) ? high : ((ulong)wideHigh)).SquareRoot());

                while (true) {
                    if (m_primeIndex == m_primeCount) {
                        m_primeCount = m_primes.Fill();
                        m_primeIndex = 0;
                        if (m_primeCount == 0) { break; }
                    }
                    var coordinate = m_primes.Coordinates[m_primeIndex];
                    var prime = (((coordinate >> 3) * 30) + PrimeWheel30.NumericResidues[((int)(coordinate & 7))]);

                    if (prime > baseLimit) { break; }
                    ++m_primeIndex;
                    AddPrime(coordinate: coordinate, prime: prime);
                }
                if (m_buckets[m_bucketIndex] != 0) {
                    var portion = segment.Slice(length: length, start: consumed);

                    if ((bucketOffset == 0) && (length == WideBucketBytes)) { MarkFullBucket(segment: portion); } else { MarkPartialBucket(segment: portion, start: bucketOffset); }
                }
                consumed += length;
                offset += ((uint)length);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddPrime(uint prime, uint coordinate) {
            // The byte origin is a multiple of thirty, hence cannot be a wheel candidate. Starting strictly
            // after it avoids a remainder correction; endpoint masks discard any candidate below the user's low.
            var multiplier = Math.Max(val1: ((ulong)prime), val2: ((m_integerOrigin / prime) + 1));
            var residue = ((int)(multiplier % 210));
            var source = WideWheelTables.FirstSources[residue];

            multiplier += ((uint)(WideWheelTables.Residues[source] - residue));
            // Wheel rounding can move the first product past ulong.MaxValue in the final interval.
            var wideMultiple = (((UInt128)multiplier) * prime);

            if (wideMultiple > m_high) { return; }
            var multiple = ((ulong)wideMultiple);
            var channel = PrimeWheel30.NumericChannels[((int)(coordinate & 7))];
            var offset = ((multiple / 30) - m_origin);
            var hop = ((int)((offset >> WideBucketBits) - m_currentOrdinal));
            ref var head = ref Unsafe.Add(source: ref MemoryMarshal.GetArrayDataReference(array: m_buckets), elementOffset: (m_bucketIndex + hop));

            Append(head: ref head, indices: ((uint)(offset & WideBucketMask)) |
                (((uint)((channel * 48) + source)) << WidePhaseBits), quotient: (coordinate >> 3));
        }
        private void MarkFullBucket(Span<byte> segment) {
            ref var output = ref MemoryMarshal.GetReference(span: segment);
            ref var steps = ref MemoryMarshal.GetArrayDataReference(array: m_steps);
            ref var buckets = ref Unsafe.Add(source: ref MemoryMarshal.GetArrayDataReference(array: m_buckets), elementOffset: m_bucketIndex);

            // Each event performs exactly one mark. An advance that stays in this bucket joins a fresh
            // output list, which the outer loop drains next. There is no per-prime segment-exit branch.
            while (buckets != 0) {
                var page = PageOf(cursor: ((WideState*)buckets));

                page->End = ((WideState*)buckets);
                buckets = 0;
                while (page != null) {
                    MarkFullPage(buckets: ref buckets, output: ref output, page: page, steps: ref steps);
                    var next = page->Next;

                    Recycle(page: page);
                    page = next;
                }
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void MarkFullPage(WideNativePage* page, ref byte output, ref WideStep steps, ref nuint buckets) {
            var current = ((WideState*)(page + 1));
            var end = page->End;

            // Keep only this page's event cursors and cached data references live in the hot loop.
            for (; (current != end); ++current) {
                var cursor = current->Indices & WideBucketMask;
                var quotient = current->Quotient;
                ref var step = ref Unsafe.Add(elementOffset: ((int)(current->Indices >> WidePhaseBits)), source: ref steps);

                // The stored offset has fifteen bits and this path receives a complete 32768-byte bucket.
                Unsafe.Add(elementOffset: ((int)cursor), source: ref output) &= step.Mask;
                cursor += ((quotient * step.Factor) + step.Carry);
                ref var head = ref Unsafe.Add(elementOffset: ((int)(cursor >> WideBucketBits)), source: ref buckets);

                Append(head: ref head, indices: (cursor & WideBucketMask) | (step.Next << WidePhaseBits), quotient: quotient);
            }
        }
        private void MarkPartialBucket(Span<byte> segment, int start) {
            var end = ((uint)(start + segment.Length));
            ref var output = ref MemoryMarshal.GetReference(span: segment);
            ref var steps = ref MemoryMarshal.GetArrayDataReference(array: m_steps);
            ref var buckets = ref Unsafe.Add(source: ref MemoryMarshal.GetArrayDataReference(array: m_buckets), elementOffset: m_bucketIndex);
            var page = PageOf(cursor: ((WideState*)buckets));

            page->End = ((WideState*)buckets);
            buckets = 0;

            // Partial callers preserve cursors beyond their requested prefix in the current bucket. Only
            // the detached input is consumed here, so those retained cursors await the next caller segment.
            while (page != null) {
                var current = ((WideState*)(page + 1));
                var inputEnd = page->End;

                for (; (current != inputEnd); ++current) {
                    var cursor = current->Indices & WideBucketMask;
                    var wheel = (current->Indices >> WidePhaseBits);
                    var quotient = current->Quotient;

                    while (cursor < end) {
                        ref var step = ref Unsafe.Add(elementOffset: ((int)wheel), source: ref steps);
                        // The scheduler retains offsets at or beyond the already-consumed prefix.
                        Unsafe.Add(elementOffset: (((int)cursor) - start), source: ref output) &= step.Mask;
                        cursor += ((quotient * step.Factor) + step.Carry);
                        wheel = step.Next;
                    }
                    ref var head = ref Unsafe.Add(elementOffset: ((int)(cursor >> WideBucketBits)), source: ref buckets);

                    Append(head: ref head, indices: (cursor & WideBucketMask) | (wheel << WidePhaseBits), quotient: quotient);
                }
                var next = page->Next;

                Recycle(page: page);
                page = next;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Append(ref nuint head, uint quotient, uint indices) {
            // The advance horizon bounds the bucket reference. An aligned boundary means a full page or null.
            // Each non-full write cursor points to one complete eight-byte state inside its owned page.
            if ((head & (WidePageBytes - 1)) == 0) {
                AppendPage(head: ref head, indices: indices, quotient: quotient);
            } else {
                var output = ((WideState*)head);

                output->Indices = indices;
                output->Quotient = quotient;
                head = ((nuint)(output + 1));
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AppendPage(ref nuint head, uint quotient, uint indices) {
            if (m_freePages == null) { AllocateSlab(); }
            var page = m_freePages;

            m_freePages = page->Next;
            page->Next = null;
            if (head != 0) {
                var previous = PageOf(cursor: ((WideState*)head));

                previous->End = ((WideState*)head);
                page->Next = previous;
            }
            var output = ((WideState*)(page + 1));

            output->Indices = indices;
            output->Quotient = quotient;
            head = ((nuint)(output + 1));
        }
        private void AllocateSlab() {
            var count = m_nextSlabPages;
            var allocation = NativeMemory.AlignedAlloc(alignment: WidePageBytes, byteCount: ((nuint)(count * WidePageBytes)));

            if (allocation == null) { throw new OutOfMemoryException(); }
            try { m_slabs.Add(item: ((nuint)allocation)); } catch { NativeMemory.AlignedFree(ptr: allocation); throw; }
            for (var index = 0; (index < count); ++index) {
                var page = ((WideNativePage*)(((byte*)allocation) + (index * WidePageBytes)));

                page->Next = m_freePages;
                m_freePages = page;
            }
            m_nextSlabPages = Math.Min(val1: WideMaximumSlabPages, val2: (count * 2));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static WideNativePage* PageOf(WideState* cursor) => ((WideNativePage*)((((nuint)cursor) - 1) & ~((nuint)(WidePageBytes - 1))));
        private void Recycle(WideNativePage* page) {
            page->Next = m_freePages;
            m_freePages = page;
        }

        public void Dispose() {
            try { m_medium?.Dispose(); } finally {
                try { m_primes.Dispose(); } finally {
                    // Slab ownership is independent of scheduler/free lists, including detached input on an exception.
                    foreach (var allocation in m_slabs) { NativeMemory.AlignedFree(ptr: ((void*)allocation)); }
                    m_slabs.Clear();
                    m_freePages = null;
                }
            }
        }

        private WideMedium? CreateMedium(ulong high, PrimeByteLayout layout) {
            // This interval intersects at most 158 periods of 210: fewer than 8192 possible primes.
            // Its quotient fits ushort and every future square lies below 98304²/30 < 2^32 byte offsets.
            var states = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: 8192);
            var count = 0;

            try {
                while (true) {
                    if (m_primeIndex == m_primeCount) {
                        m_primeCount = m_primes.Fill();
                        m_primeIndex = 0;
                        if (m_primeCount == 0) { break; }
                    }
                    var coordinate = m_primes.Coordinates[m_primeIndex];
                    var prime = (((coordinate >> 3) * 30) + PrimeWheel30.NumericResidues[((int)(coordinate & 7))]);
                    // Leave the first large prime at its original buffer index for the sparse scheduler.
                    if (prime > WideMediumLimit) { break; }
                    ++m_primeIndex;
                    var multiplier = Math.Max(val1: ((ulong)prime), val2: ((m_integerOrigin / prime) + 1));
                    var residue = (multiplier % 30);
                    byte source = 0;

                    while (PrimeWheel30.NumericResidues[source] < residue) { ++source; }
                    multiplier += (PrimeWheel30.NumericResidues[source] - residue);
                    var multiple = (((UInt128)multiplier) * prime);

                    if (multiple > high) { continue; }
                    var channel = PrimeWheel30.NumericChannels[((int)(coordinate & 7))];

                    states[count++] = new PrimeMarkState {
                        Data = ((uint)((((ulong)multiple) / 30) - m_origin)) | (((ulong)(coordinate >> 3)) << 48) |
                            (((ulong)source) << 32) | (((ulong)channel) << 40),
                    };
                }
                if (count != 0) { return new WideMedium(count: count, layout: layout, origin: m_origin, states: states); }
            } catch { ArrayPool<PrimeMarkState>.Shared.Return(states); throw; }
            ArrayPool<PrimeMarkState>.Shared.Return(states);
            return null;
        }
    }
    // Base generation uses the same cache-chunked sieve as the uint fast path. Buffered bit decoding
    // avoids callbacks and eight residue tests per byte, and no complete upper-prime table is retained.
    private sealed class UpperPrimeStream : IDisposable {
        private readonly CancellationToken m_cancellationToken;

        // Each coordinate is (prime / 30) << 3 | numericResidueIndex, an ascending key below 2^31.
        // The generator already owns those byte/bit coordinates, so preserve them until activation.
        internal readonly uint[] Coordinates = new uint[4096];

        private readonly byte[] m_bitmap;
        private readonly PrimeMarkState[] m_states;
        private readonly PacketBuckets m_buckets;

        private readonly int[] m_starts = new int[9];
        private readonly int[] m_active = new int[8];

        private readonly int m_stateCount;
        private readonly uint m_limit;

        private const ulong Origin = (65536UL / 30);

        private ulong m_nextBlock = Origin;

        private ulong m_block;
        private int m_word;
        private int m_words;

        internal UpperPrimeStream(uint limit, CancellationToken cancellationToken) {
            m_cancellationToken = cancellationToken;
            m_limit = limit;
            m_bitmap = ArrayPool<byte>.Shared.Rent(minimumLength: WideBucketBytes);
            m_states = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: PrimeKernels.BasePrimes.Length);
            try {
                m_stateCount = InitializePacketStates(blockLow: Origin, high: limit, low: 65536, states: m_states, usePreSieve: true);
                GroupPacketStates(states: m_states.AsSpan(length: m_stateCount, start: 0), starts: m_starts);
                m_buckets = new PacketBuckets(states: m_states.AsSpan(length: m_stateCount, start: 0), starts: m_starts, cutoff: (WideBucketBytes / 8));
            } catch {
                ArrayPool<PrimeMarkState>.Shared.Return(m_states);
                ArrayPool<byte>.Shared.Return(m_bitmap);
                throw;
            }
        }

        internal int Fill() {
            m_cancellationToken.ThrowIfCancellationRequested();
            var count = 0;

            while (count <= (Coordinates.Length - 64)) {
                if (m_word == m_words) {
                    m_cancellationToken.ThrowIfCancellationRequested();
                    if ((m_nextBlock * 30) > m_limit) { break; }
                    var length = ((int)Math.Min(val1: ((ulong)WideBucketBytes), val2: (((m_limit / 30UL) - m_nextBlock) + 1)));
                    var segment = m_bitmap.AsSpan(length: length, start: 0);

                    m_block = m_nextBlock;
                    var high = Math.Min(val1: ((ulong)m_limit), val2: (((m_block + ((uint)length)) * 30) - 1));

                    FilterSmallPrimes(blockLow: m_block, layout: PrimeByteLayout.Numeric, segment: segment);
                    MaskEndpoint(block: m_block, high: m_limit, index: 0, layout: PrimeByteLayout.Numeric, low: 65536, segment: segment);
                    MaskEndpoint(block: ((m_block + ((uint)length)) - 1), high: m_limit, index: (length - 1), layout: PrimeByteLayout.Numeric, low: 65536, segment: segment);
                    MarkBucketSegment(segment: segment, blockLow: m_block, high: high, states: m_states.AsSpan(length: m_stateCount, start: 0),
                        layout: PrimeByteLayout.Numeric, origin: Origin, starts: m_starts, smallActive: m_active, buckets: m_buckets);
                    m_words = ((length + 7) / 8);
                    m_bitmap.AsSpan(length: ((m_words * 8) - length), start: length).Clear();
                    m_word = 0;
                    m_nextBlock += ((uint)length);
                }
                var bits = MemoryMarshal.Cast<byte, ulong>(span: m_bitmap.AsSpan())[m_word];
                var firstCoordinate = (((uint)(m_block + ((uint)(m_word * 8)))) << 3);

                ++m_word;
                while (bits != 0) {
                    var bit = BitOperations.TrailingZeroCount(value: bits);

                    Coordinates[count++] = (firstCoordinate + ((uint)bit));
                    bits &= (bits - 1);
                }
            }
            return count;
        }

        public void Dispose() {
            m_buckets.Dispose();
            ArrayPool<PrimeMarkState>.Shared.Return(m_states);
            ArrayPool<byte>.Shared.Return(m_bitmap);
        }
    }
}
