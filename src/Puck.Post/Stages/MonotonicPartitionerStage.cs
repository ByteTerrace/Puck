using Puck.Maths;

namespace Puck.Post;

/// <summary>
/// Tier-A stage. Proves <see cref="MonotonicPartitioner"/>'s three invariants by exhaustive sweep of the whole
/// 65536-value × 1024-bucket-count domain: every routed bucket agrees with an independent jump-chain reference walk
/// (table-free, so a table-build defect cannot vouch for itself), each bucket count distributes ⌊65536/N⌋ or
/// ⌈65536/N⌉ values per bucket, raising the count from N to N + 1 moves values only into the new bucket, metrics
/// agree with a reference chain walk, the <see cref="Guid"/> overload routes through its documented
/// trailing-entropy hash, and the checked entry points reject an out-of-range bucket count. The routing map is a
/// client/server agreement — a wire peer computes the same route from the same value — so a silent mapping change
/// is a protocol break, not a tuning.
/// </summary>
internal sealed class MonotonicPartitionerStage : IPostStage {
    /// <inheritdoc/>
    public string Name => "monotonic-partitioner";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        if (Sweep() is { } sweepFailure) {
            return sweepFailure;
        }

        if (CheckMetrics() is { } metricsFailure) {
            return metricsFailure;
        }

        if (CheckGuidOverloads() is { } guidFailure) {
            return guidFailure;
        }

        if (!Throws(action: static () => _ = MonotonicPartitioner.GetBucketId(bucketCount: 0, value: ((ushort)0))) ||
            !Throws(action: static () => _ = MonotonicPartitioner.GetBucketId(bucketCount: (MonotonicPartitioner.MaxBucketCount + 1), value: ((ushort)0))) ||
            !Throws(action: static () => _ = MonotonicPartitioner.GetBucketId(bucketCount: 0, value: Guid.Empty)) ||
            !Throws(action: static () => _ = MonotonicPartitioner.GetMetrics(bucketCount: 0, value: ((ushort)0)))) {
            return PostStageOutcome.Fail(detail: "an out-of-range bucket count was not rejected");
        }

