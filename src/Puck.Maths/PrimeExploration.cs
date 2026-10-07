using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>Selects how a sieving prime advances through a thirty-wheel segment.</summary>
public enum PrimeSieveStrategy {
    /// <summary>Advances coprime multipliers in numeric order using the eight wheel gaps.</summary>
    WheelSteps,
    /// <summary>Marks eight fixed-bit streams, each advancing by the prime in byte coordinates.</summary>
    EightStreams,
    /// <summary>Marks eight-multiple packets, recomputing each prime's start for every segment.</summary>
    UnrolledPackets,
    /// <summary>Marks eight-multiple packets with positions carried between whole segments.</summary>
    CarriedPackets,
    /// <summary>Carries packet positions through sixteen-KiB marking chunks inside each segment.</summary>
    CacheBlockedPackets,
    /// <summary>Carries packet positions and specializes masks and integer lifts for each prime residue and bit layout.</summary>
    SpecializedPackets,
    /// <summary>Specializes packets and groups active medium-prime states by their saved wheel phase.</summary>
    PhaseSortedPackets,
    /// <summary>Uses quotient states, cache-sized small-prime chunks and direct medium-prime phase buckets; segment size is an adaptive maximum.</summary>
    BucketPackets,
}
/// <summary>Selects the residue represented by each bit of a thirty-wheel byte.</summary>
public enum PrimeByteLayout {
    /// <summary>Orders bits by residues 1, 7, 11, 13, 17, 19, 23, 29.</summary>
    Numeric,
    /// <summary>Orders bits by the multiplicative channels 1, 7, 19, 13, 11, 17, 29, 23.</summary>
    Algebraic,
}
/// <summary>Selects complete sieving or a bounded sieve followed by exact primality decisions.</summary>
public enum PrimeSieveMode {
    /// <summary>Chooses complete sieving or bounded presieving from interval width and upper-base workspace.</summary>
    Automatic,
    /// <summary>Generates every required base prime and carries useful upper-prime cursors in segment buckets.</summary>
    Eratosthenes,
    /// <summary>Sieves with primes through 65,535 and decides surviving candidates exactly.</summary>
    Presieve,
}
/// <summary>Explores primes throughout the unsigned sixty-four-bit domain using thirty-wheel coordinates.</summary>
public static partial class PrimeExploration {
    private static readonly byte[] NumericClearMasks = CreateClearMasks(layout: PrimeByteLayout.Numeric);
    private static readonly byte[] AlgebraicClearMasks = CreateClearMasks(layout: PrimeByteLayout.Algebraic);

    private static ReadOnlySpan<byte> WheelGaps => [6, 4, 2, 4, 2, 4, 6, 2];

    private static byte[] CreateClearMasks(PrimeByteLayout layout) {
        var masks = new byte[64];

        for (byte primeChannel = 0; (primeChannel < 8); ++primeChannel) {
            for (var source = 0; (source < 8); ++source) {
                var target = PrimeWheel30.Multiply(left: primeChannel, right: PrimeWheel30.NumericChannels[source]);
                var bit = ((layout == PrimeByteLayout.Numeric) ? PrimeWheel30.NumericIndex(channel: target) : target);

                masks[((primeChannel * 8) + source)] = ((byte)~(1 << bit));
            }
        }
        return masks;
    }

