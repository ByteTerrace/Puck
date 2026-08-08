using Xunit;

namespace Puck.Maths.Tests;

/// <summary>
/// The monotonic partitioner's exhaustive proof over the whole 65536-value × 1024-bucket-count domain, using a reference walk that is transcribed here independently of the production
/// checkpoint/tail-stream tables (<c>Checkpoints</c>, <c>TailOwnerDeltaStream</c>, <c>CumulativeTailCountByBlock</c>,
/// <c>DivModByBucketCount</c>) so a table-build defect in <see cref="MonotonicPartitioner"/> cannot vouch for itself.
/// The declarations in <see cref="LawRegistry"/> invoke these methods as laws, so every assertion participates in
/// both the ordinary test gate and the mechanically generated public-member coverage ledger.
/// </summary>
internal static class MonotonicPartitionerClaims {
    /// <summary>Full-domain proof of the three invariants <see cref="MonotonicPartitioner"/>'s own remarks name:
    /// deterministic routing and monotonicity (via agreement with a table-free reference chain-walk that is monotone
    /// by construction), and uniformity (the routing's own bucket populations land within one of the floor/ceiling
    /// share). Mirrors <c>MonotonicPartitionerStage.Sweep</c>.</summary>
    public static string? RoutingIsDeterministicMonotonicAndUniformSurface() {
        const int MaxBucketCount = MonotonicPartitioner.MaxBucketCount;
        const int MaxValueCount = MonotonicPartitioner.MaxValueCount;

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

                var routed = MonotonicPartitioner.GetBucketIdDangerous(value: (ushort)value, bucketCount: bucketCount);

                Assert.True(
                    condition: (routed == owners[value]),
                    userMessage: $"value {value} at bucket count {bucketCount} routed to {routed}; the table-free reference walk says {owners[value]}"
                );
            }

            var floorShare = (MaxValueCount / bucketCount);
            var ceilingShare = ((MaxValueCount + bucketCount - 1) / bucketCount);

