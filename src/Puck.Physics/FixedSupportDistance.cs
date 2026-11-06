using Puck.Maths;

namespace Puck.Physics;

/// <summary>How far a body may descend along its up axis before one collider first comes within the contact skin of
/// it: the support distance a ground snap lands by (<see cref="FixedStepUp.TrySnap{TStep}"/>), read from the collider's
/// own push query rather than searched for with whole sweeps.</summary>
/// <remarks>
/// <para><b>Convex colliders.</b> Every kind but <see cref="FixedStaticColliderKind.Field"/> is convex, as is every body
/// volume, and the separation between two convex solids is a convex function of a straight translation of one of them.
/// The push query with its skin padded by the remaining reach measures that separation exactly (its penetration is the
/// padded skin less the separation) and its normal is the separation's direction, so the separation falls along the
/// descent at the rate <c>normal · up</c>. Advancing by the separation left over the skin divided by that rate is a
/// Newton step from below on a convex function: it never passes the first touching descent, it reaches a flat face's in
/// one step, and it closes on a rounded edge's or a sphere's in a few. A collider the padded query does not reach, or
/// whose separation does not fall along the descent, is never met within the reach.</para>
/// <para><b>Field colliders.</b> A field's solid is not convex and its solve measures no separation along a direction,
/// so a field collider's first push is bracketed by halving the reach against that collider's push alone, to within the
/// reach over <c>2^</c><see cref="FieldProbes"/>.</para>
/// <para>Either way the answer only proposes where the body lands: the caller's landing sweep proves the support and
/// decides whether the body stands on it.</para>
/// </remarks>
public static class FixedSupportDistance {
    /// <summary>The halvings a field collider's bracket spends.</summary>
    public const int FieldProbes = 8;
    /// <summary>The most Newton steps one convex collider spends: a flat face takes one and a confirming query, and a
    /// curved feature converges well within this.</summary>
    public const int ConvexSteps = 16;

    // A step this short has converged: the touching descent lies within it.
    private static readonly FixedQ4816 Converged = FixedQ4816.FromRawBits(value: 4L);
    // The rounding a padded query carries: the reach is padded by this, so a collider exactly the reach away is still
    // measured.
    private static readonly FixedQ4816 PaddingSlack = FixedQ4816.FromRawBits(value: 4L);
    private static readonly FixedQ4816 Two = FixedQ4816.FromInteger(value: 2L);

    /// <summary>Finds the least descent along <paramref name="up"/>, within <paramref name="reach"/>, at which a body
    /// volume first comes within <paramref name="skin"/> of a collider.</summary>
    /// <param name="collider">The collider.</param>
    /// <param name="position">The body's foot point before the descent.</param>
    /// <param name="orientation">The body's orientation.</param>
    /// <param name="volume">The body-local volume.</param>
    /// <param name="skin">The contact skin.</param>
    /// <param name="up">The body's unit up axis; the descent runs along its opposite.</param>
    /// <param name="reach">The greatest descent searched, positive.</param>
    /// <param name="descent">The descent at which the volume first comes within the skin: zero when it already is,
    /// and never past the touching descent for a convex collider.</param>
    /// <returns><see langword="true"/> when the collider is met within the reach.</returns>
    public static bool TryDescend(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) =>
        ((collider.Kind == FixedStaticColliderKind.Field)
            ? TryBracket(collider: in collider, descent: out descent, orientation: in orientation, position: position,
                reach: reach, skin: skin, up: in up, volume: in volume)
            : TryAdvance(collider: in collider, descent: out descent, orientation: in orientation, position: position,
                reach: reach, skin: skin, up: in up, volume: in volume));

    // Newton from below on the convex separation: see the type's remarks.
    private static bool TryAdvance(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) {
        descent = FixedQ4816.Zero;

        for (var step = 0; (step < ConvexSteps); step++) {
            var padded = ((skin + (reach - descent)) + PaddingSlack);
            var pushes = collider.TryGetPush(orientation: in orientation, position: (position - (up * descent)), push: out var push,
                skin: padded, uncertain: out _, volume: in volume);

            FixedStaticContactWork.Process.CountQuery(pushed: pushes);
            if (!pushes) {
                return false;
            }
            // The separation is the padded skin less the penetration; what lies over the skin is still to descend.
            var gap = ((padded - push.Penetration) - skin);

            if (gap <= FixedQ4816.Zero) {
                return true;
            }
            var rate = FixedVector3.Dot(left: push.Normal, right: up);

            if (rate <= FixedQ4816.Zero) {
                return false;
            }
            var advance = (gap / rate);

            descent += advance;
            if (descent > reach) {
                return false;
            }
            if (advance <= Converged) {
                return true;
            }
        }
        return true;
    }
    // A field collider's first push, bracketed against that collider alone: see the type's remarks.
    private static bool TryBracket(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
        in FixedBodyColliderVolume volume, FixedQ4816 skin, in FixedVector3 up, FixedQ4816 reach, out FixedQ4816 descent) {
        descent = reach;
        if (!Pushes(collider: in collider, descent: reach, orientation: in orientation, position: position, skin: skin, up: in up, volume: in volume)) {
            return false;
        }
        var clear = FixedQ4816.Zero;

        for (var probe = 0; (probe < FieldProbes); probe++) {
            var middle = ((clear + descent) / Two);

            if (Pushes(collider: in collider, descent: middle, orientation: in orientation, position: position, skin: skin, up: in up, volume: in volume)) {
                descent = middle;
            } else {
                clear = middle;
            }
        }
        return true;

        static bool Pushes(in FixedStaticCollider collider, FixedVector3 position, in FixedQuaternion orientation,
            in FixedBodyColliderVolume volume, FixedQ4816 skin, in FixedVector3 up, FixedQ4816 descent) {
            var pushes = collider.TryGetPush(orientation: in orientation, position: (position - (up * descent)), push: out _,
                skin: skin, uncertain: out _, volume: in volume);

            FixedStaticContactWork.Process.CountQuery(pushed: pushes);
            return pushes;
        }
    }
}
