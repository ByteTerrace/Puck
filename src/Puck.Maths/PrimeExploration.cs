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
    /// <summary>Uses complete sieving through the uint domain and bounded presieving above it.</summary>
    Automatic,
    /// <summary>Generates and retains every base prime through the interval upper bound's square root.</summary>
    Eratosthenes,
    /// <summary>Sieves with primes through 65,535 and decides surviving candidates exactly.</summary>
    Presieve,
}
/// <summary>Explores primes throughout the unsigned sixty-four-bit domain using thirty-wheel coordinates.</summary>
public static partial class PrimeExploration {
    private const int BaseWindowBits = 65536;
    private const int BaseChunkLength = 16384;

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
    /// <remarks>
    /// <para>Each segment uses one byte per thirty integers. Two, three, and five are reported separately; partial
    /// first and final bytes are masked, and one is never reported. Scalar marking starts at each prime's square;
    /// periodic filtering restores the small primes themselves before applying the endpoint masks.</para>
    /// <para>Automatic mode uses the shared base-prime table through 65,535 for intervals ending within the uint
    /// domain. Above that domain it uses the same bounded table and exact candidate primality decisions. Presieve
    /// mode always applies those decisions. Neither mode stores a bitmap for the full interval.</para>
    /// <para>Eratosthenes mode generates all required uint base primes with the existing odd-window marker. Those
    /// primes are retained in chunks: at the maximum ulong upper bound, the 203,280,220 odd base primes require
    /// 813,120,880 payload bytes, about 775.45 MiB, plus chunk-array overhead, the shared small table, and the segment.
    /// Base generation therefore has a cost determined by the upper bound, even for a narrow high interval.</para>
    /// <para>Callbacks may stop enumeration by throwing; the segment and base-generation bitmap are returned to
    /// their pools even when a callback throws, as are packet-state buffers. Packet strategies use eight-byte
    /// state payloads only for base primes through 65,535; higher bases retain eight-stream marking. Independent
    /// calls share immutable small-prime and wheel-mask tables.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onPrime"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentBytes"/> is outside
    /// <c>[1, Array.MaxLength]</c>, or <paramref name="strategy"/>, <paramref name="layout"/>, or
    /// <paramref name="mode"/> is not a defined value.</exception>
    public static void Enumerate(
        ulong low,
        ulong high,
        Action<ulong> onPrime,
        int segmentBytes = 32768,
        PrimeSieveStrategy strategy = PrimeSieveStrategy.BucketPackets,
        PrimeByteLayout layout = PrimeByteLayout.Numeric,
        PrimeSieveMode mode = PrimeSieveMode.Automatic,
        bool usePreSieve = true
    ) {
        ArgumentNullException.ThrowIfNull(onPrime);
        _ = Explore(high: high, layout: layout, low: low, mode: mode, onPrime: onPrime, segmentBytes: segmentBytes, strategy: strategy, usePreSieve: usePreSieve);
    }
    /// <summary>Counts primes in a closed interval without delivering individual values.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound; a value below <paramref name="low"/> gives zero.</param>
    /// <param name="segmentBytes">The positive number of thirty-integer blocks per segment; BucketPackets treats it as an adaptive maximum.</param>
    /// <param name="strategy">The wheel marking strategy.</param>
    /// <param name="layout">The order of residue bits within each byte.</param>
    /// <param name="mode">The complete-sieve or bounded-presieve policy.</param>
    /// <param name="usePreSieve">Uses periodic small-prime patterns through 163 instead of individual marks for those primes.</param>
    /// <returns>The number of primes in the interval.</returns>
    /// <remarks>Shares enumeration's marking, endpoint masks, and base-prime policy. Complete sieving counts
    /// surviving bits with word population counts; bounded presieving still decides every survivor exactly.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentBytes"/> is outside
    /// <c>[1, Array.MaxLength]</c>, or an option is not a defined value.</exception>
    public static ulong Count(
        ulong low,
        ulong high,
        int segmentBytes = 32768,
        PrimeSieveStrategy strategy = PrimeSieveStrategy.BucketPackets,
        PrimeByteLayout layout = PrimeByteLayout.Numeric,
        PrimeSieveMode mode = PrimeSieveMode.Automatic,
        bool usePreSieve = true
    ) => Explore(high: high, layout: layout, low: low, mode: mode, onPrime: null, segmentBytes: segmentBytes, strategy: strategy, usePreSieve: usePreSieve);

