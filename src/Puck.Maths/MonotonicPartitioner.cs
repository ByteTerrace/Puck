using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// The deterministic routing metrics of one input value: where its rank sits in the migration order and how close
/// it is to its next migration.
/// </summary>
/// <param name="BucketCount">The bucket count that the metrics were calculated for.</param>
/// <param name="JumpCount">The total number of migrations that occur across the whole supported range (1-1024).</param>
/// <param name="MigrationDistance">The bucket-count distance to the value's next migration; 0 when no migration
/// remains within the supported range, 1 when the next increment migrates it.</param>
/// <param name="Rank">The normalized rank (0-65535) of the input; higher values generally represent greater
/// migration volatility.</param>
/// <param name="Value">The value that the metrics were calculated for.</param>
public readonly record struct MonotonicPartitionerMetrics(
    int BucketCount,
    int JumpCount,
    int MigrationDistance,
    int Rank,
    ushort Value
)
{
    /// <summary>Gets the instantaneous migration pressure; 1.0f indicates a jump on the next increment, while 0.0f
    /// signifies complete stability.</summary>
    public float Velocity =>
        ((0 == MigrationDistance) ? 0.0f : (1.0f / MigrationDistance));
}

/// <summary>
/// A static router that maps 65536 values (or a <see cref="Guid"/>, through its trailing entropy) onto between 1 and
/// 1024 buckets — jump-consistent-hash routing with the ownership chains compressed into checkpoint and tail-stream
/// tables, so the common lookup is one indexed checkpoint scan and a bit scan.
/// </summary>
/// <remarks>
/// <para>Three invariants hold over the whole domain (proven by the POST battery's exhaustive sweep):</para>
/// <list type="bullet">
///   <item><description><b>Deterministic</b> — the same (value, bucketCount) pair always yields the same bucket,
///   on every machine; routing decisions made on both sides of a wire agree bit-for-bit.</description></item>
///   <item><description><b>Monotonic</b> — raising the bucket count from N to N + 1 only moves values <em>into</em>
///   bucket N, never between existing buckets, so scaling out migrates the minimal set.</description></item>
///   <item><description><b>Uniform</b> — each bucket owns ⌊65536/N⌋ or ⌈65536/N⌉ values.</description></item>
/// </list>
/// </remarks>
public static class MonotonicPartitioner {
    /// <summary>The largest supported bucket count.</summary>
    public const int MaxBucketCount = 1024;
    /// <summary>The size of the routed value domain.</summary>
    public const int MaxValueCount = 65536;

    private const int BucketShift = 4;
    // Bucket counts at or below this limit resolve entirely from a checkpoint's 64-bit ownership bitmask.
    private const int FastPathBucketLimit = 64;
    private const int MaxTailStreamByteOffset = ushort.MaxValue;
    // Chains that own more than this many upper (>= 64) buckets are precompressed into the tail stream; shorter
    // chains re-walk from the checkpoint, which is cheaper than a decode.
    private const int MinUpperOwnersForTail = 16;
    // Odd, so (value * multiplier) mod 65536 permutes the domain; decorrelates sequential values from bucket ranks.
    private const uint PermutationMultiplier = 40503U;
    private const uint NaturalMaxRank = ((65535U * PermutationMultiplier) & 0xFFFFU);
    private const uint NaturalRankSwapMask = (NaturalMaxRank ^ 0xFFFFU);
    private const int RankBucketCount = (MaxValueCount / RanksPerBucket);
    private const int RanksPerBucket = 16;

    private static readonly OwnershipCheckpoint[] Checkpoints;
    private static readonly ushort[] CumulativeTailCountByBlock;
    // Entry n packs (65536 / n) << 32 | (65536 % n), so the jump chain walks without hardware division.
    private static readonly ulong[] DivModByBucketCount;
    private static readonly ushort[] FirstCheckpointByRankBucket;
    private static readonly int FirstTailBlock;
    private static readonly byte[] TailOwnerDeltaStream;
    private static readonly ulong[] TailPresenceBitsByBlock;
    private static readonly ushort[] TailStreamOffsetByIndex;