    /// <summary>Reports every prime in a closed interval in ascending numeric order.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound; a value below <paramref name="low"/> gives an empty interval.</param>
    /// <param name="onPrime">The callback invoked once for each prime.</param>
    /// <param name="segmentBytes">The positive number of thirty-integer blocks per segment; BucketPackets treats it as an adaptive maximum.</param>
    /// <param name="strategy">The wheel marking strategy.</param>
    /// <param name="layout">The order of residue bits within each byte.</param>
    /// <param name="mode">The complete-sieve or bounded-presieve policy.</param>
    /// <param name="usePreSieve">Uses periodic small-prime patterns through 163 instead of individual marks for those primes.</param>
    /// <param name="cancellationToken">Cancels before starting, between segments and during upper-base generation.</param>
    /// <remarks>
    /// <para>Each segment uses one byte per thirty integers. Two, three, and five are reported separately; partial
    /// first and final bytes are masked, and one is never reported. Scalar marking starts at each prime's square;
    /// periodic filtering restores the small primes themselves before applying the endpoint masks.</para>
    /// <para>Automatic mode uses <see cref="ResolveMode(ulong, ulong, PrimeSieveMode)"/> to choose complete
    /// sieving from the interval width and a conservative upper-base workspace bound. Presieve uses the shared
    /// primes through 65,535 and decides remaining survivors with the exact word test. Survivors below 65537
    /// squared are already proven prime and need no further test. Neither policy stores a full-interval bitmap.</para>
    /// <para>Eratosthenes mode streams all required uint base primes from the optimized thirty-wheel sieve.
    /// Useful upper primes carry eight-byte cursor states through sparse 32-KiB segment buckets; unused primes
    /// are discarded. Base generation still has a cost determined by the upper bound, even for a narrow high
    /// interval. Explicit Eratosthenes bypasses the automatic policy's workspace bound.</para>
    /// <para>Callbacks may stop enumeration by throwing; the segment and base-generation bitmap are returned to
    /// their pools even when a callback throws, as are packet-state buffers. Packet strategies use eight-byte
    /// state payloads for base primes through 65,535; upper bases through 98,304 reuse medium packets, and
    /// larger bases use the sparse bucket scheduler. Native state slabs are freed on exit as well. Independent
    /// calls share immutable small-prime and wheel-mask tables.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onPrime"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentBytes"/> is outside
    /// <c>[1, Array.MaxLength]</c>, or <paramref name="strategy"/>, <paramref name="layout"/>, or
    /// <paramref name="mode"/> is not a defined value.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static void Enumerate(
        ulong low,
        ulong high,
        Action<ulong> onPrime,
        int segmentBytes = 32768,
        PrimeSieveStrategy strategy = PrimeSieveStrategy.BucketPackets,
        PrimeByteLayout layout = PrimeByteLayout.Numeric,
        PrimeSieveMode mode = PrimeSieveMode.Automatic,
        bool usePreSieve = true,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(onPrime);
        _ = Explore(cancellationToken: cancellationToken, high: high, layout: layout, low: low, mode: mode, onPrime: onPrime, segmentBytes: segmentBytes, strategy: strategy, usePreSieve: usePreSieve);
    }
    /// <summary>Counts primes in a closed interval without delivering individual values.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound; a value below <paramref name="low"/> gives zero.</param>
    /// <param name="segmentBytes">The positive number of thirty-integer blocks per segment; BucketPackets treats it as an adaptive maximum.</param>
    /// <param name="strategy">The wheel marking strategy.</param>
    /// <param name="layout">The order of residue bits within each byte.</param>
    /// <param name="mode">The complete-sieve or bounded-presieve policy.</param>
    /// <param name="usePreSieve">Uses periodic small-prime patterns through 163 instead of individual marks for those primes.</param>
    /// <param name="cancellationToken">Cancels before starting, between segments and during upper-base generation.</param>
    /// <returns>The number of primes in the interval.</returns>
    /// <remarks>Shares enumeration's marking, endpoint masks, and base-prime policy. Complete sieving counts
    /// surviving bits with word population counts; bounded presieving decides only survivors not already proven by its base primes.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentBytes"/> is outside
    /// <c>[1, Array.MaxLength]</c>, or an option is not a defined value.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static ulong Count(
        ulong low,
        ulong high,
        int segmentBytes = 32768,
        PrimeSieveStrategy strategy = PrimeSieveStrategy.BucketPackets,
        PrimeByteLayout layout = PrimeByteLayout.Numeric,
        PrimeSieveMode mode = PrimeSieveMode.Automatic,
        bool usePreSieve = true,
        CancellationToken cancellationToken = default
    ) => Explore(cancellationToken: cancellationToken, high: high, layout: layout, low: low, mode: mode, onPrime: null, segmentBytes: segmentBytes, strategy: strategy, usePreSieve: usePreSieve);

    internal static ulong CountWithWork(ulong low, ulong high, CancellationToken cancellationToken, PrimeBitmapWork? work) =>
        Explore(cancellationToken: cancellationToken, high: high, layout: PrimeByteLayout.Numeric, low: low,
            mode: PrimeSieveMode.Automatic, onPrime: null, segmentBytes: 32768, strategy: PrimeSieveStrategy.BucketPackets,
            usePreSieve: true, work: work);

    // A consumer sees endpoint-masked segments and may stop without exceptions. Internal callers use
    // numeric layout and handle the three exceptional primes before entering this bitmap-only path.
    private delegate bool SegmentConsumer(ReadOnlySpan<byte> segment, ulong blockLow, ulong segmentHigh, bool testSurvivors);

    private static unsafe ulong Explore(ulong low, ulong high, Action<ulong>? onPrime, int segmentBytes,
        PrimeSieveStrategy strategy, PrimeByteLayout layout, PrimeSieveMode mode, bool usePreSieve, CancellationToken cancellationToken,
        SegmentConsumer? onSegment = null, PrimeBitmapWork? work = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(segmentBytes, Array.MaxLength);
        if (((uint)strategy) > ((uint)PrimeSieveStrategy.BucketPackets)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(strategy));
        }
        if (((uint)layout) > ((uint)PrimeByteLayout.Algebraic)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(layout));
        }
        if (((uint)mode) > ((uint)PrimeSieveMode.Presieve)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(mode));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (high < low) { return 0; }

        var count = 0UL;

        if ((low <= 2UL) && (high >= 2UL)) { ++count; onPrime?.Invoke(2UL); cancellationToken.ThrowIfCancellationRequested(); }
        if ((low <= 3UL) && (high >= 3UL)) { ++count; onPrime?.Invoke(3UL); cancellationToken.ThrowIfCancellationRequested(); }
        if ((low <= 5UL) && (high >= 5UL)) { ++count; onPrime?.Invoke(5UL); cancellationToken.ThrowIfCancellationRequested(); }
        if (high < 7UL) { return count; }

        low = Math.Max(val1: low, val2: 7UL);
        var testSurvivors = (ResolveMode(high: high, low: low, mode: mode) == PrimeSieveMode.Presieve);
        var baseLimit = high.SquareRoot();

        segmentBytes = ResolveSegmentBytes(high: high, segmentBytes: segmentBytes, strategy: strategy);
        using var wideSieve = ((!testSurvivors && (baseLimit > 65535UL))
            ? new WideSieve(cancellationToken: cancellationToken, high: high, layout: layout, limit: ((uint)baseLimit), low: low)
            : null);
        var blockLow = (low / 30UL);
        var lastBlock = (high / 30UL);
        var requestedBytes = ((int)Math.Min(val1: ((ulong)segmentBytes), val2: ((lastBlock - blockLow) + 1UL)));
        var alignmentPadding = ((requestedBytes <= (Array.MaxLength - 63)) ? 63 : 0);
        var storage = ArrayPool<byte>.Shared.Rent(minimumLength: (requestedBytes + alignmentPadding));
        PrimeMarkState[]? stateStorage = null;
        PrimeMarkState[]? reorderStorage = null;
        PacketBuckets? buckets = null;
        var stateCount = 0;
        var activeStateCount = 0;
        var stateOrigin = blockLow;
        Span<int> groupStarts = stackalloc int[9];
        Span<int> groupActive = stackalloc int[8];

        groupActive.Clear();

        fixed (byte* storagePointer = storage) {
            var storageOffset = ((alignmentPadding == 0) ? 0 : ((int)((64 - (((nuint)storagePointer) & 63)) & 63)));

            try {
                if (strategy >= PrimeSieveStrategy.UnrolledPackets) {
                    stateStorage = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: PrimeKernels.BasePrimes.Length);
                    if (strategy != PrimeSieveStrategy.UnrolledPackets) {
                        stateCount = InitializePacketStates(blockLow: blockLow, high: high, low: low, states: stateStorage, usePreSieve: usePreSieve);
                        if (strategy >= PrimeSieveStrategy.SpecializedPackets) {
                            GroupPacketStates(stateStorage.AsSpan(length: stateCount, start: 0), groupStarts);
                            if (strategy == PrimeSieveStrategy.BucketPackets) {
                                buckets = new PacketBuckets(stateStorage.AsSpan(length: stateCount, start: 0), groupStarts, (Math.Min(val1: segmentBytes, val2: 32768) / 8));
                            }
                            if (strategy == PrimeSieveStrategy.PhaseSortedPackets) {
                                var largestGroup = 0;

                                for (var group = 0; (group < 8); ++group) { largestGroup = Math.Max(val1: largestGroup, val2: (groupStarts[(group + 1)] - groupStarts[group])); }
                                reorderStorage = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: largestGroup);
                            }
                        }
                    }
                }
                while (blockLow <= lastBlock) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = ((int)Math.Min(val1: ((ulong)segmentBytes), val2: ((lastBlock - blockLow) + 1UL)));
                    var blockHigh = ((blockLow + ((ulong)length)) - 1UL);
                    var segmentLow = Math.Max(val1: low, val2: (blockLow * 30UL));
                    // The last ulong block has residues beyond ulong.MaxValue, so form its end in the wider carrier.
                    var wideEnd = ((((UInt128)blockHigh) * 30UL) + 29UL);
                    var segmentHigh = ((wideEnd > high) ? high : (ulong)wideEnd);
                    var segment = storage.AsSpan(length: length, start: storageOffset);

                    if (usePreSieve) { FilterSmallPrimes(blockLow: blockLow, layout: layout, segment: segment); } else { segment.Fill(value: byte.MaxValue); }
                    MaskEndpoint(block: blockLow, high: high, index: 0, layout: layout, low: low, segment: segment);
                    MaskEndpoint(block: blockHigh, high: high, index: (length - 1), layout: layout, low: low, segment: segment);
                    if (stateStorage is not null) {
                        if (strategy == PrimeSieveStrategy.UnrolledPackets) {
                            stateCount = InitializePacketStates(blockLow: blockLow, high: segmentHigh, low: segmentLow, states: stateStorage, usePreSieve: usePreSieve);
                            activeStateCount = 0;
                            stateOrigin = blockLow;
                        }
                        if (buckets is not null) {
                            MarkBucketSegment(segment, blockLow, segmentHigh, stateStorage.AsSpan(length: stateCount, start: 0), layout,
                                stateOrigin, groupStarts, groupActive, buckets);
                        } else if (strategy >= PrimeSieveStrategy.SpecializedPackets) {
                            MarkGroupedPacketSegment(segment, blockLow, segmentHigh, stateStorage.AsSpan(length: stateCount, start: 0), layout,
                                stateOrigin, groupStarts, groupActive, reorderStorage);
                        } else {
                            MarkPacketChunks(segment, blockLow, segmentHigh, stateStorage.AsSpan(length: stateCount, start: 0), layout,
                                ((strategy == PrimeSieveStrategy.CacheBlockedPackets) ? PacketChunkBytes : length), stateOrigin, ref activeStateCount);
                        }
                    } else {
                        MarkBases(PrimeKernels.BasePrimes, segment, blockLow, segmentLow, segmentHigh, strategy, layout, usePreSieve);
                    }
                    wideSieve?.Mark(blockLow: blockLow, high: segmentHigh, segment: segment);
                    work?.AddSegment(bytes: length);

                    var decideSegment = (testSurvivors && (segmentHigh >= ProvenPresieveLimit));

                    if (onSegment is not null) {
                        if (!onSegment(segment, blockLow, segmentHigh, decideSegment)) { break; }
                    } else {
                        count += (((onPrime is null) && !decideSegment)
                            ? CountBits(segment: segment)
                            : ReportSegment(blockLow: blockLow, layout: layout, onPrime: onPrime, segment: segment, testSurvivors: decideSegment));
                    }
                    if (blockHigh == lastBlock) { break; }
                    blockLow = (blockHigh + 1UL);
                }
            } finally {
                buckets?.Dispose();
                if (reorderStorage is not null) { ArrayPool<PrimeMarkState>.Shared.Return(reorderStorage); }
                if (stateStorage is not null) { ArrayPool<PrimeMarkState>.Shared.Return(stateStorage); }
                ArrayPool<byte>.Shared.Return(storage);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }
    private static ulong CountBits(ReadOnlySpan<byte> segment) {
        var words = MemoryMarshal.Cast<byte, ulong>(span: segment);
        var count = 0UL;

        ref var first = ref MemoryMarshal.GetReference(span: words);
        var index = 0;

        for (; (index <= (words.Length - 4)); index += 4) {
            count += ((ulong)BitOperations.PopCount(value: Unsafe.Add(elementOffset: index, source: ref first)));
            count += ((ulong)BitOperations.PopCount(value: Unsafe.Add(elementOffset: (index + 1), source: ref first)));
            count += ((ulong)BitOperations.PopCount(value: Unsafe.Add(elementOffset: (index + 2), source: ref first)));
            count += ((ulong)BitOperations.PopCount(value: Unsafe.Add(elementOffset: (index + 3), source: ref first)));
        }
        for (; (index < words.Length); ++index) { count += ((ulong)BitOperations.PopCount(value: Unsafe.Add(elementOffset: index, source: ref first))); }
        for (var tailIndex = (words.Length * sizeof(ulong)); (tailIndex < segment.Length); ++tailIndex) {
            count += ((ulong)BitOperations.PopCount(value: ((uint)segment[tailIndex])));
        }
        return count;
    }
    private static void MaskEndpoint(Span<byte> segment, int index, ulong block, ulong low, ulong high, PrimeByteLayout layout) {
        var residues = PrimeWheel30.NumericResidues;
        var channels = PrimeWheel30.NumericChannels;
        var blockValue = (((UInt128)block) * 30UL);

        for (var numeric = 0; (numeric < 8); ++numeric) {
            var value = (blockValue + residues[numeric]);

            if ((value < low) || (value > high)) {
                var bit = ((layout == PrimeByteLayout.Numeric) ? numeric : channels[numeric]);

                segment[index] &= ((byte)~(1 << bit));
            }
        }
    }
    private static ulong ReportSegment(ReadOnlySpan<byte> segment, ulong blockLow, PrimeByteLayout layout, bool testSurvivors, Action<ulong>? onPrime) {
        var residues = PrimeWheel30.NumericResidues;
        var channels = PrimeWheel30.NumericChannels;
        var count = 0UL;

        for (var index = 0; (index < segment.Length); ++index) {
            var mask = segment[index];

            if (mask == 0) { continue; }
            var block = (blockLow + ((ulong)index));
            var blockValue = (block * 30UL);

            for (var numeric = 0; (numeric < 8); ++numeric) {
                var channel = channels[numeric];
                var bit = ((layout == PrimeByteLayout.Numeric) ? numeric : channel);

                if ((mask & (1 << bit)) == 0) { continue; }
                var value = (blockValue + residues[numeric]);

                // Sieving every prime below 65537 already proves survivors below its square prime.
                if (testSurvivors && (value >= ProvenPresieveLimit) && !PrimeKernels.IsPrimeCandidateWord(value: value)) { continue; }
                if (onPrime is null) {
                    ++count;
                } else {
                    onPrime(value);
                }
            }
        }
        return count;
    }
    private static void MarkBases(ReadOnlySpan<uint> primes, Span<byte> segment, ulong blockLow, ulong low, ulong high, PrimeSieveStrategy strategy, PrimeByteLayout layout, bool usePreSieve) {
        foreach (var basePrime in primes) {
            if (basePrime <= (usePreSieve ? 163U : 5U)) { continue; }
            var prime = ((ulong)basePrime);
            var square = (prime * prime);

            if (square > high) { break; }
            var lower = Math.Max(val1: low, val2: square);
            // Division followed by a remainder correction avoids the usual overflowing low + prime - 1.
            var (quotient, remainder) = Math.DivRem(left: lower, right: prime);
            var multiplier = (quotient + ((remainder == 0UL) ? 0UL : 1UL));
            var maximumMultiplier = (high / prime);

            if (multiplier > maximumMultiplier) { continue; }
            _ = PrimeWheel30.TryChannel(channel: out var primeChannel, residue: ((byte)(prime % 30UL)));
            var clearMasks = ((layout == PrimeByteLayout.Numeric) ? NumericClearMasks : AlgebraicClearMasks).AsSpan(length: 8, start: (primeChannel * 8));

            if (strategy == PrimeSieveStrategy.EightStreams) {
                MarkStreams(blockLow: blockLow, clearMasks: clearMasks, maximumMultiplier: maximumMultiplier, multiplier: multiplier, prime: prime, segment: segment);
            } else {
                MarkWheelSteps(blockLow: blockLow, clearMasks: clearMasks, maximumMultiplier: maximumMultiplier, multiplier: multiplier, prime: prime, segment: segment);
            }
        }
    }
    private static void MarkStreams(Span<byte> segment, ulong blockLow, ulong prime, ulong multiplier, ulong maximumMultiplier, ReadOnlySpan<byte> clearMasks) {
        var residues = PrimeWheel30.NumericResidues;
        var remainder = (multiplier % 30UL);
        var length = ((ulong)segment.Length);
        ref var first = ref MemoryMarshal.GetReference(span: segment);
        ref var firstMask = ref MemoryMarshal.GetReference(span: clearMasks);

        for (var source = 0; (source < 8); ++source) {
            var delta = (((((ulong)residues[source]) + 30UL) - remainder) % 30UL);
            var firstMultiplier = (multiplier + delta);

            if (firstMultiplier > maximumMultiplier) { continue; }
            var firstBlock = ((firstMultiplier * prime) / 30UL);
            // MarkBases supplies an eight-entry mask row; the source loop proves this offset's bounds.
            var clearMask = Unsafe.Add(elementOffset: source, source: ref firstMask);
            // A multiplier stride of thirty is a byte-coordinate stride of exactly prime.
            for (var index = (firstBlock - blockLow); (index < length); index += prime) {
                // The loop guard proves the offset is inside this span and hence fits nint on either architecture.
                // The next offset is below Array.MaxLength + uint.MaxValue, so ulong addition cannot wrap.
                Unsafe.Add(elementOffset: ((nint)index), source: ref first) &= clearMask;
            }
        }
    }
    private static void MarkWheelSteps(Span<byte> segment, ulong blockLow, ulong prime, ulong multiplier, ulong maximumMultiplier, ReadOnlySpan<byte> clearMasks) {
        var residues = PrimeWheel30.NumericResidues;
        var gaps = WheelGaps;
        var remainder = (multiplier % 30UL);
        var source = 0;

        // Every remainder is at most 29, the final residue, so this search always finds a lane.
        while (residues[source] < remainder) { ++source; }
        multiplier += (((ulong)residues[source]) - remainder);
        if (multiplier > maximumMultiplier) { return; }

        var multiple = (multiplier * prime);
        var high = (maximumMultiplier * prime);
        ref var first = ref MemoryMarshal.GetReference(span: segment);
        ref var firstMask = ref MemoryMarshal.GetReference(span: clearMasks);
        ref var firstGap = ref MemoryMarshal.GetReference(span: gaps);

        while (true) {
            // The rounded first multiple is at least the segment's low endpoint; high is no greater than its
            // high endpoint. Every visited byte therefore lies inside segment. Source starts in 0..7 and stays
            // there under the masked increment, proving both eight-entry table offsets as well.
            var index = ((multiple / 30UL) - blockLow);

            Unsafe.Add(elementOffset: ((nint)index), source: ref first) &= Unsafe.Add(elementOffset: source, source: ref firstMask);
            var advance = (prime * Unsafe.Add(elementOffset: source, source: ref firstGap));

            if ((high - multiple) < advance) { break; }
            multiple += advance;
            source = (source + 1) & 7;
        }
    }
}
