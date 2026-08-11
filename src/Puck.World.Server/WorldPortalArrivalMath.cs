using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// The mapped arrival isometry (<c>Puck.World.WorldPlacementPortal.Arrival</c> = Mapped) — the positional-continuity
/// half of a seamless border crossing: a traveler's pose relative to the source portal frame, mapped through the
/// pair's isometry into the destination counterpart's frame, with captured velocity rotated the same way. Pure and
/// fixed-point throughout (no wall-clock, RNG, or float in the computation), so <c>Puck.World.WorldInstanceHost</c>
/// (the composition root, out of reach for <c>Puck.World.Tests</c>) is not needed to prove this math — see that
/// project's own <c>PortalSweepOriginLawTests</c> for the same "prove the primitive, not the orchestration" shape.
/// </summary>
/// <remarks>
/// <para><b>The frame — the face's own seam, not its placement or its center.</b> <c>sourcePosition</c>/
/// <c>destinationPosition</c> are a <c>Puck.World.WorldFaceFrame</c>'s own <c>PointAt(SeamU, SeamV)</c> — the exact
    /// point the traveler's swept segment crossed the source face, and (<c>-SeamU</c>, <c>SeamV</c>) applied to
    /// the destination counterpart's own frame (the mapped image after the horizontal flip, never a fresh sample, since the
/// traveler is not standing at the destination yet). Both frames come from the one derivation
/// (<c>WorldFaceCatalog</c>) the trigger scan, this arrival math, and rendering all read, so scan, arrival, and the
/// drawn door can never disagree about where a face sits, including when a face's shape sits off its placement root
/// or carries its own rotation (<c>WorldInstanceHost.ScanPortalFace</c>/<c>ApplyTransfer</c> callers thread the
/// derived frame and its captured seam coordinates through, never the placement's raw position/yaw). Feeding the
/// frame's <c>Origin</c> instead of its seam lands a traveler at the door's center rather than the point it actually
/// crossed: harmless for a picture-frame portal a player glances through, fatal for contiguous terrain, where an
/// off-center crossing must land at the exact corresponding point for the ground to read as one continuous surface.
/// docs/world-model.md's "Where portal work stands" section carries the same contract.</para>
/// <para><b>The isometry.</b> Source frame F_s = (sourcePosition, sourceYaw); destination frame F_d =
/// (destinationPosition, destinationYaw). A traveler's world pose maps as <c>F_d ∘ Flip ∘ F_s⁻¹</c>, where Flip is a
/// 180° rotation about vertical: walking out of the source face (facing its outward normal) must walk in through the
/// destination face (facing its inward direction) for a crossing to feel like one continuous doorway rather than a
/// disorienting jump. Composed as one delta yaw, since every rotation here is about the same world-up axis (pure
/// yaw rotations about a shared axis commute, so composing three of them collapses to adding three angles):
/// <c>deltaYaw = destinationYaw - sourceYaw + 180°</c>. A same-yaw pair (source and destination frames sharing one
/// yaw) still rotates by exactly 180° — the discriminating case is a different-yaw pair, where a defect that drops
/// the flip or the frame subtraction produces a visibly wrong heading rather than a coincidentally-plausible one.</para>
/// <para><b>Vertical.</b> A rotation about world-up leaves Y untouched, so the mapped position's height is exactly
/// <c>destinationPosition.Y + (travelerPosition.Y - sourcePosition.Y)</c> — height is preserved relative to the
/// source face's own origin, carried straight across to the destination face's origin.</para>
/// <para><b>Velocity.</b> The captured planar velocity rotates by the identical deltaYaw (momentum carries through
/// the door in the mapped direction); vertical velocity is untouched by a world-up rotation, exactly like
/// position's own Y component.</para>
/// <para><b>Reciprocal round-trip error.</b> <c>YawRadians</c> is returned
/// unnormalized and this is by design — nothing downstream needs it reduced to ±π, and <c>Puck.World.Server.WorldBody</c>'s
/// own yaw is already an unbounded accumulator. A consequence: a traveler that crosses A→B then back B→A does not
/// land on exactly its original yaw — it lands on original + one <c>2π</c> increment, because
/// <c>deltaYaw_AB + deltaYaw_BA</c> collapses to exactly <c>2 * s_flipRadians</c> (the authored yaws cancel; fixed-
/// point addition/subtraction is exact), and <c>2 * s_flipRadians</c> differs from a direct
/// <c>FixedQ4816.FromDouble(2π)</c> by exactly one raw unit — two independent single-<c>π</c> roundings doubled land
/// one ULP away from one double-<c>2π</c> rounding. This is accepted: the drift is deterministic (bit-identical every
/// run, never wall-clock or RNG-derived), sub-arcsecond per crossing (one raw unit is 1/65536 radian, ~0.00087°), and
/// repeated round trips accumulate it linearly (N round trips, N raw units of yaw drift) rather than compounding —
/// harmless for an angle nothing normalizes or feeds to a routine requiring it bounded. Position and planar velocity
/// carry their own, larger (but still small, and bounded) round-trip drift from the two composed
/// <see cref="FixedQuaternion.Rotate"/> calls' own SinCos rounding — <c>Puck.World.Tests.WorldPortalArrivalMathLawTests</c>
/// measures and pins explicit budgets for both.</para>
/// </remarks>
public static class WorldPortalArrivalMath {
    // The world-up rotation axis every portal frame rotates about — no pitch/roll ever enters this computation (the
    // same grounded, yaw-only convention WorldInstanceHost.ScanPortalFace and WorldPopulation.ActivateSeat's own
    // spawn pose already assume for a placement's authored transform).
    // The 180° flip folded into every mapped crossing — see this type's own remarks for why walking OUT of the
    // source face must walk IN through the destination face. A fixed-point constant, computed once via the SAME
    // FromDouble(degrees * pi/180) boundary every other authored yaw in this engine crosses through
    // (WorldPlacementAttachment.TryResolve, WorldPopulation's own spawn-pose conversion).