    // A run of ranks whose lower (< 64) bucket ownership is identical: the bitmask resolves any bucket count on the
    // fast path, while LastRank bounds the run and CheckpointRankOffset rebases a slow-path re-walk.
    [StructLayout(layoutKind: LayoutKind.Explicit, Size = 16)]
    private readonly struct OwnershipCheckpoint(
        ushort checkpointRankOffset,
        ushort lastRank,
        ulong lowerBucketBitmask
    )
    {
        [FieldOffset(offset: 10)]
        public readonly ushort CheckpointRankOffset = checkpointRankOffset;
        [FieldOffset(offset: 8)]
        public readonly ushort LastRank = lastRank;
        [FieldOffset(offset: 0)]
        public readonly ulong LowerBucketBitmask = lowerBucketBitmask;
    }

    static MonotonicPartitioner() {
        if (16 != Unsafe.SizeOf<OwnershipCheckpoint>()) {
            throw new InvalidOperationException(message: $"OwnershipCheckpoint must be 16 bytes; actual size is {Unsafe.SizeOf<OwnershipCheckpoint>()}.");
        }

        if (0U == (PermutationMultiplier & 1U)) {
            throw new InvalidOperationException(message: "PermutationMultiplier must be odd.");
        }

        if (RanksPerBucket != (1 << BucketShift)) {
            throw new InvalidOperationException(message: "RanksPerBucket must match BucketShift.");
        }

        BuildTables(
            bucketIndex: out FirstCheckpointByRankBucket,
            cumulativeTailCountByBlock: out CumulativeTailCountByBlock,
            divModByBucketCount: out DivModByBucketCount,
            firstTailBlock: out FirstTailBlock,
            lowerCheckpoints: out Checkpoints,
            tailOffsets: out TailStreamOffsetByIndex,
            tailPresenceBitsByBlock: out TailPresenceBitsByBlock,
            tailStream: out TailOwnerDeltaStream
        );
    }
    private static void AdvanceJumpChain(
        ulong[] divModByBucketCount,
        ref int currentBucket,
        ref int remainingRank,
        int jumpAtBucketCount
    ) {
        var donorBucket = (jumpAtBucketCount - 1);
        var priorDivMod = divModByBucketCount[donorBucket];
        var jumpDivMod = divModByBucketCount[jumpAtBucketCount];
        var priorQuotient = ((int)(priorDivMod >> 32));
        var priorRemainder = unchecked((int)priorDivMod);
        var jumpQuotient = ((int)(jumpDivMod >> 32));
        var jumpRemainder = unchecked((int)jumpDivMod);
        var priorDonationTotal = (
            (currentBucket * (priorQuotient - jumpQuotient)) +
            Math.Min(currentBucket, priorRemainder) -
            Math.Min(currentBucket, jumpRemainder)
        );
        var donationThreshold = (((MaxValueCount - 1 - currentBucket) / jumpAtBucketCount) + 1);

        remainingRank = (priorDonationTotal + (remainingRank - donationThreshold));
        currentBucket = donorBucket;
    }
    private static ushort[] BuildCumulativeTailCounts(ulong[] tailBits, int expectedTailCount) {
        var cumulativeCounts = new ushort[tailBits.Length];
        var runningTotal = 0;

        for (var i = 0; (i < cumulativeCounts.Length); ++i) {
            cumulativeCounts[i] = checked((ushort)runningTotal);
            runningTotal += BitOperations.PopCount(value: tailBits[i]);
        }

        return (runningTotal != expectedTailCount)
            ? throw new InvalidOperationException(message: $"Tail index mismatch. Expected {expectedTailCount}; got {runningTotal}.")
            : cumulativeCounts;
    }
    private static ushort[] BuildFirstCheckpointByRankBucket(OwnershipCheckpoint[] checkpoints) {
        var bucketIndex = new ushort[RankBucketCount];
        var checkpointIdx = 0;

        for (var rankBucket = 0; (rankBucket < bucketIndex.Length); ++rankBucket) {
            var bucketStartRank = (rankBucket * RanksPerBucket);

            while (checkpoints[checkpointIdx].LastRank < bucketStartRank) {
                ++checkpointIdx;
            }

            bucketIndex[rankBucket] = checked((ushort)checkpointIdx);
        }

        return bucketIndex;
    }
    private static void BuildTables(
        out ulong[] divModByBucketCount,
        out OwnershipCheckpoint[] lowerCheckpoints,
        out ushort[] bucketIndex,
        out int firstTailBlock,
        out ulong[] tailPresenceBitsByBlock,
        out ushort[] cumulativeTailCountByBlock,
        out ushort[] tailOffsets,
        out byte[] tailStream
    ) {
        divModByBucketCount = new ulong[(MaxBucketCount + 1)];

        for (var n = 1; (n <= MaxBucketCount); ++n) {
            var quotient = ((ulong)(MaxValueCount / n));
            var remainder = ((ulong)(MaxValueCount % n));

            divModByBucketCount[n] = ((quotient << 32) | remainder);
        }

        var checkpoints = new List<OwnershipCheckpoint>(capacity: 2048);
        var currentBitmask = 0UL;
        var currentCheckpointRankBase = 0;
        var currentCheckpointStartRank = 0;
        var hasCheckpoint = false;
        var tempTailOffsets = new List<ushort>(capacity: 512);
        var tempTailRanks = new List<ushort>(capacity: 512);
        var tempTailStream = new List<byte>(capacity: (16 * 1024));
        var tempUpperBuckets = new List<int>(capacity: 256);

        for (var rank = 0; (rank < MaxValueCount); ++rank) {
            ComputeOwnershipChain(
                checkpointBucket: out var checkpointBucket,
                checkpointRank: out var checkpointRank,
                divModByBucketCount: divModByBucketCount,
                initialRank: rank,
                lowerBucketBitmask: out var lowerBucketBitmask,
                upperBuckets: tempUpperBuckets
            );

            var bucketFromMask = BitOperations.Log2(value: lowerBucketBitmask);

            if (bucketFromMask != checkpointBucket) {
                throw new InvalidOperationException(message: $"Checkpoint bucket mismatch at rank {rank}.");
            }

            if (!hasCheckpoint || (lowerBucketBitmask != currentBitmask)) {
                if (hasCheckpoint) {
                    checkpoints.Add(item: new(
                        checkpointRankOffset: unchecked((ushort)(currentCheckpointRankBase - currentCheckpointStartRank)),
                        lastRank: checked((ushort)(rank - 1)),
                        lowerBucketBitmask: currentBitmask
                    ));
                }

                currentBitmask = lowerBucketBitmask;
                currentCheckpointRankBase = checkpointRank;
                currentCheckpointStartRank = rank;
                hasCheckpoint = true;
            }
            else {
                var expectedCheckpointRank = (currentCheckpointRankBase + (rank - currentCheckpointStartRank));

                if (checkpointRank != expectedCheckpointRank) {
                    throw new InvalidOperationException(message: $"Checkpoint residual is not linear at rank {rank}. Expected {expectedCheckpointRank}; got {checkpointRank}.");
                }
            }

            if (tempUpperBuckets.Count > MinUpperOwnersForTail) {
                tempTailRanks.Add(item: checked((ushort)rank));

                if (tempTailStream.Count > MaxTailStreamByteOffset) {
                    throw new InvalidOperationException(message: "Tail stream exceeded ushort offset capacity.");
                }

                tempTailOffsets.Add(item: ((ushort)tempTailStream.Count));
                EncodeTailEntry(
                    checkpointBucket: checkpointBucket,
                    destination: tempTailStream,
                    upperBuckets: tempUpperBuckets
                );
            }
        }

        if (!hasCheckpoint) {
            throw new InvalidOperationException(message: "No checkpoints were generated.");
        }

        checkpoints.Add(item: new(
            checkpointRankOffset: unchecked((ushort)(currentCheckpointRankBase - currentCheckpointStartRank)),
            lastRank: ushort.MaxValue,
            lowerBucketBitmask: currentBitmask
        ));

        lowerCheckpoints = [.. checkpoints];
        bucketIndex = BuildFirstCheckpointByRankBucket(checkpoints: lowerCheckpoints);

        var tailRanks = tempTailRanks;

        tailPresenceBitsByBlock = BuildTailPresenceBitmap(
            firstBlock: out firstTailBlock,
            tailRanks: tailRanks
        );
        cumulativeTailCountByBlock = BuildCumulativeTailCounts(
            expectedTailCount: tailRanks.Count,
            tailBits: tailPresenceBitsByBlock
        );
        tailOffsets = [.. tempTailOffsets];
        tailStream = [.. tempTailStream];
    }
    private static ulong[] BuildTailPresenceBitmap(List<ushort> tailRanks, out int firstBlock) {
        if (0 == tailRanks.Count) {
            firstBlock = 0;

            return [];
        }

        firstBlock = (tailRanks[0] >> 6);

        var lastBlock = (tailRanks[^1] >> 6);
        var window = new ulong[(lastBlock - firstBlock + 1)];

        foreach (var rank in tailRanks) {
            window[((rank >> 6) - firstBlock)] |= (1UL << (rank & 0x3F));
        }

        return window;
    }
    private static void ComputeOwnershipChain(
        ulong[] divModByBucketCount,
        int initialRank,
        List<int> upperBuckets,
        out ulong lowerBucketBitmask,
        out int checkpointBucket,
        out int checkpointRank
    ) {
        upperBuckets.Clear();

        var currentBucket = 0;
        var remainingRank = initialRank;

        lowerBucketBitmask = 1UL;
        checkpointBucket = 0;
        checkpointRank = remainingRank;

        while (0 != remainingRank) {
            var jumpAtBucketCount = (((MaxValueCount - 1 - currentBucket) / remainingRank) + 1);

            if (MaxBucketCount < jumpAtBucketCount) {
                break;
            }

            AdvanceJumpChain(
                currentBucket: ref currentBucket,
                divModByBucketCount: divModByBucketCount,
                jumpAtBucketCount: jumpAtBucketCount,
                remainingRank: ref remainingRank
            );

            if (FastPathBucketLimit > currentBucket) {
                lowerBucketBitmask |= (1UL << currentBucket);
                checkpointBucket = currentBucket;
                checkpointRank = remainingRank;
            }
            else {
                upperBuckets.Add(item: currentBucket);
            }
        }
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static int DecodeTailOwner(
        int tailEntryIndex,
        int startingBucket,
        int bucketCount
    ) {
        var currentBucket = startingBucket;
        var stream = TailOwnerDeltaStream;
        var streamPosition = ((int)TailStreamOffsetByIndex[tailEntryIndex]);

        while (true) {
            var firstByte = stream[streamPosition++];

            if (0 == firstByte) {
                return currentBucket;
            }

            var bucketDelta = (firstByte & 0x7F);

            if (0 != (firstByte & 0x80)) {
                bucketDelta |= ((stream[streamPosition++] & 0x7F) << 7);
            }

            var candidateBucket = (currentBucket + bucketDelta);

            if (candidateBucket >= bucketCount) {
                return currentBucket;
            }

            currentBucket = candidateBucket;
        }
    }
    private static void EncodePositiveVarint(List<byte> destination, int value) {
        if (0x3FFFU <= ((uint)(value - 1))) {
            throw new ArgumentOutOfRangeException(
                actualValue: value,
                message: "Value must fit in two varint bytes.",
                paramName: nameof(value)
            );
        }

        if (0x80 > value) {
            destination.Add(item: ((byte)value));
        }
        else {
            destination.Add(item: ((byte)((value & 0x7F) | 0x80)));
            destination.Add(item: ((byte)(value >> 7)));
        }
    }
    private static void EncodeTailEntry(
        List<byte> destination,
        int checkpointBucket,
        List<int> upperBuckets
    ) {
        var previousBucket = checkpointBucket;

        for (var i = 0; (i < upperBuckets.Count); ++i) {
            var bucket = upperBuckets[i];
            var bucketDelta = (bucket - previousBucket);

            if (0 >= bucketDelta) {
                throw new InvalidOperationException(message: "Upper buckets must be strictly increasing.");
            }

            EncodePositiveVarint(
                destination: destination,
                value: bucketDelta
            );
            previousBucket = bucket;
        }

        destination.Add(item: 0);
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.NoInlining)]
    private static int GetBucketIdSlowPath(
        int checkpointBucket,
        int checkpointRankOffset,
        int bucketCount,
        int rank
    ) {
        var tailBlockOffset = ((uint)((rank >> 6) - FirstTailBlock));
        var tailPresenceBitsByBlock = TailPresenceBitsByBlock;

        if (tailBlockOffset < ((uint)tailPresenceBitsByBlock.Length)) {
            var rankBitInBlock = (1UL << (rank & 0x3F));
            var tailPresenceBits = tailPresenceBitsByBlock[(int)tailBlockOffset];

            if (0 != (tailPresenceBits & rankBitInBlock)) {
                var tailCountBeforeBlock = CumulativeTailCountByBlock[(int)tailBlockOffset];
                var tailCountWithinBlock = BitOperations.PopCount(value: (tailPresenceBits & (rankBitInBlock - 1UL)));

                return DecodeTailOwner(
                    bucketCount: bucketCount,
                    startingBucket: checkpointBucket,
                    tailEntryIndex: (tailCountBeforeBlock + tailCountWithinBlock)
                );
            }
        }

        return ((int)WalkJumpChain(
            bucketCount: ((uint)bucketCount),
            currentBucket: ((uint)checkpointBucket),
            remainingRank: unchecked((ushort)(rank + checkpointRankOffset))
        ));
    }
    // The Guid's trailing 4 bytes, read explicitly little-endian, widened to a bias-free rank in [1, 65534] — the
    // declared endianness makes the route machine-independent by definition.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static ushort GetGuidHash(Guid value) {
        var entropy = BinaryPrimitives.ReadUInt32LittleEndian(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Guid>(in value))[12..]);

        return ((ushort)(((65534UL * entropy) >> 32) + 1));
    }
    // The permutation maps 65535 to NaturalMaxRank; the branchless swap trades that pair so the top input value owns
    // the top rank, keeping the rank domain's extremes addressable.
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static uint GetNormalizedRank(ushort value) {
        var rank = ((value * PermutationMultiplier) & ushort.MaxValue);
        var mask = (((int)(((rank ^ NaturalMaxRank) - 1U) | ((rank ^ ushort.MaxValue) - 1U))) >> 31);

        return (rank ^ (((uint)mask) & NaturalRankSwapMask));
    }
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    private static ulong KeepLowestBits(ulong value, int bitCount) =>
        Bmi2.X64.IsSupported
            ? Bmi2.X64.ZeroHighBits(
                index: ((uint)bitCount),
                value: value
            )
            : ((bitCount >= 64) ? value : (value & ((1UL << bitCount) - 1UL)));
    [DoesNotReturn]
    private static void ThrowBucketCountOutOfRange() =>
        throw new ArgumentOutOfRangeException(
            message: $"Bucket count must be in the inclusive range [1, {MaxBucketCount}].",
            paramName: "bucketCount"
        );
    private static uint WalkJumpChain(
        uint bucketCount,
        uint currentBucket,
        uint remainingRank
    ) {
        var divModByBucketCount = DivModByBucketCount;

        // A no-op at runtime (callers validate the range); gives the JIT the bound that proves the two indexed
        // loads below never need a bounds check.
        bucketCount = Math.Min(bucketCount, ((uint)MaxBucketCount));

        unchecked {
            while (0 != remainingRank) {
                var jumpAtBucketCount = (((MaxValueCount - 1U - currentBucket) / remainingRank) + 1U);

                if (jumpAtBucketCount > bucketCount) {
                    break;
                }

                var donorBucket = (jumpAtBucketCount - 1U);
                var priorDivMod = divModByBucketCount[(int)donorBucket];
                var jumpDivMod = divModByBucketCount[(int)jumpAtBucketCount];
                var priorQuotient = ((uint)(priorDivMod >> 32));
                var priorRemainder = ((uint)priorDivMod);
                var jumpQuotient = ((uint)(jumpDivMod >> 32));
                var jumpRemainder = ((uint)jumpDivMod);
                var diffPrior = ((int)(currentBucket - priorRemainder));
                var minPrior = (priorRemainder + ((currentBucket - priorRemainder) & ((uint)(diffPrior >> 31))));
                var diffJump = ((int)(currentBucket - jumpRemainder));
                var minJump = (jumpRemainder + ((currentBucket - jumpRemainder) & ((uint)(diffJump >> 31))));
                var priorDonationTotal = ((currentBucket * (priorQuotient - jumpQuotient)) + minPrior - minJump);
                var donationThreshold = (jumpQuotient + ((uint)(diffJump >> 31) & 1U));

                remainingRank = (priorDonationTotal + (remainingRank - donationThreshold));
                currentBucket = donorBucket;
            }
        }

        return currentBucket;
    }

    /// <summary>
    /// Maps an input value to a bucket id without any safety checks.
    /// </summary>
    /// <param name="value">The value to route.</param>
    /// <param name="bucketCount">The bucket count; must be between 1 and 1024 inclusive, or the result is meaningless.</param>
    /// <returns>The zero-based bucket id that owns <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static int GetBucketIdDangerous(ushort value, int bucketCount) {
        var rank = GetNormalizedRank(value: value);
        var checkpoints = Checkpoints;
        var checkpointIndex = ((int)FirstCheckpointByRankBucket[(int)(rank >> BucketShift)]);

        while (checkpoints[checkpointIndex].LastRank < rank) {
            ++checkpointIndex;
        }

        ref readonly var checkpoint = ref checkpoints[checkpointIndex];

        return (FastPathBucketLimit >= bucketCount)
            ? BitOperations.Log2(value: KeepLowestBits(
                bitCount: bucketCount,
                value: checkpoint.LowerBucketBitmask
            ))
            : GetBucketIdSlowPath(
                bucketCount: bucketCount,
                checkpointBucket: BitOperations.Log2(value: checkpoint.LowerBucketBitmask),
                checkpointRankOffset: checkpoint.CheckpointRankOffset,
                rank: ((int)rank)
            );
    }
    /// <summary>
    /// Maps an input value to a bucket id without any safety checks.
    /// </summary>
    /// <param name="value">The value to route.</param>
    /// <param name="bucketCount">The bucket count; must be between 1 and 1024 inclusive, or the result is meaningless.</param>
    /// <returns>The zero-based bucket id that owns <paramref name="value"/>.</returns>
    [MethodImpl(methodImplOptions: MethodImplOptions.AggressiveInlining)]
    public static int GetBucketIdDangerous(Guid value, int bucketCount) =>
        GetBucketIdDangerous(
            bucketCount: bucketCount,
            value: GetGuidHash(value: value)
        );
    /// <summary>
    /// Maps an input value to a bucket id.
    /// </summary>
    /// <param name="value">The value to route.</param>
    /// <param name="bucketCount">The bucket count; must be between 1 and 1024 inclusive.</param>
    /// <returns>The zero-based bucket id that owns <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bucketCount"/> is outside [1, 1024].</exception>
    public static int GetBucketId(ushort value, int bucketCount) {
        if (MaxBucketCount <= ((uint)(bucketCount - 1))) {
            ThrowBucketCountOutOfRange();
        }

        return GetBucketIdDangerous(
            bucketCount: bucketCount,
            value: value
        );
    }
    /// <summary>
    /// Maps an input value to a bucket id.
    /// </summary>
    /// <param name="value">The value to route.</param>
    /// <param name="bucketCount">The bucket count; must be between 1 and 1024 inclusive.</param>
    /// <returns>The zero-based bucket id that owns <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bucketCount"/> is outside [1, 1024].</exception>
    public static int GetBucketId(Guid value, int bucketCount) {
        if (MaxBucketCount <= ((uint)(bucketCount - 1))) {
            ThrowBucketCountOutOfRange();
        }

        return GetBucketIdDangerous(
            bucketCount: bucketCount,
            value: value
        );
    }
    /// <summary>
    /// Calculates the deterministic routing metrics of an input value.
    /// </summary>
    /// <param name="value">The value to route.</param>
    /// <param name="bucketCount">The bucket count; must be between 1 and 1024 inclusive.</param>
    /// <returns>The value's routing metrics at <paramref name="bucketCount"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bucketCount"/> is outside [1, 1024].</exception>
    public static MonotonicPartitionerMetrics GetMetrics(ushort value, int bucketCount) {
        if (MaxBucketCount <= ((uint)(bucketCount - 1))) {
            ThrowBucketCountOutOfRange();
        }

        var currentBucket = 0;
        var divModTable = DivModByBucketCount;
        var jumpCount = 0;
        var migrationDistance = 0;
        var rank = GetNormalizedRank(value: value);
        var remainingRank = ((int)rank);

        while (0 != remainingRank) {
            var nextJump = (((MaxValueCount - 1 - currentBucket) / remainingRank) + 1);

            if (nextJump > MaxBucketCount) {
                break;
            }

            if ((0 == migrationDistance) && (nextJump > bucketCount)) {
                migrationDistance = (nextJump - bucketCount);
            }

            AdvanceJumpChain(
                currentBucket: ref currentBucket,
                divModByBucketCount: divModTable,
                jumpAtBucketCount: nextJump,
                remainingRank: ref remainingRank
            );

            ++jumpCount;
        }

        return new(
            BucketCount: bucketCount,
            JumpCount: jumpCount,
            MigrationDistance: migrationDistance,
            Rank: ((int)rank),
            Value: value
        );
    }
    /// <summary>
    /// Calculates the deterministic routing metrics of an input value.
    /// </summary>
    /// <param name="value">The value to route.</param>
    /// <param name="bucketCount">The bucket count; must be between 1 and 1024 inclusive.</param>
    /// <returns>The value's routing metrics at <paramref name="bucketCount"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bucketCount"/> is outside [1, 1024].</exception>
    public static MonotonicPartitionerMetrics GetMetrics(Guid value, int bucketCount) =>
        GetMetrics(
            bucketCount: bucketCount,
            value: GetGuidHash(value: value)
        );
}