    private static unsafe ulong Explore(ulong low, ulong high, Action<ulong>? onPrime, int segmentBytes,
        PrimeSieveStrategy strategy, PrimeByteLayout layout, PrimeSieveMode mode, bool usePreSieve) {
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
        if (high < low) { return 0; }

        var count = 0UL;

        if ((low <= 2UL) && (high >= 2UL)) { ++count; onPrime?.Invoke(2UL); }
        if ((low <= 3UL) && (high >= 3UL)) { ++count; onPrime?.Invoke(3UL); }
        if ((low <= 5UL) && (high >= 5UL)) { ++count; onPrime?.Invoke(5UL); }
        if (high < 7UL) { return count; }

        low = Math.Max(val1: low, val2: 7UL);
        var testSurvivors = ((mode == PrimeSieveMode.Presieve) ||
            ((mode == PrimeSieveMode.Automatic) && (high > uint.MaxValue)));
        var baseLimit = high.SquareRoot();

        if (strategy == PrimeSieveStrategy.BucketPackets) {
            var cacheBytes = Math.Min(val1: segmentBytes, val2: 32768);
            var adaptiveBytes = Math.Clamp(max: ((ulong)segmentBytes), min: ((ulong)cacheBytes), value: (2UL * baseLimit));

            segmentBytes = ((int)(adaptiveBytes - (adaptiveBytes % ((ulong)cacheBytes))));
        }
        var upperBases = ((!testSurvivors && (baseLimit > 65535UL))
            ? CreateUpperBasePrimes(limit: ((uint)baseLimit))
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
                    if (upperBases is not null) {
                        foreach (var chunk in upperBases) {
                            if ((((ulong)chunk[0]) * chunk[0]) > segmentHigh) { break; }
                            MarkBases(blockLow: blockLow, high: segmentHigh, layout: layout, low: segmentLow, primes: chunk, segment: segment,
                                strategy: ((stateStorage is null) ? strategy : PrimeSieveStrategy.EightStreams), usePreSieve: usePreSieve);
                        }
                    }

                    count += (((onPrime is null) && !testSurvivors)
                        ? CountBits(segment: segment)
                        : ReportSegment(blockLow: blockLow, layout: layout, onPrime: onPrime, segment: segment, testSurvivors: testSurvivors));
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
                if (testSurvivors) {
                    // Endpoint masking guarantees that the coordinate is inside the ulong domain.
                    _ = CandidateAddress.TryFromCoordinates(address: out var candidate, block: block, channel: channel);
                    if (!IsPrimeCandidate(candidate: candidate)) { continue; }
                }
                if (onPrime is null) {
                    ++count;
                } else {
                    onPrime((blockValue + residues[numeric]));
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

        while ((source < 8) && (residues[source] < remainder)) { ++source; }
        if (source == 8) {
            multiplier += (31UL - remainder);
            source = 0;
        } else {
            multiplier += (((ulong)residues[source]) - remainder);
        }
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
    private static List<uint[]> CreateUpperBasePrimes(uint limit) {
        var chunks = new List<uint[]>();
        var chunk = new uint[BaseChunkLength];
        var count = 0;
        var bitmap = ArrayPool<ulong>.Shared.Rent(minimumLength: (BaseWindowBits / 64));
        var windowLow = 65537UL;

        try {
            while (windowLow <= limit) {
                var bits = Math.Min(val1: ((ulong)BaseWindowBits), val2: (((((ulong)limit) - windowLow) / 2UL) + 1UL));

                PrimeKernels.MarkWindow(basePrimes: PrimeKernels.BasePrimes, bitmap: bitmap, bits: bits, low: windowLow);
                var words = ((int)((bits + 63UL) >> 6));

                for (var word = 0; (word < words); ++word) {
                    var candidates = ~bitmap[word];

                    if ((word == (words - 1)) && ((bits & 63UL) != 0UL)) {
                        candidates &= ((1UL << ((int)(bits & 63UL))) - 1UL);
                    }
                    while (candidates != 0UL) {
                        var bit = ((((ulong)word) * 64UL) + ((ulong)BitOperations.TrailingZeroCount(value: candidates)));

                        chunk[count++] = ((uint)(windowLow + (bit * 2UL)));
                        candidates &= (candidates - 1UL);
                        if (count == BaseChunkLength) {
                            chunks.Add(item: chunk);
                            chunk = new uint[BaseChunkLength];
                            count = 0;
                        }
                    }
                }
                windowLow += (bits * 2UL);
            }
        } finally {
            ArrayPool<ulong>.Shared.Return(bitmap);
        }
        if (count != 0) {
            Array.Resize(array: ref chunk, newSize: count);
            chunks.Add(item: chunk);
        }
        return chunks;
    }
}
