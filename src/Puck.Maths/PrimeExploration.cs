using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

/// <summary>Explores primes throughout the unsigned sixty-four-bit domain using thirty-wheel coordinates.</summary>
public static partial class PrimeExploration {
    internal static readonly byte[] ClearMasks = CreateClearMasks();

    // Row primeChannel clears, at each ascending multiplier phase, the numeric bit of the product's residue.
    private static byte[] CreateClearMasks() {
        var masks = new byte[(PrimeWheel30.ChannelCount * PrimeWheel30.ChannelCount)];

        for (var primeChannel = 0; (primeChannel < PrimeWheel30.ChannelCount); ++primeChannel) {
            var residue = PrimeWheel30.Residues[primeChannel];

            for (var source = 0; (source < PrimeWheel30.ChannelCount); ++source) {
                masks[((primeChannel * PrimeWheel30.ChannelCount) + source)] = ((byte)~(1 << PrimeWheel30.TargetBit(phase: source, residue: residue)));
            }
        }
        return masks;
    }

    /// <summary>Reports every prime in a closed interval in ascending numeric order.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound; a value below <paramref name="low"/> gives an empty interval.</param>
    /// <param name="onPrime">The callback invoked once for each prime.</param>
    /// <param name="cancellationToken">Cancels before starting, between segments, during upper-base generation and before returning.</param>
    /// <remarks>
    /// <para>Each segment stores one byte per thirty integers, one bit per unit residue in ascending order. Two, three
    /// and five are reported separately; partial first and final bytes are masked, and one is never reported. Periodic
    /// patterns remove the multiples of 7 through 163 and restore those primes themselves; each larger base prime marks
    /// its multiples from its square.</para>
    /// <para>The interval selects the policy. An interval ending below 65,537², the square of the first prime past the
    /// shared base-prime table, is sieved completely with that table. Above it, an interval narrower than one
    /// sixty-fourth of the square root of its upper bound sieves with the table and decides each surviving candidate
    /// exactly. A wider interval sieves completely with every base prime through its square root: carrying upper-prime
    /// cursors in sparse buckets while a conservative workspace bound fits 128 MiB, and otherwise streaming every upper
    /// base prime through each segment of 16 MiB less one 32-KiB chunk, with no per-prime state outliving a segment.
    /// No policy stores a full-interval bitmap, and every policy reports the same primes; the crossovers are cost
    /// heuristics.</para>
    /// <para>Callbacks may stop enumeration by throwing; pooled bitmaps and state buffers are returned and native
    /// state slabs freed even then. Independent calls share immutable small-prime and wheel-mask tables.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onPrime"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static void Enumerate(ulong low, ulong high, Action<ulong> onPrime, CancellationToken cancellationToken = default) =>
        Enumerate(cancellationToken: cancellationToken, high: high, low: low, mode: PrimeSieveMode.Automatic, onPrime: onPrime, segmentBytes: CacheSegmentBytes);
    /// <summary>Counts primes in a closed interval without delivering individual values.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound; a value below <paramref name="low"/> gives zero.</param>
    /// <param name="cancellationToken">Cancels before starting, between segments, during upper-base generation and before returning.</param>
    /// <returns>The number of primes in the interval.</returns>
    /// <remarks>Shares enumeration's marking, endpoint masks and policy. Complete sieving counts surviving bits with
    /// word population counts; narrow intervals decide only the survivors their base primes have not already proven.</remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static ulong Count(ulong low, ulong high, CancellationToken cancellationToken = default) =>
        Count(cancellationToken: cancellationToken, high: high, low: low, mode: PrimeSieveMode.Automatic, segmentBytes: CacheSegmentBytes);

    /// <summary>Enumerates under an explicit policy and requested segment size.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound.</param>
    /// <param name="onPrime">The callback invoked once for each prime.</param>
    /// <param name="segmentBytes">The requested wheel bytes per segment, an adaptive maximum; see <see cref="ResolveSegmentBytes(ulong, ulong, int, PrimeSieveMode)"/>.</param>
    /// <param name="mode">The policy; <see cref="PrimeSieveMode.Automatic"/> is the public one.</param>
    /// <param name="cancellationToken">Cancels before starting, between segments, during upper-base generation and before returning.</param>
    /// <exception cref="ArgumentNullException"><paramref name="onPrime"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentBytes"/> is outside <c>[1, Array.MaxLength]</c>, or
    /// <paramref name="mode"/> is undefined.</exception>
    internal static void Enumerate(ulong low, ulong high, Action<ulong> onPrime, int segmentBytes, PrimeSieveMode mode, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(onPrime);
        _ = Explore(cancellationToken: cancellationToken, high: high, low: low, mode: mode, onPrime: onPrime, segmentBytes: segmentBytes);
    }
    /// <summary>Counts under an explicit policy and requested segment size.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound.</param>
    /// <param name="segmentBytes">The requested wheel bytes per segment, an adaptive maximum; see <see cref="ResolveSegmentBytes(ulong, ulong, int, PrimeSieveMode)"/>.</param>
    /// <param name="mode">The policy; <see cref="PrimeSieveMode.Automatic"/> is the public one.</param>
    /// <param name="cancellationToken">Cancels before starting, between segments, during upper-base generation and before returning.</param>
    /// <returns>The number of primes in the interval.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentBytes"/> is outside <c>[1, Array.MaxLength]</c>, or
    /// <paramref name="mode"/> is undefined.</exception>
    internal static ulong Count(ulong low, ulong high, int segmentBytes, PrimeSieveMode mode, CancellationToken cancellationToken = default) =>
        Explore(cancellationToken: cancellationToken, high: high, low: low, mode: mode, onPrime: null, segmentBytes: segmentBytes);
    internal static ulong CountWithWork(ulong low, ulong high, CancellationToken cancellationToken, PrimeBitmapWork? work) =>
        Explore(cancellationToken: cancellationToken, high: high, low: low, mode: PrimeSieveMode.Automatic, onPrime: null,
            segmentBytes: CacheSegmentBytes, work: work);

