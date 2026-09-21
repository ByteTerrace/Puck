using System.Diagnostics.CodeAnalysis;
using Puck.Maths;

namespace Puck.State;

/// <summary>Exact integer vector transform kernels for mixing, mean, nearest search, and remember.</summary>
public static class VectorTransforms {
    /// <summary>A ranked match from a nearest search.</summary>
    /// <param name="Key">The candidate cell key.</param>
    /// <param name="Score">The similarity score (dot or Q48.16 cosine).</param>
    public readonly record struct NearestMatch(CellName Key, long Score);

    /// <summary>Writes the normalized weighted sum of 1 to 8 vector terms.</summary>
    /// <param name="vectors">The component spans of each vector term.</param>
    /// <param name="weights">The integer weights for each term ([-1000, 1000], non-zero).</param>
    /// <param name="destination">The destination component span, matching the vectors' dimensions.</param>
    /// <param name="sum">Working storage for the weighted sum, at least as wide as
    /// <paramref name="destination"/>.</param>
    /// <param name="refusal">The refusal reason if the operation fails; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> on success; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sum"/> is narrower than
    /// <paramref name="destination"/>.</exception>
    public static bool TryMix(
        ReadOnlySpan<ReadOnlyMemory<sbyte>> vectors,
        ReadOnlySpan<int> weights,
        Span<sbyte> destination,
        Span<long> sum,
        [NotNullWhen(false)] out RuleRefusal? refusal
    ) {
        if ((vectors.Length == 0) || (vectors.Length > StateCapacity.MaxMixTerms) || (vectors.Length != weights.Length)) {
            refusal = RuleRefusal.VectorMixTerms;

            return false;
        }

        var dimensions = destination.Length;

        for (var termIndex = 0; termIndex < vectors.Length; termIndex++) {
            if (vectors[termIndex].Length != dimensions) {
                refusal = RuleRefusal.VectorSpaceMismatch;

                return false;
            }

            var weight = weights[termIndex];

            if ((weight < -StateCapacity.MaxMixWeight) || (weight > StateCapacity.MaxMixWeight) || (weight == 0)) {
                refusal = RuleRefusal.VectorMixTerms;

                return false;
            }
        }

        if (sum.Length < dimensions) {
            throw new ArgumentException(
                message: $"The working storage holds {sum.Length} sums where the destination has {dimensions} dimensions.",
                paramName: nameof(sum)
            );
        }

        sum = sum[..dimensions];
        sum.Clear();

        for (var termIndex = 0; termIndex < vectors.Length; termIndex++) {
            var vector = vectors[termIndex].Span;
            long weight = weights[termIndex];

            for (var dimensionIndex = 0; dimensionIndex < dimensions; dimensionIndex++) {
                sum[dimensionIndex] += weight * vector[dimensionIndex];
            }
        }

        var isAllZero = true;

        for (var dimensionIndex = 0; dimensionIndex < dimensions; dimensionIndex++) {
            if (sum[dimensionIndex] != 0L) {
                isAllZero = false;

                break;
            }
        }

        if (isAllZero) {
            refusal = RuleRefusal.VectorMixZero;

            return false;
        }

        if (!SignedByteVectorFunctions.TryNormalize(components: sum, destination: destination)) {
            refusal = RuleRefusal.VectorMixZero;

            return false;
        }

        refusal = null;

        return true;
    }

    /// <summary>Writes the normalized centroid (mean) of candidate vectors.</summary>
    /// <param name="candidates">The candidate vector component spans.</param>
    /// <param name="destination">The destination component span.</param>
    /// <param name="sum">Working storage for the component sums, at least as wide as
    /// <paramref name="destination"/>.</param>
    /// <param name="refusal">The refusal reason if the operation fails; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> on success; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="sum"/> is narrower than
    /// <paramref name="destination"/>.</exception>
    public static bool TryMean(
        ReadOnlySpan<ReadOnlyMemory<sbyte>> candidates,
        Span<sbyte> destination,
        Span<long> sum,
        [NotNullWhen(false)] out RuleRefusal? refusal
    ) {
        if (candidates.Length == 0) {
            refusal = RuleRefusal.VectorMeanEmpty;

            return false;
        }

        var dimensions = destination.Length;

        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++) {
            if (candidates[candidateIndex].Length != dimensions) {
                refusal = RuleRefusal.VectorSpaceMismatch;

                return false;
            }
        }

        if (sum.Length < dimensions) {
            throw new ArgumentException(
                message: $"The working storage holds {sum.Length} sums where the destination has {dimensions} dimensions.",
                paramName: nameof(sum)
            );
        }

