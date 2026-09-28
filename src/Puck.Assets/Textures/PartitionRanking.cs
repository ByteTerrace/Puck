namespace Puck.Assets.Textures;

/// <summary>The partition ranking the partitioned block encoders share: which of the format's partitions (the BC7
/// tables, whose two-subset table BC6H's two-region modes use too) split a block into the subsets that vary least. The
/// ranking is exact integer arithmetic, so it is the same on every machine.</summary>
internal static class PartitionRanking {
    // The least common multiple of 1 to 16: a subset's variance times it is an integer for any member count.
    private const long VarianceScale = 720720L;

    // Writes into `ranked` the first `partitions` partitions of the `subsets`-subset table whose subsets vary least over
    // the first `channels` channels of sixteen texels `stride` values apart, least first, the lower partition number first
    // among equals, and returns how many it wrote. A subset's variation is n Σ x² - (Σ x)² summed over channels, times
    // 720720 / n: its variance times 720720, exactly.
    public static int Rank(ReadOnlySpan<int> values, int stride, int channels, int subsets, int partitions, Span<int> ranked) {
        Span<long> scores = stackalloc long[ranked.Length];
        var count = 0;

        for (var partition = 0; (partition < partitions); partition++) {
            var score = 0L;

            for (var subset = 0; (subset < subsets); subset++) {
                var members = 0L;
                var residual = 0L;

                for (var channel = 0; (channel < channels); channel++) {
                    var sum = 0L;
                    var squares = 0L;

                    members = 0L;

                    for (var texel = 0; (texel < 16); texel++) {
                        if (Bc7Codec.SubsetOf(partition: partition, subsets: subsets, texel: texel) == subset) {
                            var value = values[((texel * stride) + channel)];

                            members++;
                            sum += value;
                            squares += (((long)value) * value);
                        }
                    }

                    residual += ((members * squares) - (sum * sum));
                }

                score += ((VarianceScale / members) * residual);
            }

            var at = count;

            while (
                (at > 0) &&
                (score < scores[(at - 1)])
            ) {
                at--;
            }

            if (at >= ranked.Length) {
                continue;
            }

            for (var slot = (Math.Min(val1: count, val2: (ranked.Length - 1)) - 1); (slot >= at); slot--) {
                scores[(slot + 1)] = scores[slot];
                ranked[(slot + 1)] = ranked[slot];
            }

            scores[at] = score;
            ranked[at] = partition;
            count = Math.Min(val1: (count + 1), val2: ranked.Length);
        }

        return count;
    }
}
