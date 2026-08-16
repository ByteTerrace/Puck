using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// One two-body contact CANDIDATE: a witness that two bodies (identified by their <see cref="FixedRigidWorld"/> ids)
/// may be touching, carrying an anchor on each side, the shared normal, and the separation. A candidate is not yet a
/// constraint — <see cref="FixedPairManifoldSlotTable"/> decides which persistent slot it lands in.
/// </summary>
/// <param name="BodyIdA">The id of the body the normal points away from.</param>
/// <param name="BodyIdB">The id of the body the normal points toward.</param>
/// <param name="AnchorA">The contact point relative to body A's centre of mass, world axes.</param>
/// <param name="AnchorB">The contact point relative to body B's centre of mass, world axes.</param>
/// <param name="Normal">The unit contact normal, world axes, pointing from A toward B.</param>
/// <param name="Separation">The signed gap; negative means overlapping.</param>
/// <param name="SourceId">The identity of the generator that produced the candidate.</param>
/// <param name="FeatureId">The identity of the contact feature within that generator's output.</param>
public readonly record struct FixedTwoBodyContact(
    int BodyIdA,
    int BodyIdB,
    FixedVector3 AnchorA,
    FixedVector3 AnchorB,
    FixedVector3 Normal,
    FixedQ4816 Separation,
    int SourceId,
    int FeatureId
) {
    /// <summary>Sorts a candidate list into canonical order in place.</summary>
    /// <param name="candidates">The candidates to order.</param>
    /// <remarks>An explicit insertion sort rather than a library sort, matching <see cref="FixedContactCandidate.Canonicalize"/>:
    /// the ordering is part of the contract a permutation law proves, so it is written where it can be read, and its
    /// cost is irrelevant at manifold sizes.</remarks>
    public static void Canonicalize(List<FixedTwoBodyContact> candidates) {
        ArgumentNullException.ThrowIfNull(argument: candidates);

        for (var index = 1; (index < candidates.Count); ++index) {
            var current = candidates[index];
            var slot = (index - 1);

            while (
                (slot >= 0) &&
                (Compare(
                left: candidates[slot],
                right: current
            ) > 0)
            ) {
                candidates[(slot + 1)] = candidates[slot];
                --slot;
            }

            candidates[(slot + 1)] = current;
        }
    }
    /// <summary>Compares two candidates on a TOTAL key: the canonically-ordered body id pair first, then source,
    /// feature, normal, separation, and both anchors, each read as a raw carrier word.</summary>
    /// <param name="left">The first candidate.</param>
    /// <param name="right">The second candidate.</param>
    /// <returns>A negative value, zero, or a positive value as <paramref name="left"/> orders before, with, or after
    /// <paramref name="right"/>.</returns>
    /// <remarks>The key covers every declared field — including <see cref="AnchorB"/>, which a key stopping at the
    /// body ids would leave uncompared, letting two candidates differing only in <see cref="AnchorB"/> compare equal
    /// and hand the insertion sort's stability the last word on their order instead of the key.</remarks>
    public static int Compare(FixedTwoBodyContact left, FixedTwoBodyContact right) {
        var leftMin = Math.Min(
            val1: left.BodyIdA,
            val2: left.BodyIdB
        );
        var leftMax = Math.Max(
            val1: left.BodyIdA,
            val2: left.BodyIdB
        );
        var rightMin = Math.Min(
            val1: right.BodyIdA,
            val2: right.BodyIdB
        );
        var rightMax = Math.Max(
            val1: right.BodyIdA,
            val2: right.BodyIdB
        );
        var order = leftMin.CompareTo(value: rightMin);

        if (order != 0) { return order; }

        order = leftMax.CompareTo(value: rightMax);

        if (order != 0) { return order; }

        order = left.SourceId.CompareTo(value: right.SourceId);

        if (order != 0) { return order; }

        order = left.FeatureId.CompareTo(value: right.FeatureId);

        if (order != 0) { return order; }

        order = left.Normal.X.Value.CompareTo(value: right.Normal.X.Value);

        if (order != 0) { return order; }

        order = left.Normal.Y.Value.CompareTo(value: right.Normal.Y.Value);

        if (order != 0) { return order; }

        order = left.Normal.Z.Value.CompareTo(value: right.Normal.Z.Value);

        if (order != 0) { return order; }

        order = left.Separation.Value.CompareTo(value: right.Separation.Value);

        if (order != 0) { return order; }

        order = left.AnchorA.X.Value.CompareTo(value: right.AnchorA.X.Value);

        if (order != 0) { return order; }

        order = left.AnchorA.Y.Value.CompareTo(value: right.AnchorA.Y.Value);

        if (order != 0) { return order; }

        order = left.AnchorA.Z.Value.CompareTo(value: right.AnchorA.Z.Value);

        if (order != 0) { return order; }

        order = left.AnchorB.X.Value.CompareTo(value: right.AnchorB.X.Value);

        if (order != 0) { return order; }

        order = left.AnchorB.Y.Value.CompareTo(value: right.AnchorB.Y.Value);

        if (order != 0) { return order; }

        return left.AnchorB.Z.Value.CompareTo(value: right.AnchorB.Z.Value);
    }
}