            for (var bucket = 0; (bucket < bucketCount); ++bucket) {
                var population = bucketPopulations[bucket];

                Assert.True(
                    condition: ((population >= floorShare) && (population <= ceilingShare)),
                    userMessage: $"bucket {bucket} of {bucketCount} owns {population} values; uniformity requires {floorShare} or {ceilingShare}"
                );
            }
        }

        return null;
    }

    /// <summary>Full-value-domain proof that <see cref="MonotonicPartitioner.GetMetrics(ushort, int)"/> agrees with
    /// an independently derived reference chain-walk — rank, jump count, migration distance and velocity — at bucket
    /// counts that span the fast checkpoint path (1), the slow tail-stream path (100) and the maximum (1024). Mirrors
    /// <c>MonotonicPartitionerStage.CheckMetrics</c>.</summary>
    public static string? MetricsMatchReferenceChainWalkSurface() {
        const int MaxBucketCount = MonotonicPartitioner.MaxBucketCount;
        const int MaxValueCount = MonotonicPartitioner.MaxValueCount;

        foreach (var bucketCount in (ReadOnlySpan<int>)[1, 100, 1024,]) {
            for (var value = 0; (value < MaxValueCount); ++value) {
                var metrics = MonotonicPartitioner.GetMetrics(value: (ushort)value, bucketCount: bucketCount);
                var expectedRank = NormalizedRank(value: value);
                var jumpCount = 0;
                var migrationDistance = 0;
                var owner = 0;
                var remainingRank = expectedRank;

                while (0 != remainingRank) {
                    var nextJump = (((MaxValueCount - 1 - owner) / remainingRank) + 1);

                    if (nextJump > MaxBucketCount) {
                        break;
                    }

                    if ((0 == migrationDistance) && (nextJump > bucketCount)) {
                        migrationDistance = (nextJump - bucketCount);
                    }

                    Advance(jumpAtBucketCount: nextJump, owner: ref owner, remainingRank: ref remainingRank);
                    ++jumpCount;
                }

                var expectedVelocity = ((0 == migrationDistance) ? 0.0f : (1.0f / migrationDistance));

                Assert.True(
                    condition: (
                        (metrics.BucketCount == bucketCount) &&
                        (metrics.JumpCount == jumpCount) &&
                        (metrics.MigrationDistance == migrationDistance) &&
                        (metrics.Rank == expectedRank) &&
                        (metrics.Value == value) &&
                        (metrics.Velocity == expectedVelocity)
                    ),
                    userMessage: $"metrics for value {value} at bucket count {bucketCount} disagree with the reference chain walk: got {metrics}, expected rank {expectedRank}, jumps {jumpCount}, migration {migrationDistance}, velocity {expectedVelocity}"
                );
            }
        }

        return null;
    }

    /// <summary>Pins the <see cref="Guid"/> overloads' exact wire mapping — the documented trailing-entropy hash — as
    /// a protocol contract rather than mere self-agreement between the two internal overloads: an independently
    /// computed little-endian read of bytes 12..15, bias-free widened into [1, 65534], routed through the same
    /// <c>ushort</c> entry points, must reach the same bucket and the same metrics the <see cref="Guid"/> overloads
    /// return. Mirrors <c>MonotonicPartitionerStage.CheckGuidOverloads</c>.</summary>
    public static string? GuidRoutesThroughTrailingEntropyProtocolSurface() {
        Span<byte> guidBytes = stackalloc byte[16];

        for (var sample = 0; (sample < 64); ++sample) {
            for (var i = 0; (i < guidBytes.Length); ++i) {
                guidBytes[i] = ((byte)((sample * 37) + (i * 11) + 5));
            }

            var guid = new Guid(b: guidBytes);
            var entropy = ((uint)(guidBytes[12] | (guidBytes[13] << 8) | (guidBytes[14] << 16) | (guidBytes[15] << 24)));
            var expectedHash = ((ushort)(((65534UL * entropy) >> 32) + 1));

            foreach (var bucketCount in (ReadOnlySpan<int>)[1, 64, 353, 1024,]) {
                Assert.True(
                    condition: (
                        MonotonicPartitioner.GetBucketId(value: guid, bucketCount: bucketCount) ==
                        MonotonicPartitioner.GetBucketId(value: expectedHash, bucketCount: bucketCount)
                    ),
                    userMessage: $"Guid overload disagreed with the independently computed trailing-entropy hash route at sample {sample}, bucket count {bucketCount}"
                );
                Assert.True(
                    condition: (
                        MonotonicPartitioner.GetBucketIdDangerous(value: guid, bucketCount: bucketCount) ==
                        MonotonicPartitioner.GetBucketIdDangerous(value: expectedHash, bucketCount: bucketCount)
                    ),
                    userMessage: $"Guid dangerous overload disagreed with the independently computed trailing-entropy hash route at sample {sample}, bucket count {bucketCount}"
                );
            }

            Assert.Equal(
                expected: MonotonicPartitioner.GetMetrics(value: expectedHash, bucketCount: 353),
                actual: MonotonicPartitioner.GetMetrics(value: guid, bucketCount: 353)
            );
        }

        return null;
    }

    /// <summary>The checked entry points refuse an out-of-range bucket count by throwing
    /// <see cref="ArgumentOutOfRangeException"/> naming <c>bucketCount</c>, rather than routing to a meaningless
    /// index or clamping silently. Mirrors the refusal checks at the end of <c>MonotonicPartitionerStage.Run</c>.</summary>
    public static string? BucketCountOutOfRangeRefusesSurface() {
        Assert.Equal(
            expected: "bucketCount",
            actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => MonotonicPartitioner.GetBucketId(value: (ushort)0, bucketCount: 0)).ParamName
        );
        Assert.Equal(
            expected: "bucketCount",
            actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => MonotonicPartitioner.GetBucketId(value: (ushort)0, bucketCount: (MonotonicPartitioner.MaxBucketCount + 1))).ParamName
        );
        Assert.Equal(
            expected: "bucketCount",
            actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => MonotonicPartitioner.GetBucketId(value: Guid.Empty, bucketCount: 0)).ParamName
        );
        Assert.Equal(
            expected: "bucketCount",
            actual: Assert.Throws<ArgumentOutOfRangeException>(testCode: () => MonotonicPartitioner.GetMetrics(value: (ushort)0, bucketCount: 0)).ParamName
        );

        return null;
    }

    // ---- the table-free reference walk, shared by the two exhaustive claims above ----
    // Transcribed from MonotonicPartitionerStage's own reference (never from MonotonicPartitioner's checkpoint/
    // tail-stream tables): a value migrates exactly when the bucket count reaches its chain's next jump, and it
    // always lands in the youngest bucket, which is what makes this walk monotone by construction.
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
    // natural maximum with 65535 so the top input value owns the top rank. No table, no bitmask — just the formula.
    private static int NormalizedRank(int value) {
        const int NaturalMaxRank = ((int)((65535U * 40503U) & 0xFFFFU));

        var rank = ((int)((((uint)value) * 40503U) & 0xFFFFU));

        return rank switch {
            NaturalMaxRank => 0xFFFF,
            0xFFFF => NaturalMaxRank,
            _ => rank,
        };
    }
}