        sum = sum[..dimensions];
        sum.Clear();

        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++) {
            var candidate = candidates[candidateIndex].Span;

            for (var dimensionIndex = 0; dimensionIndex < dimensions; dimensionIndex++) {
                sum[dimensionIndex] += candidate[dimensionIndex];
            }
        }

        var isAllZero = true;

        for (var dimensionIndex = 0; dimensionIndex < dimensions; dimensionIndex++) {
            if (sum[dimensionIndex] != 0L) {
                isAllZero = false;

                break;
            }
        }

        if (isAllZero) {
            refusal = RuleRefusal.VectorMeanEmpty;

            return false;
        }

        if (!SignedByteVectorFunctions.TryNormalize(components: sum, destination: destination)) {
            refusal = RuleRefusal.VectorMeanEmpty;

            return false;
        }

        refusal = null;

        return true;
    }

    /// <summary>Selects the top-K nearest or farthest matches from candidate vectors using a bounded buffer.</summary>
    public static int SelectNearest(
        ReadOnlySpan<NearestCandidate> candidates,
        ReadOnlySpan<sbyte> query,
        bool isFixedScore,
        int k,
        long? threshold,
        CellName? excludeKey,
        bool farthest,
        Span<NearestMatch> results
    ) {
        if ((k <= 0) || (results.Length < k)) {
            return 0;
        }

        var count = 0;

        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++) {
            ref readonly var candidate = ref candidates[candidateIndex];

            if (excludeKey.HasValue && (candidate.Key == excludeKey.Value)) {
                continue;
            }

            if (!candidate.Admitted) {
                continue;
            }

            var score = (isFixedScore
                ? SignedByteVectorFunctions.CosineQ16(left: candidate.Components.Span, right: query)
                : SignedByteVectorFunctions.Dot(left: candidate.Components.Span, right: query)
            );

            if (threshold.HasValue) {
                if (!farthest && (score < threshold.Value)) {
                    continue;
                }

                if (farthest && (score > threshold.Value)) {
                    continue;
                }
            }

            InsertRanked(
                results: results,
                count: ref count,
                capacity: k,
                farthest: farthest,
                item: new NearestMatch(Key: candidate.Key, Score: score)
            );
        }

        return count;
    }

    private static void InsertRanked(
        Span<NearestMatch> results,
        ref int count,
        int capacity,
        bool farthest,
        NearestMatch item
    ) {
        var pos = 0;

        while (pos < count) {
            var cmp = CompareMatches(a: item, b: results[pos], farthest: farthest);

            if (cmp < 0) {
                break;
            }

            pos++;
        }

        if (pos >= capacity) {
            return;
        }

        var shiftCount = Math.Min(val1: count, val2: (capacity - 1)) - pos;

        if (shiftCount > 0) {
            results.Slice(start: pos, length: shiftCount)
                .CopyTo(destination: results.Slice(start: pos + 1, length: shiftCount));
        }

        results[pos] = item;

        if (count < capacity) {
            count++;
        }
    }

    private static int CompareMatches(NearestMatch a, NearestMatch b, bool farthest) {
        // Return < 0 if a ranks before b, > 0 if a ranks after b.
        // Primary: score
        if (a.Score != b.Score) {
            if (!farthest) {
                // Higher score ranks first
                return (a.Score > b.Score) ? -1 : 1;
            }

            // Lower score ranks first
            return (a.Score < b.Score) ? -1 : 1;
        }

        // Secondary tie-break: Key in ordinal order (ascending)
        return string.CompareOrdinal(strA: a.Key.Value, strB: b.Key.Value);
    }

    /// <summary>Checks whether a vector can be remembered into a table, ensuring no existing cell is within the cosine similarity threshold.</summary>
    /// <param name="existingCells">The existing cells in the destination table.</param>
    /// <param name="key">The key of the vector to remember.</param>
    /// <param name="vector">The vector components to remember.</param>
    /// <param name="unlessWithinQ16">The cosine similarity threshold in Q48.16.</param>
    /// <param name="matchingKey">The key of the matching cell if one is within threshold; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the vector can be stored; <see langword="false"/> if a near-duplicate was found.</returns>
    public static bool TryRemember(
        ReadOnlySpan<NearestCandidate> existingCells,
        CellName key,
        ReadOnlySpan<sbyte> vector,
        long unlessWithinQ16,
        out CellName? matchingKey
    ) {
        for (var index = 0; index < existingCells.Length; index++) {
            ref readonly var candidate = ref existingCells[index];

            if (candidate.Key == key) {
                continue;
            }

            var cosine = SignedByteVectorFunctions.CosineQ16(left: candidate.Components.Span, right: vector);

            if (cosine >= unlessWithinQ16) {
                matchingKey = candidate.Key;

                return false;
            }
        }

        matchingKey = null;

        return true;
    }
}

/// <summary>A candidate vector cell passed to vector search or transform kernels.</summary>
/// <param name="Key">The candidate cell key.</param>
/// <param name="Components">The vector components.</param>
/// <param name="Admitted">Whether this candidate passes the where filter.</param>
public readonly record struct NearestCandidate(CellName Key, ReadOnlyMemory<sbyte> Components, bool Admitted = true);