    /// <summary>Returns the destination frame point corresponding to a source crossing coordinate. The isometry's
    /// 180-degree flip reverses the horizontal face axis, so <paramref name="seamU"/> changes sign while vertical
    /// <paramref name="seamV"/> does not.</summary>
    /// <param name="destinationFrame">The destination counterpart's face frame.</param>
    /// <param name="seamU">The source crossing coordinate along its face's right axis.</param>
    /// <param name="seamV">The source crossing coordinate along its face's up axis.</param>
    /// <returns>The corresponding destination seam point.</returns>
    public static FixedVector3 CounterpartSeam(in WorldFaceFrame destinationFrame, FixedQ4816 seamU, FixedQ4816 seamV) =>
        destinationFrame.PointAt(u: -seamU, v: seamV);

    /// <summary>One mapped arrival: a traveler's pose and velocity, computed at the source, mapped into the
    /// destination's own frame.</summary>
    /// <param name="Position">The mapped world position.</param>
    /// <param name="YawRadians">The mapped world yaw, fixed-point radians.</param>
    /// <param name="PlanarVelocity">The rotated planar velocity.</param>
    /// <param name="VerticalVelocity">The (rotation-invariant) vertical velocity.</param>
    public readonly record struct Arrival(FixedVector3 Position, FixedQ4816 YawRadians, FixedVector3 PlanarVelocity, FixedQ4816 VerticalVelocity);

    /// <summary>Computes a mapped arrival — see this type's own remarks for the isometry.</summary>
    /// <param name="travelerPosition">The traveler's captured world position at the source, fixed point.</param>
    /// <param name="travelerYawRadians">The traveler's captured world yaw at the source, fixed-point radians.</param>
    /// <param name="travelerPlanarVelocity">The traveler's captured planar velocity at the source.</param>
    /// <param name="travelerVerticalVelocity">The traveler's captured vertical velocity at the source.</param>
    /// <param name="sourcePosition">The source face's own SEAM point (F_s) — <c>WorldFaceFrame.PointAt(SeamU, SeamV)</c>
    /// at the crossing, fixed point. Not the frame's <c>Origin</c>: see this type's own remarks.</param>
    /// <param name="sourceYawRadians">The source face's own frame heading (F_s), fixed-point radians.</param>
    /// <param name="destinationPosition">The destination counterpart face's own seam point (F_d) —
    /// (-SeamU, SeamV) applied to the counterpart's own frame, fixed point.</param>
    /// <param name="destinationYawRadians">The destination counterpart face's own frame heading (F_d), fixed-point radians.</param>
    /// <returns>The mapped arrival.</returns>
    public static Arrival ComputeArrival(
        FixedVector3 travelerPosition,
        FixedQ4816 travelerYawRadians,
        FixedVector3 travelerPlanarVelocity,
        FixedQ4816 travelerVerticalVelocity,
        FixedVector3 sourcePosition,
        FixedQ4816 sourceYawRadians,
        FixedVector3 destinationPosition,
        FixedQ4816 destinationYawRadians
    ) {
        var deltaYaw = WorldFrameIsometry.RotationDelta(sourceYaw: sourceYawRadians, destinationYaw: destinationYawRadians);
        var relativePosition = (travelerPosition - sourcePosition);
        var offset = WorldFrameIsometry.Rotate(value: relativePosition, deltaYaw: deltaYaw);
        var planarVelocity = WorldFrameIsometry.Rotate(value: travelerPlanarVelocity, deltaYaw: deltaYaw);

        return new Arrival(
            Position: (destinationPosition + offset),
            YawRadians: (travelerYawRadians + deltaYaw),
            PlanarVelocity: planarVelocity,
            VerticalVelocity: travelerVerticalVelocity
        );
    }
}