    // A consumer sees endpoint-masked segments and may stop without exceptions. Internal callers handle the three
    // exceptional primes before entering this bitmap-only path.
    private delegate bool SegmentConsumer(ReadOnlySpan<byte> segment, ulong blockLow, ulong segmentHigh, bool testSurvivors);

    private static unsafe ulong Explore(ulong low, ulong high, Action<ulong>? onPrime, int segmentBytes, PrimeSieveMode mode,
        CancellationToken cancellationToken, SegmentConsumer? onSegment = null, PrimeBitmapWork? work = null) {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(segmentBytes, Array.MaxLength);
        if (((uint)mode) > ((uint)PrimeSieveMode.Windowed)) {
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
        var resolvedMode = ResolveMode(high: high, low: low, mode: mode);
        var testSurvivors = (resolvedMode == PrimeSieveMode.Presieve);
        var baseLimit = high.SquareRoot();

        segmentBytes = ResolveSegmentBytes(high: high, low: low, mode: mode, segmentBytes: segmentBytes);
        // Every base prime below 65537 lies in the shared table, so upper bases exist only from that prime's square.
        var hasUpperBases = (baseLimit >= FirstUpperPrime);
        using var wideSieve = ((hasUpperBases && (resolvedMode == PrimeSieveMode.Eratosthenes))
            ? new WideSieve(cancellationToken: cancellationToken, high: high, limit: ((uint)baseLimit), low: low)
            : null);
        using var windowedMarker = ((hasUpperBases && (resolvedMode == PrimeSieveMode.Windowed))
            ? new WindowedUpperMarker(cancellationToken: cancellationToken)
            : null);
        var blockLow = (low / PrimeWheel30.Modulus);
        var lastBlock = (high / PrimeWheel30.Modulus);
        var requestedBytes = ((int)Math.Min(val1: ((ulong)segmentBytes), val2: ((lastBlock - blockLow) + 1UL)));
        var alignmentPadding = ((requestedBytes <= (Array.MaxLength - 63)) ? 63 : 0);
        var storage = ArrayPool<byte>.Shared.Rent(minimumLength: (requestedBytes + alignmentPadding));
        PrimeMarkState[]? stateStorage = null;
        PacketBuckets? buckets = null;
        var stateCount = 0;
        // Dormant packet cursors stay relative to the interval's first byte until their squares activate them.
        var stateOrigin = blockLow;
        Span<int> groupStarts = stackalloc int[(PrimeWheel30.ChannelCount + 1)];
        Span<int> groupActive = stackalloc int[PrimeWheel30.ChannelCount];

        groupActive.Clear();

        fixed (byte* storagePointer = storage) {
            var storageOffset = ((alignmentPadding == 0) ? 0 : ((int)((64 - (((nuint)storagePointer) & 63)) & 63)));

            try {
                stateStorage = ArrayPool<PrimeMarkState>.Shared.Rent(minimumLength: PrimeKernels.BasePrimes.Length);
                stateCount = InitializePacketStates(blockLow: blockLow, high: high, low: low, states: stateStorage);
                GroupPacketStates(stateStorage.AsSpan(length: stateCount, start: 0), groupStarts);
                buckets = new PacketBuckets(stateStorage.AsSpan(length: stateCount, start: 0), groupStarts, (Math.Min(val1: segmentBytes, val2: CacheSegmentBytes) / 8));
                while (blockLow <= lastBlock) {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = ((int)Math.Min(val1: ((ulong)segmentBytes), val2: ((lastBlock - blockLow) + 1UL)));
                    var blockHigh = ((blockLow + ((ulong)length)) - 1UL);
                    var segmentLow = Math.Max(val1: low, val2: (blockLow * PrimeWheel30.Modulus));
                    // The last ulong block has residues beyond ulong.MaxValue, so form its end in the wider carrier.
                    var wideEnd = ((((UInt128)blockHigh) * ((ulong)PrimeWheel30.Modulus)) + ((ulong)(PrimeWheel30.Modulus - 1)));
                    var segmentHigh = ((wideEnd > high) ? high : (ulong)wideEnd);
                    var segment = storage.AsSpan(length: length, start: storageOffset);

                    FilterSmallPrimes(blockLow: blockLow, segment: segment);
                    MaskEndpoint(block: blockLow, high: high, index: 0, low: low, segment: segment);
                    MaskEndpoint(block: blockHigh, high: high, index: (length - 1), low: low, segment: segment);
                    MarkBucketSegment(segment, blockLow, segmentHigh, stateStorage.AsSpan(length: stateCount, start: 0), stateOrigin, groupStarts, groupActive, buckets);
                    wideSieve?.Mark(blockLow: blockLow, high: segmentHigh, segment: segment);
                    if (windowedMarker is not null) {
                        windowedMarker.Mark(blockLow: blockLow, high: segmentHigh, low: segmentLow, segment: segment);
                        work?.AddWindowedSegment();
                    }
                    work?.AddSegment(bytes: length);

                    var decideSegment = (testSurvivors && (segmentHigh >= ProvenPresieveLimit));

                    if (onSegment is not null) {
                        if (!onSegment(segment, blockLow, segmentHigh, decideSegment)) { break; }
                    } else {
                        count += (((onPrime is null) && !decideSegment)
                            ? CountBits(segment: segment)
                            : ReportSegment(blockLow: blockLow, onPrime: onPrime, segment: segment, testSurvivors: decideSegment));
                    }
                    if (blockHigh == lastBlock) { break; }
                    blockLow = (blockHigh + 1UL);
                }
            } finally {
                buckets?.Dispose();
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
    private static void MaskEndpoint(Span<byte> segment, int index, ulong block, ulong low, ulong high) {
        var residues = PrimeWheel30.NumericResidues;
        var blockValue = (((UInt128)block) * ((ulong)PrimeWheel30.Modulus));

        for (var bit = 0; (bit < PrimeWheel30.ChannelCount); ++bit) {
            var value = (blockValue + residues[bit]);

            if ((value < low) || (value > high)) {
                segment[index] &= ((byte)~(1 << bit));
            }
        }
    }
    private static ulong ReportSegment(ReadOnlySpan<byte> segment, ulong blockLow, bool testSurvivors, Action<ulong>? onPrime) {
        var residues = PrimeWheel30.NumericResidues;
        var count = 0UL;

        for (var index = 0; (index < segment.Length); ++index) {
            var mask = segment[index];

            if (mask == 0) { continue; }
            var block = (blockLow + ((ulong)index));
            var blockValue = (block * PrimeWheel30.Modulus);

            for (var bit = 0; (bit < PrimeWheel30.ChannelCount); ++bit) {
                if ((mask & (1 << bit)) == 0) { continue; }
                var value = (blockValue + residues[bit]);

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
    // The stateless marker: one division finds each prime's first multiple in the segment, and the wheel steps mark
    // the rest. The windowed sieve streams every upper base prime through it once per segment.
    private static void MarkBases(ReadOnlySpan<uint> primes, Span<byte> segment, ulong blockLow, ulong low, ulong high) {
        foreach (var basePrime in primes) {
            if (basePrime <= PreSievePrimeLimit) { continue; }
            var prime = ((ulong)basePrime);
            var square = (prime * prime);

            if (square > high) { break; }
            var lower = Math.Max(val1: low, val2: square);
            // Division followed by a remainder correction avoids the usual overflowing low + prime - 1.
            var (quotient, remainder) = Math.DivRem(left: lower, right: prime);
            var multiplier = (quotient + ((remainder == 0UL) ? 0UL : 1UL));

            // A wide product rejects a prime with no multiple in the segment before the second division. Windowed
            // segments stream every upper base prime, and most primes above the segment width have no multiple in it.
            if (Math.BigMul(a: multiplier, b: prime) > high) { continue; }
            var maximumMultiplier = (high / prime);

            _ = PrimeWheel30.TryChannel(channel: out var primeChannel, residue: ((byte)(prime % PrimeWheel30.Modulus)));
            var clearMasks = ClearMasks.AsSpan(length: PrimeWheel30.ChannelCount, start: (primeChannel * PrimeWheel30.ChannelCount));

            MarkWheelSteps(blockLow: blockLow, clearMasks: clearMasks, maximumMultiplier: maximumMultiplier, multiplier: multiplier, prime: prime, segment: segment);
        }
    }
    private static void MarkWheelSteps(Span<byte> segment, ulong blockLow, ulong prime, ulong multiplier, ulong maximumMultiplier, ReadOnlySpan<byte> clearMasks) {
        var residues = PrimeWheel30.NumericResidues;
        var gaps = PrimeWheel30.Gaps;
        var remainder = (multiplier % PrimeWheel30.Modulus);
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
            var index = ((multiple / PrimeWheel30.Modulus) - blockLow);

            Unsafe.Add(elementOffset: ((nint)index), source: ref first) &= Unsafe.Add(elementOffset: source, source: ref firstMask);
            var advance = (prime * Unsafe.Add(elementOffset: source, source: ref firstGap));

            if ((high - multiple) < advance) { break; }
            multiple += advance;
            source = (source + 1) & (PrimeWheel30.ChannelCount - 1);
        }
    }
}
