namespace Puck.Physics;

/// <summary>The insertion sort shared by every contact-candidate list's <c>Canonicalize</c>: an explicit sort rather
/// than a library one, because the ordering is part of a determinism law's contract, so it is written where it can
/// be read; its cost is irrelevant at manifold sizes.</summary>
internal static class FixedContactCanonicalization {
    /// <summary>Sorts a candidate list into canonical order in place.</summary>
    /// <param name="candidates">The candidates to order.</param>
    /// <param name="compare">The total-key comparer two equal-comparing candidates must be bitwise identical under.</param>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> or <paramref name="compare"/> is
    /// <see langword="null"/>.</exception>
    internal static void InsertionSort<T>(List<T> candidates, Comparison<T> compare) {
        ArgumentNullException.ThrowIfNull(argument: candidates);
        ArgumentNullException.ThrowIfNull(argument: compare);

        for (var index = 1; (index < candidates.Count); ++index) {
            var current = candidates[index];
            var slot = (index - 1);

            while (
                (slot >= 0) &&
                (compare(
                x: candidates[slot],
                y: current
            ) > 0)
            ) {
                candidates[(slot + 1)] = candidates[slot];
                --slot;
            }

            candidates[(slot + 1)] = current;
        }
    }
}