        return PostStageOutcome.Pass(detail: "deterministic, uniform, and monotonic across the whole 65536×1024 domain, against an independent reference walk");
    }

    private static PostStageOutcome? CheckGuidOverloads() {
        // The Guid route is defined as the ushort route of the documented trailing-entropy hash: a little-endian
        // read of bytes 12..15, widened bias-free into [1, 65534].
        Span<byte> guidBytes = stackalloc byte[16];

        for (var sample = 0; (sample < 64); ++sample) {
            for (var i = 0; (i < guidBytes.Length); ++i) {
                guidBytes[i] = ((byte)((sample * 37) + (i * 11) + 5));
            }

            var guid = new Guid(b: guidBytes);
            var entropy = ((uint)(guidBytes[12] | (guidBytes[13] << 8) | (guidBytes[14] << 16) | (guidBytes[15] << 24)));
            var expectedHash = ((ushort)(((65534UL * entropy) >> 32) + 1));

            foreach (var bucketCount in ((ReadOnlySpan<int>)[1, 64, 353, 1024,])) {
                if (MonotonicPartitioner.GetBucketId(bucketCount: bucketCount, value: guid) !=
                    MonotonicPartitioner.GetBucketId(bucketCount: bucketCount, value: expectedHash)) {
                    return PostStageOutcome.Fail(detail: $"Guid overload disagreed with the trailing-entropy hash route at sample {sample}, bucket count {bucketCount}");
                }
            }

            if (MonotonicPartitioner.GetMetrics(bucketCount: 353, value: guid) !=
                MonotonicPartitioner.GetMetrics(bucketCount: 353, value: expectedHash)) {
                return PostStageOutcome.Fail(detail: $"Guid metrics overload disagreed with the trailing-entropy hash route at sample {sample}");
            }
        }

        return null;
    }
    private static PostStageOutcome? CheckMetrics() {
        foreach (var bucketCount in ((ReadOnlySpan<int>)[1, 100, 1024,])) {
            for (var value = 0; (value < MonotonicPartitioner.MaxValueCount); ++value) {
                var metrics = MonotonicPartitioner.GetMetrics(bucketCount: bucketCount, value: ((ushort)value));
                var expectedRank = NormalizedRank(value: value);
                var jumpCount = 0;
                var migrationDistance = 0;
                var owner = 0;
                var remainingRank = expectedRank;

                while (0 != remainingRank) {
                    var nextJump = (((MonotonicPartitioner.MaxValueCount - 1 - owner) / remainingRank) + 1);

                    if (nextJump > MonotonicPartitioner.MaxBucketCount) {
                        break;
                    }

                    if ((0 == migrationDistance) && (nextJump > bucketCount)) {
                        migrationDistance = (nextJump - bucketCount);
                    }

                    Advance(jumpAtBucketCount: nextJump, owner: ref owner, remainingRank: ref remainingRank);
                    ++jumpCount;
                }

                if ((metrics.BucketCount != bucketCount) || (metrics.JumpCount != jumpCount) ||
                    (metrics.MigrationDistance != migrationDistance) || (metrics.Rank != expectedRank) ||
                    (metrics.Value != value) ||
                    (metrics.Velocity != ((0 == migrationDistance) ? 0.0f : (1.0f / migrationDistance)))) {
                    return PostStageOutcome.Fail(detail: $"metrics for value {value} at bucket count {bucketCount} disagree with the reference chain walk");
                }
            }
        }

        return null;
    }
    private static PostStageOutcome? Sweep() {
        const int MaxBucketCount = MonotonicPartitioner.MaxBucketCount;
        const int MaxValueCount = MonotonicPartitioner.MaxValueCount;

        // Reference state per value, advanced as the bucket count grows: a value migrates exactly when the count
        // reaches its chain's next jump, and it always lands in the youngest bucket — so the reference is monotone
        // by construction, and full agreement transfers monotonicity to the table path.
        var bucketPopulations = new int[MaxBucketCount];
        var nextJumps = new int[MaxValueCount];
        var owners = new int[MaxValueCount];
        var remainingRanks = new int[MaxValueCount];

        for (var value = 0; (value < MaxValueCount); ++value) {
            var rank = NormalizedRank(value: value);

            remainingRanks[value] = rank;
            nextJumps[value] = NextJump(owner: 0, remainingRank: rank);
        }

        bucketPopulations[0] = MaxValueCount;

        for (var bucketCount = 1; (bucketCount <= MaxBucketCount); ++bucketCount) {
            for (var value = 0; (value < MaxValueCount); ++value) {
                if (nextJumps[value] == bucketCount) {
                    var owner = owners[value];
                    var remainingRank = remainingRanks[value];

                    Advance(jumpAtBucketCount: bucketCount, owner: ref owner, remainingRank: ref remainingRank);
                    --bucketPopulations[owners[value]];
                    ++bucketPopulations[owner];
                    nextJumps[value] = NextJump(owner: owner, remainingRank: remainingRank);
                    owners[value] = owner;
                    remainingRanks[value] = remainingRank;
                }

                if (MonotonicPartitioner.GetBucketIdDangerous(bucketCount: bucketCount, value: ((ushort)value)) != owners[value]) {
                    return PostStageOutcome.Fail(detail: $"value {value} at bucket count {bucketCount} routed to {MonotonicPartitioner.GetBucketIdDangerous(bucketCount: bucketCount, value: ((ushort)value))}, reference walk says {owners[value]}");
                }
            }

            var floorShare = (MaxValueCount / bucketCount);
            var ceilingShare = ((MaxValueCount + bucketCount - 1) / bucketCount);

            for (var bucket = 0; (bucket < bucketCount); ++bucket) {
                var population = bucketPopulations[bucket];

                if ((population < floorShare) || (population > ceilingShare)) {
                    return PostStageOutcome.Fail(detail: $"bucket {bucket} of {bucketCount} owns {population} values; uniformity requires {floorShare} or {ceilingShare}");
                }
            }
        }

        return null;
    }

    private static void Advance(ref int owner, ref int remainingRank, int jumpAtBucketCount) {
        const int MaxValueCount = MonotonicPartitioner.MaxValueCount;

        var donorBucket = (jumpAtBucketCount - 1);
        var jumpQuotient = (MaxValueCount / jumpAtBucketCount);
        var jumpRemainder = (MaxValueCount % jumpAtBucketCount);
        var priorQuotient = (MaxValueCount / donorBucket);
        var priorRemainder = (MaxValueCount % donorBucket);
        var priorDonationTotal = (
            (owner * (priorQuotient - jumpQuotient)) +
            Math.Min(owner, priorRemainder) -
            Math.Min(owner, jumpRemainder)
        );
        var donationThreshold = (((MaxValueCount - 1 - owner) / jumpAtBucketCount) + 1);

        remainingRank = (priorDonationTotal + (remainingRank - donationThreshold));
        owner = donorBucket;
    }
    private static int NextJump(int owner, int remainingRank) {
        if (0 == remainingRank) {
            return 0;
        }

        var jumpAtBucketCount = (((MonotonicPartitioner.MaxValueCount - 1 - owner) / remainingRank) + 1);

        return ((jumpAtBucketCount > MonotonicPartitioner.MaxBucketCount) ? 0 : jumpAtBucketCount);
    }
    // The plainly written twin of the production GetNormalizedRank: permute by the odd multiplier, then trade the
    // natural maximum with 65535 so the top input value owns the top rank.
    private static int NormalizedRank(int value) {
        const int NaturalMaxRank = ((int)((65535U * 40503U) & 0xFFFFU));

        var rank = ((int)((((uint)value) * 40503U) & 0xFFFFU));

        return rank switch {
            NaturalMaxRank => 0xFFFF,
            0xFFFF => NaturalMaxRank,
            _ => rank,
        };
    }
    private static bool Throws(Action action) {
        try {
            action();

            return false;
        }
        catch (ArgumentOutOfRangeException) {
            return true;
        }
    }
}
